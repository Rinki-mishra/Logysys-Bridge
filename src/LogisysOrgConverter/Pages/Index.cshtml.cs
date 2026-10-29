using System.Text.RegularExpressions;
using LogisysOrgConverter.Conversion;
using LogisysOrgConverter.Data;
using LogisysOrgConverter.Notifications;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LogisysOrgConverter.Pages;

public partial class IndexModel : PageModel
{
    private readonly ConversionStore _store;
    private readonly FormMailbox _mailbox;
    private readonly GoogleSheets _google;

    public IndexModel(ConversionStore store, FormMailbox mailbox, GoogleSheets google)
    {
        _store = store;
        _mailbox = mailbox;
        _google = google;
    }

    [BindProperty]
    public IFormFile? ClientFile { get; set; }

    public string? ErrorMessage { get; set; }

    public string? StatusMessage { get; set; }

    public string? MailboxError { get; set; }

    public string MailboxAddress { get; private set; } = string.Empty;

    public bool GoogleConnected { get; private set; }

    public IReadOnlyList<FormNotice> Notices { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (TempData["Status"] is string status)
        {
            StatusMessage = status;
        }

        if (TempData["Error"] is string error)
        {
            ErrorMessage = error;
        }

        await LoadMailboxAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (ClientFile is null || ClientFile.Length == 0)
        {
            ErrorMessage = "Choose the client workbook first.";
            await LoadMailboxAsync(cancellationToken);
            return Page();
        }

        if (!Path.GetExtension(ClientFile.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = "The client file must be an .xlsx workbook.";
            await LoadMailboxAsync(cancellationToken);
            return Page();
        }

        try
        {
            await using var upload = ClientFile.OpenReadStream();
            await using var stream = new MemoryStream();
            await upload.CopyToAsync(stream, cancellationToken);
            stream.Position = 0;
            EnsureWorkbook(stream);
            var id = await _store.ConvertAsync(ClientFile.FileName, stream, cancellationToken);
            return RedirectToPage("/Runs/Detail", new { id });
        }
        catch (ClientSheetException ex)
        {
            ErrorMessage = ex.Message;
            await LoadMailboxAsync(cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostMailSheetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var notice = await _mailbox.FindSharedSheetAsync(cancellationToken);
            var bytes = await _google.DownloadAsync(FormSourceKinds.Sheet, notice.SourceId, cancellationToken, notice.OpenUrl);
            Response.Headers.Append("X-Sheet-Name", notice.FileName);
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", notice.FileName);
        }
        catch (FormSheetException ex)
        {
            return new ContentResult { StatusCode = StatusCodes.Status400BadRequest, Content = ex.Message, ContentType = "text/plain" };
        }
        catch (TimeoutException)
        {
            return new ContentResult
            {
                StatusCode = StatusCodes.Status400BadRequest,
                Content = "Opening the spreadsheet timed out. Load it again.",
                ContentType = "text/plain"
            };
        }
    }

    public async Task<IActionResult> OnPostSheetAsync(string kind, string sourceId, string title, CancellationToken cancellationToken)
    {
        if (kind is not (FormSourceKinds.Sheet or FormSourceKinds.Form) || !SourceIdPattern().IsMatch(sourceId ?? string.Empty))
        {
            ErrorMessage = "That form could not be read.";
            await LoadMailboxAsync(cancellationToken);
            return Page();
        }

        try
        {
            // Form edit links need owner access. riya@awgmen.com has the shared response sheet instead.
            FormNotice notice;
            byte[] bytes;
            if (kind == FormSourceKinds.Form)
            {
                notice = await _mailbox.FindSharedSheetAsync(cancellationToken);
                bytes = await _google.DownloadAsync(FormSourceKinds.Sheet, notice.SourceId, cancellationToken, notice.OpenUrl);
            }
            else
            {
                var openLink = LooksLikeOrganisationResponses(title)
                    ? FormNoticeParser.WithFormResponsesTab(
                        "https://docs.google.com/spreadsheets/d/" + sourceId + "/edit?usp=sharing")
                    : null;
                notice = new FormNotice(
                    kind,
                    sourceId!,
                    string.IsNullOrWhiteSpace(title) ? "form responses" : title,
                    DateTime.UtcNow,
                    null,
                    openLink);
                bytes = await _google.DownloadAsync(kind, sourceId!, cancellationToken, notice.OpenUrl);
            }

            await using var stream = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);
            EnsureWorkbook(stream);
            var id = await _store.ConvertAsync(notice.FileName, stream, cancellationToken);
            return RedirectToPage("/Runs/Detail", new { id });
        }
        catch (Exception ex) when (ex is FormSheetException or ClientSheetException)
        {
            ErrorMessage = ex.Message;
            await LoadMailboxAsync(cancellationToken);
            return Page();
        }
    }

    private static bool LooksLikeOrganisationResponses(string? title) =>
        !string.IsNullOrWhiteSpace(title)
        && title.Contains("Creation Request Form", StringComparison.OrdinalIgnoreCase)
        && title.Contains("Responses", StringComparison.OrdinalIgnoreCase);

    private static void EnsureWorkbook(Stream stream)
    {
        if (!stream.CanSeek)
        {
            throw new ClientSheetException("The file could not be opened as an Excel workbook.");
        }

        var position = stream.Position;
        Span<byte> header = stackalloc byte[4];
        var read = stream.Read(header);
        stream.Position = position;
        if (read < 2 || header[0] != 0x50 || header[1] != 0x4B)
        {
            throw new ClientSheetException(
                "Google did not return Organisation Creation Request Form (Responses) as an Excel file. Use Load spreadsheet from mail, then Create Logi-Sys files.");
        }
    }

    private async Task LoadMailboxAsync(CancellationToken cancellationToken)
    {
        MailboxAddress = _mailbox.Address;
        GoogleConnected = _google.IsReady;
        var listing = await _mailbox.ListAsync(cancellationToken);
        Notices = listing.Notices;
        MailboxError = listing.Error;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{8,128}$")]
    private static partial Regex SourceIdPattern();
}
