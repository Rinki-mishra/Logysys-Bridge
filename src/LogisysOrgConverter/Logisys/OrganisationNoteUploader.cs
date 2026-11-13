using System.Text.RegularExpressions;
using LogisysOrgConverter.Conversion;
using LogisysOrgConverter.Data;
using LogisysOrgConverter.GoogleDrive;
using LogisysOrgConverter.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace LogisysOrgConverter.Logisys;

public sealed record NoteUploadSummary(int Organisations, int Attached, int Skipped, int Failed, string Message);

public sealed class OrganisationNoteUploader
{
    private static readonly Regex NonAlpha = new("[^A-Z0-9]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly AppDbContext _db;
    private readonly LogisysBrowserSession _browser;
    private readonly LogisysOptions _options;
    private readonly GoogleDriveAccess _drive;
    private readonly GoogleBrowserSession _googleBrowser;
    private readonly ILogger<OrganisationNoteUploader> _logger;

    public OrganisationNoteUploader(
        AppDbContext db,
        LogisysBrowserSession browser,
        IOptions<LogisysOptions> options,
        GoogleDriveAccess drive,
        GoogleBrowserSession googleBrowser,
        ILogger<OrganisationNoteUploader> logger)
    {
        _db = db;
        _browser = browser;
        _options = options.Value;
        _drive = drive;
        _googleBrowser = googleBrowser;
        _logger = logger;
    }

    public Task<NoteUploadSummary> AttachAsync(Guid runId, CancellationToken cancellationToken) =>
        AttachAsync(runId, onlyName: null, cancellationToken);

    public async Task<NoteUploadSummary> AttachAsync(Guid runId, string? onlyName, CancellationToken cancellationToken)
    {
        // Only Created orgs — Status=Update matches every existing Softlink org on the form
        // (1000+ rows / 4000+ Drive files) and Attach never finishes.
        var query = _db.RunRows
            .Where(row => row.RunId == runId
                && row.DocumentsJson != null
                && row.DocumentsJson != "[]"
                && row.ImportStatus == ImportStatuses.Created
                && row.NotesUploadStatus != "Attached");

        List<ConversionRunRow> rows;
        if (!string.IsNullOrWhiteSpace(onlyName))
        {
            var key = NonAlpha.Replace(onlyName.ToUpperInvariant(), string.Empty);
            var all = await query.OrderBy(row => row.SourceRowNumber).ToListAsync(cancellationToken);
            rows = all.Where(row => NonAlpha.Replace(row.OrganisationName.ToUpperInvariant(), string.Empty)
                .Contains(key, StringComparison.Ordinal)).ToList();
        }
        else
        {
            rows = await query.OrderBy(row => row.SourceRowNumber).ToListAsync(cancellationToken);
        }

        if (rows.Count == 0)
        {
            return new NoteUploadSummary(0, 0, 0, 0,
                string.IsNullOrWhiteSpace(onlyName)
                    ? "No Created organisations with form documents are waiting for Notes. Mark the Logi-Sys import result first (Created), then Attach."
                    : $"No Created organisation matching \"{onlyName.Trim()}\" with documents waiting for Notes.");
        }

        var useApi = _drive.IsConfigured && await _drive.GetConnectionAsync(cancellationToken) is not null;
        if (!useApi && !_googleBrowser.HasSignedIn)
        {
            return new NoteUploadSummary(rows.Count, 0, 0, rows.Count,
                "Google Drive files cannot be downloaded yet. Open Load spreadsheet from mail once (so GoogleSession is signed in), or connect Google Drive, then Attach again.");
        }

        // Download all Drive files in one GoogleSession pass, then Softlink Notes upload.
        // (Opening Edge once per file was why Attach sat on "downloading Drive files".)
        // Softlink Notes: every Drive document linked on the form row (KYC, PAN, GST, TAN, IEC, etc.).
        var rowDocs = rows
            .Select(row => (Row: row, Documents: FormDocuments.Read(row.DocumentsJson)))
            .Where(item => item.Documents.Count > 0)
            .ToList();
        var fileIds = rowDocs
            .SelectMany(item => item.Documents)
            .Select(doc => GoogleDriveAccess.FileId(doc.Link))
            .Where(id => id is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        _logger.LogInformation("Attach: downloading {Count} Google Drive file(s) for {Orgs} organisation(s).", fileIds.Count, rowDocs.Count);

        Dictionary<string, (string Name, string MimeType, byte[] Content)> downloaded = new(StringComparer.Ordinal);
        if (fileIds.Count > 0)
        {
            if (useApi)
            {
                foreach (var fileId in fileIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var file = await _drive.DownloadAsync(fileId, cancellationToken);
                        downloaded[fileId] = (file.Name, file.MimeType, file.Content);
                    }
                    catch (Exception ex) when (ex is GoogleDriveException or HttpRequestException or TaskCanceledException or TimeoutException)
                    {
                        _logger.LogWarning(ex, "Could not download Drive file {FileId} via API.", fileId);
                    }
                }
            }
            else
            {
                foreach (var pair in await _googleBrowser.DownloadDriveFilesAsync(fileIds, cancellationToken))
                {
                    downloaded[pair.Key] = pair.Value;
                }
            }
        }

        var prepared = new List<(ConversionRunRow Row, List<(FormDocument Doc, string Path)> Files)>();
        var skipped = 0;
        var failed = 0;
        foreach (var (row, documents) in rowDocs)
        {
            var files = new List<(FormDocument Doc, string Path)>();
            foreach (var document in documents)
            {
                var fileId = GoogleDriveAccess.FileId(document.Link);
                if (fileId is null || !downloaded.TryGetValue(fileId, out var file))
                {
                    skipped++;
                    continue;
                }

                if (file.Content.Length > _options.MaxAttachmentBytes)
                {
                    _logger.LogWarning("{Label} is {Size} bytes; Softlink allows {Max}.", document.Label, file.Content.Length, _options.MaxAttachmentBytes);
                    skipped++;
                    continue;
                }

                var extension = ExtensionFor(file.Name, file.MimeType);
                var path = Path.Combine(Path.GetTempPath(), "logisys-note-" + Guid.NewGuid().ToString("N") + extension);
                await File.WriteAllBytesAsync(path, file.Content, cancellationToken);
                files.Add((document, path));
            }

            if (files.Count == 0)
            {
                row.NotesUploadStatus = "Failed";
                row.NotesUploadMessage = "No document could be downloaded from Google Drive (or each file was over Softlink's 2 MB limit).";
                failed++;
                await _db.SaveChangesAsync(cancellationToken);
                continue;
            }

            prepared.Add((row, files));
        }

        if (prepared.Count == 0)
        {
            return new NoteUploadSummary(rows.Count, 0, skipped, failed,
                $"No documents ready to attach (skipped {skipped}, failed {failed}). Softlink Notes was not opened. Files over 2 MB cannot be attached; Load spreadsheet from mail if Drive downloads failed.");
        }

        _logger.LogInformation("Attach: {Ready} organisation(s) ready for Softlink Notes upload.", prepared.Count);

        return await _browser.RunAsync(async (page, token) =>
        {
            var attached = 0;
            try
            {
                foreach (var (row, files) in prepared)
                {
                    token.ThrowIfCancellationRequested();
                    page = await LogisysBrowserSession.EnsureLivePageAsync(page);
                    if (await LogisysBrowserSession.IsLoginPageAsync(page))
                    {
                        foreach (var (_, path) in files)
                        {
                            TryDelete(path);
                        }

                        throw new LogisysException(
                            "Softlink returned to Login after you signed in (session dropped). In the open Edge window type Security Code, Log In, Submit for Gurugram, leave Edge open, then Attach again immediately.");
                    }

                    try
                    {
                        page = await LogisysBrowserSession.EnsureLivePageAsync(page);
                        await DismissOpenNoteDialogsAsync(page);
                        var master = await SoftlinkOrgNavigation.OpenOrganisationAsync(
                            page, _browser.BaseUrl, row.OrganisationName, token);
                        master = await SoftlinkOrgNavigation.FindOrganizationMasterPageAsync(master, row.OrganisationName)
                            ?? master;
                        master.SetDefaultTimeout(60_000);
                        await master.BringToFrontAsync();
                        await DismissOpenNoteDialogsAsync(master);
                        await OpenNotesTabAsync(master);
                        foreach (var (document, path) in files)
                        {
                            try
                            {
                                await AddNoteWithAttachmentAsync(master, document.Label, path);
                                attached++;
                            }
                            finally
                            {
                                TryDelete(path);
                            }
                        }

                        row.NotesUploadStatus = "Attached";
                        row.NotesUploadMessage =
                            $"{files.Count} note(s) attached ({string.Join(", ", files.Select(f => f.Doc.Label))}) at {DateTime.UtcNow:u}.";
                        await TryCloseOrgMasterAsync(master);
                        // Always drop the Master tab so the next organisation cannot reuse it.
                        await SoftlinkOrgNavigation.CloseOrgMasterPagesAsync(page.Context);
                        page = await LogisysBrowserSession.EnsureLivePageAsync(page);
                    }
                    catch (Exception ex) when (ex is LogisysException or PlaywrightException or GoogleDriveException or TimeoutException)
                    {
                        foreach (var (_, path) in files)
                        {
                            TryDelete(path);
                        }

                        var onLogin = false;
                        try
                        {
                            page = await LogisysBrowserSession.EnsureLivePageAsync(page);
                            await DismissOpenNoteDialogsAsync(page);
                            onLogin = ex.Message.Contains("signed out", StringComparison.OrdinalIgnoreCase)
                                || ex.Message.Contains("Login", StringComparison.OrdinalIgnoreCase)
                                || await LogisysBrowserSession.IsLoginPageAsync(page);
                        }
                        catch (PlaywrightException)
                        {
                        }

                        if (onLogin)
                        {
                            failed++;
                            row.NotesUploadStatus = "Failed";
                            row.NotesUploadMessage = FirstLine(ex.Message);
                            await _db.SaveChangesAsync(token);
                            throw new LogisysException(
                                "Softlink returned to the Login page, so Attach stopped. Sign in again in the open Edge window (Security Code + Log In + Submit), leave it open, then Attach immediately.");
                        }

                        failed++;
                        row.NotesUploadStatus = "Failed";
                        row.NotesUploadMessage = FirstLine(ex.Message);
                        _logger.LogWarning(ex, "Could not attach notes for {Organisation}.", row.OrganisationName);
                        try
                        {
                            await _browser.CaptureAsync(page, row.OrganisationName);
                        }
                        catch (PlaywrightException)
                        {
                        }
                    }

                    await _db.SaveChangesAsync(token);
                    page = await LogisysBrowserSession.EnsureLivePageAsync(page);
                }
            }
            catch (Exception)
            {
                throw;
            }

            return new NoteUploadSummary(rows.Count, attached, skipped, failed,
                $"Organisations {rows.Count}: attached {attached} file(s), skipped {skipped}, failed {failed}.");
        }, cancellationToken);
    }

    private async Task<string?> DownloadToTempAsync(
        FormDocument document,
        bool useApi,
        CancellationToken cancellationToken)
    {
        if (GoogleDriveAccess.FileId(document.Link) is not { } fileId)
        {
            return null;
        }

        try
        {
            string name;
            string mime;
            byte[] content;
            if (useApi)
            {
                var file = await _drive.DownloadAsync(fileId, cancellationToken);
                name = file.Name;
                mime = file.MimeType;
                content = file.Content;
            }
            else
            {
                var file = await _googleBrowser.DownloadDriveFileAsync(fileId, cancellationToken);
                name = file.Name;
                mime = file.MimeType;
                content = file.Content;
            }

            if (content.Length > _options.MaxAttachmentBytes)
            {
                _logger.LogWarning("{Label} is {Size} bytes; Softlink allows {Max}.", document.Label, content.Length, _options.MaxAttachmentBytes);
                return null;
            }

            var extension = ExtensionFor(name, mime);
            var path = Path.Combine(Path.GetTempPath(), "logisys-note-" + Guid.NewGuid().ToString("N") + extension);
            await File.WriteAllBytesAsync(path, content, cancellationToken);
            return path;
        }
        catch (Exception ex) when (ex is GoogleDriveException or FormSheetException or HttpRequestException or TaskCanceledException or TimeoutException or PlaywrightException)
        {
            _logger.LogWarning(ex, "Could not download {Label} from Drive.", document.Label);
            return null;
        }
    }

    // Softlink Organization Master: Notes is the second-to-last main tab (notepad icon after KYC, before Globe).
    // Always click Notes for the current master — do not trust a leftover Notes pane from another org.
    private static async Task OpenNotesTabAsync(IPage page)
    {
        if (await ClickSecondToLastMasterTabAsync(page))
        {
            await page.WaitForTimeoutAsync(1_000);
            if (await NotesGridReadyAsync(page))
            {
                return;
            }
        }

        var candidates = new[]
        {
            page.GetByTitle("Notes", new PageGetByTitleOptions { Exact = false }),
            page.Locator("[title*='Note' i], [aria-label*='Note' i]"),
            page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { NameRegex = new Regex("note", RegexOptions.IgnoreCase) })
        };

        foreach (var locator in candidates)
        {
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            try
            {
                await locator.First.ClickAsync(new LocatorClickOptions { Timeout = 5_000, Force = true });
                await page.WaitForTimeoutAsync(1_000);
                if (await NotesGridReadyAsync(page))
                {
                    return;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        throw new LogisysException(
            "The Notes tab (second-to-last tab after KYC) was not found on Organization Master.");
    }

    private static async Task<bool> ClickSecondToLastMasterTabAsync(IPage page)
    {
        foreach (var frame in page.Frames)
        {
            try
            {
                var clicked = await frame.EvaluateAsync<bool>("""
                    () => {
                      const norm = (t) => (t || '').replace(/\s+/g, ' ').trim().toLowerCase();
                      const kycNodes = [...document.querySelectorAll('a, span, td, li, div')].filter(n =>
                        norm(n.textContent) === 'kyc' && n.children.length < 3);
                      for (const kyc of kycNodes) {
                        const row = kyc.parentElement;
                        if (!row) continue;
                        const tabs = [...row.children].filter(c => {
                          const style = window.getComputedStyle(c);
                          return style.display !== 'none' && style.visibility !== 'hidden' && c.offsetParent !== null;
                        });
                        if (tabs.length < 3) continue;
                        // Last = Globe, second-to-last = Notes (notepad icon).
                        const notes = tabs[tabs.length - 2];
                        notes.click();
                        return true;
                      }
                      return false;
                    }
                    """);
                if (clicked)
                {
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        return false;
    }

    private static async Task<bool> NotesGridReadyAsync(IPage page)
    {
        // Notes pane shows Date / Created By / Notes with New at the bottom.
        foreach (var root in new[] { page.Locator("body") }.Concat(page.Frames.Where(f => f != page.MainFrame).Select(f => f.Locator("body"))))
        {
            try
            {
                var createdBy = root.GetByText("Created By", new() { Exact = true });
                var notesHeader = root.GetByText("Notes", new() { Exact = true });
                var newBtn = root.GetByText("New", new() { Exact = true });
                if (await createdBy.CountAsync() > 0 && await notesHeader.CountAsync() > 0
                    && await newBtn.CountAsync() > 0)
                {
                    return true;
                }

                if (await page.GetByText("Add Note", new PageGetByTextOptions { Exact = false }).CountAsync() > 0
                    || await page.Locator("text=Predefined Notes").CountAsync() > 0)
                {
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        return false;
    }

    private static async Task AddNoteWithAttachmentAsync(IPage page, string label, string filePath)
    {
        // Softlink Notes: #NotesControl1_btnNew → iframe UploadAttachment.aspx
        // (#txtNotes, #fuAttachment Choose File, #btnUpload Save).
        // Save is <input type=submit> — Playwright's click waits for navigation and often hits 30s;
        // use a DOM click instead, then wait for the iframe to unload.
        await DismissOpenNoteDialogsAsync(page);

        try
        {
            if (!await ClickNotesNewAsync(page))
            {
                throw new LogisysException("The Notes New button was not found (open the notepad tab after KYC).");
            }
        }
        catch (PlaywrightException ex)
        {
            throw new LogisysException("Notes → New timed out: " + FirstLine(ex.Message));
        }

        IFrame dialog;
        try
        {
            dialog = await WaitForUploadAttachmentFrameAsync(page)
                ?? throw new LogisysException("The Add Note / Upload Attachment dialog did not open after Notes → New.");
        }
        catch (PlaywrightException ex)
        {
            throw new LogisysException("Waiting for Add Note dialog timed out: " + FirstLine(ex.Message));
        }

        try
        {
            var notes = dialog.Locator("#txtNotes");
            if (await notes.CountAsync() > 0)
            {
                await notes.First.FillAsync(label, new LocatorFillOptions { Timeout = 10_000 });
            }

            // Optional predefined note "Init" (DOM click — avoids actionability waits).
            await dialog.EvaluateAsync("""
                () => { const el = document.querySelector('#lnk_Init'); if (el) el.click(); }
                """);

            var file = dialog.Locator("#fuAttachment, input[name='fuAttachment'], input[type='file']");
            if (await file.CountAsync() == 0)
            {
                throw new LogisysException("The Attachment Choose File box (#fuAttachment) was not found on Add Note.");
            }

            await file.First.SetInputFilesAsync(filePath);
            await page.WaitForTimeoutAsync(500);

            var saved = await dialog.EvaluateAsync<bool>("""
                () => {
                  const b = document.querySelector('#btnUpload');
                  if (!b) return false;
                  b.click();
                  return true;
                }
                """);
            if (!saved)
            {
                throw new LogisysException("The Save button was not found on the Add Note dialog.");
            }
        }
        catch (PlaywrightException ex)
        {
            throw new LogisysException("Add Note attach/save failed: " + FirstLine(ex.Message));
        }

        // Dialog iframe usually unloads after Save.
        for (var i = 0; i < 40; i++)
        {
            if (FindUploadAttachmentFrame(page) is null)
            {
                return;
            }

            await page.WaitForTimeoutAsync(250);
        }

        // Still open — try Close so the next note/org is not blocked.
        await DismissOpenNoteDialogsAsync(page);
    }

    private static async Task DismissOpenNoteDialogsAsync(IPage page)
    {
        foreach (var frame in page.Frames.ToList())
        {
            try
            {
                var url = frame.Url ?? string.Empty;
                if (!url.Contains("UploadAttachment", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await frame.EvaluateAsync("""
                    () => {
                      const close = document.querySelector('#btnClose');
                      if (close) close.click();
                    }
                    """);
            }
            catch (PlaywrightException)
            {
            }
        }

        await page.WaitForTimeoutAsync(400);

        // Softlink embeds Add Note in #lpopEmbedWin_container — hide if still stuck.
        try
        {
            await page.EvaluateAsync("""
                () => {
                  const win = document.querySelector('#lpopEmbedWin_container');
                  if (win) win.style.display = 'none';
                }
                """);
        }
        catch (PlaywrightException)
        {
        }
    }

    private static async Task TryCloseOrgMasterAsync(IPage master)
    {
        try
        {
            if (master.IsClosed)
            {
                return;
            }

            var url = master.Url ?? string.Empty;
            if (!url.Contains("OrganizationMaster", StringComparison.OrdinalIgnoreCase)
                && !await SoftlinkOrgNavigation.IsOrgMasterOpenAsync(master))
            {
                return;
            }

            var closed = await master.EvaluateAsync<bool>("""
                () => {
                  const buttons = [...document.querySelectorAll('input[type="button"], input[type="submit"], button, a')];
                  const label = (b) => ((b.value || b.textContent || '').replace(/\s+/g,' ').trim().toLowerCase());
                  const close = buttons.find(b => {
                    const text = label(b);
                    return (text === 'close' || text === 'save & close' || text === 'save and close')
                      && !b.disabled && !b.classList.contains('aspNetDisabled');
                  });
                  if (!close) return false;
                  close.click();
                  return true;
                }
                """);
            if (closed)
            {
                await master.WaitForTimeoutAsync(1_000);
            }

            if (!master.IsClosed)
            {
                await master.CloseAsync();
            }
        }
        catch (PlaywrightException)
        {
        }
    }

    private static async Task<IFrame?> WaitForUploadAttachmentFrameAsync(IPage page)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var frame = FindUploadAttachmentFrame(page);
            if (frame is not null)
            {
                try
                {
                    var file = frame.Locator("#fuAttachment, input[type='file']");
                    if (await file.CountAsync() > 0)
                    {
                        return frame;
                    }
                }
                catch (PlaywrightException)
                {
                }
            }

            await page.WaitForTimeoutAsync(250);
        }

        return FindUploadAttachmentFrame(page);
    }

    private static IFrame? FindUploadAttachmentFrame(IPage page)
    {
        foreach (var frame in page.Frames)
        {
            var url = frame.Url ?? string.Empty;
            if (url.Contains("UploadAttachment", StringComparison.OrdinalIgnoreCase))
            {
                return frame;
            }
        }

        return null;
    }

    private static async Task<bool> ClickNotesNewAsync(IPage page)
    {
        // Prefer DOM click on Softlink Notes New — avoids 30s actionability waits under overlays.
        foreach (var frame in page.Frames)
        {
            try
            {
                var clicked = await frame.EvaluateAsync<bool>("""
                    () => {
                      const exact = document.querySelector('#NotesControl1_btnNew');
                      if (exact && !exact.classList.contains('aspNetDisabled')) {
                        exact.click();
                        return true;
                      }
                      const norm = (t) => (t || '').replace(/\s+/g, ' ').trim().toLowerCase();
                      const nodes = [...document.querySelectorAll('a.GridLinkButton_Add, a, span, td, button, input[type="button"], input[type="submit"]')];
                      const el = nodes.find(n => {
                        const t = norm(n.value || n.textContent);
                        if (t !== 'new') return false;
                        if (n.disabled || n.classList.contains('aspNetDisabled')) return false;
                        const id = (n.id || '').toLowerCase();
                        if (id.includes('savenew') || id.includes('saveandnew')) return false;
                        return true;
                      });
                      if (!el) return false;
                      el.click();
                      return true;
                    }
                    """);
                if (clicked)
                {
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        return false;
    }

    private static async Task<bool> ClickButtonAsync(IPage page, string name)
    {
        var button = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = name, Exact = true });
        if (await button.CountAsync() == 0 || !await button.First.IsVisibleAsync() || await button.First.IsDisabledAsync())
        {
            return false;
        }

        await button.First.ClickAsync();
        return true;
    }

    private static async Task<bool> ClickTextAsync(IPage page, string text)
    {
        var locator = page.GetByText(text, new PageGetByTextOptions { Exact = true });
        if (await locator.CountAsync() == 0 || !await locator.First.IsVisibleAsync())
        {
            locator = page.GetByText(text, new PageGetByTextOptions { Exact = false });
        }

        if (await locator.CountAsync() == 0 || !await locator.First.IsVisibleAsync())
        {
            return false;
        }

        await locator.First.ClickAsync();
        return true;
    }

    private static string ExtensionFor(string name, string mime)
    {
        var ext = Path.GetExtension(name);
        if (!string.IsNullOrWhiteSpace(ext) && ext.Length <= 8)
        {
            return ext;
        }

        return mime.ToLowerInvariant() switch
        {
            "application/pdf" => ".pdf",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            "application/msword" => ".doc",
            _ => ".bin"
        };
    }

    private static string Key(string value) => NonAlpha.Replace(value.ToUpperInvariant(), string.Empty);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 300 ? line[..300] : line;
    }
}
