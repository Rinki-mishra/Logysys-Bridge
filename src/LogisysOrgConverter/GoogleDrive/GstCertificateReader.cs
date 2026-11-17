using System.Collections.Concurrent;
using LogisysOrgConverter.Conversion;
using LogisysOrgConverter.Notifications;

namespace LogisysOrgConverter.GoogleDrive;

public sealed class GstCertificateReader
{
    private readonly GoogleDriveAccess _drive;
    private readonly GoogleBrowserSession _googleBrowser;
    private readonly GoogleVisitorAccess _visitor;

    public GstCertificateReader(
        GoogleDriveAccess drive,
        GoogleBrowserSession googleBrowser,
        GoogleVisitorAccess visitor)
    {
        _drive = drive;
        _googleBrowser = googleBrowser;
        _visitor = visitor;
    }

    // Keyed by the exact link in the GST document column, which is how the converter looks them up.
    public async Task<IReadOnlyDictionary<string, GstCertificate>> ReadAsync(
        IReadOnlyList<ClientRow> rows,
        CancellationToken cancellationToken = default)
    {
        var links = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Cells.TryGetValue(OrganisationConverter.GstDocumentColumn, out var link) && !string.IsNullOrWhiteSpace(link))
            {
                row.Cells.TryGetValue(OrganisationConverter.PanColumn, out var pan);
                links.TryAdd(link.Trim(), pan);
            }
        }

        var results = new ConcurrentDictionary<string, GstCertificate>(StringComparer.Ordinal);
        if (links.Count == 0)
        {
            return results;
        }

        // 1) Connected Google Drive OAuth (same as feature/softlink-automation).
        if (await UsesGoogleSignInAsync(cancellationToken))
        {
            try
            {
                await _drive.GetAccessTokenAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is GoogleDriveException or HttpRequestException or TaskCanceledException)
            {
                return FailAll(links.Keys, ex is GoogleDriveException ? ex.Message : "Google could not be reached");
            }

            await Parallel.ForEachAsync(
                links,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
                async (item, token) => results[item.Key] = await ReadAsync(item.Key, item.Value, _drive.DownloadAsync, token));
            return results;
        }

        // Do not open GoogleSession/Edge here during convert — that hung the mail poller and blocked
        // Softlink. Notes still download Drive files later via GoogleBrowserSession after convert.
        // Typed GSTIN / form values are used when certificates are not fetched here.
        return FailAll(
            links.Keys,
            "GST certificate Drive download skipped during convert so Softlink can proceed; typed GSTIN is used when present");
    }

    public async Task<GstCertificate> ReadOneAsync(string link, string? pan, CancellationToken cancellationToken)
    {
        if (await UsesGoogleSignInAsync(cancellationToken))
        {
            return await ReadAsync(link, pan, _drive.DownloadAsync, cancellationToken);
        }

        if (!_googleBrowser.HasSignedIn && !_visitor.IsConfigured)
        {
            return GstCertificate.Failed(
                "Google Drive is not connected; sign in once via the spreadsheet download so GoogleSession can open Drive links");
        }

        try
        {
            if (GoogleDriveAccess.FileId(link) is not { } fileId)
            {
                return GstCertificate.Failed("the GST document column is not a Google Drive link");
            }

            var file = await _googleBrowser.DownloadDriveFileAsync(fileId, cancellationToken);
            return GstinReader.FromFile(file.Content, file.MimeType, pan);
        }
        catch (FormSheetException ex)
        {
            return GstCertificate.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or Microsoft.Playwright.PlaywrightException)
        {
            return GstCertificate.Failed("Google Drive could not be reached");
        }
    }

    private async Task<IReadOnlyDictionary<string, GstCertificate>> ReadViaGoogleSessionAsync(
        IReadOnlyDictionary<string, string?> links,
        CancellationToken cancellationToken)
    {
        var results = new ConcurrentDictionary<string, GstCertificate>(StringComparer.Ordinal);
        var fileIds = new List<string>();
        var linkById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var link in links.Keys)
        {
            if (GoogleDriveAccess.FileId(link) is not { } fileId)
            {
                results[link] = GstCertificate.Failed("the GST document column is not a Google Drive link");
                continue;
            }

            linkById[fileId] = link;
            fileIds.Add(fileId);
        }

        if (fileIds.Count == 0)
        {
            return results;
        }

        IReadOnlyDictionary<string, (string Name, string MimeType, byte[] Content)> downloaded;
        try
        {
            downloaded = await _googleBrowser.DownloadDriveFilesAsync(fileIds, cancellationToken);
        }
        catch (FormSheetException ex)
        {
            return FailAll(links.Keys, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or Microsoft.Playwright.PlaywrightException)
        {
            return FailAll(links.Keys, "Google Drive could not be reached");
        }

        foreach (var (link, pan) in links)
        {
            if (results.ContainsKey(link))
            {
                continue;
            }

            if (GoogleDriveAccess.FileId(link) is not { } fileId
                || !downloaded.TryGetValue(fileId, out var file))
            {
                results[link] = GstCertificate.Failed(
                    "Google Drive did not return the file. Open the spreadsheet once so GoogleSession is signed in, then try again.");
                continue;
            }

            results[link] = GstinReader.FromFile(file.Content, file.MimeType, pan);
        }

        return results;
    }

    private async Task<bool> UsesGoogleSignInAsync(CancellationToken cancellationToken) =>
        _drive.IsConfigured && await _drive.GetConnectionAsync(cancellationToken) is not null;

    private static async Task<GstCertificate> ReadAsync(
        string link,
        string? pan,
        Func<string, CancellationToken, Task<DriveFile>> download,
        CancellationToken cancellationToken)
    {
        if (GoogleDriveAccess.FileId(link) is not { } fileId)
        {
            return GstCertificate.Failed("the GST document column is not a Google Drive link");
        }

        try
        {
            var file = await download(fileId, cancellationToken);
            return GstinReader.FromFile(file.Content, file.MimeType, pan);
        }
        catch (GoogleDriveException ex)
        {
            return GstCertificate.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or Microsoft.Playwright.PlaywrightException)
        {
            return GstCertificate.Failed("Google Drive could not be reached");
        }
    }

    private static ConcurrentDictionary<string, GstCertificate> FailAll(IEnumerable<string> links, string problem) =>
        new(links.Select(link => KeyValuePair.Create(link, GstCertificate.Failed(problem))), StringComparer.Ordinal);
}
