using LogisysOrgConverter.Conversion;
using LogisysOrgConverter.Data;
using LogisysOrgConverter.Notifications;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LogisysOrgConverter.Pages.Google;

public class CallbackModel : PageModel
{
    private readonly GoogleSheets _google;
    private readonly ConversionStore _store;

    public CallbackModel(GoogleSheets google, ConversionStore store)
    {
        _google = google;
        _store = store;
    }

    public async Task<IActionResult> OnGetAsync(string? code, string? state, string? error, CancellationToken cancellationToken)
    {
        var redirectUri = HttpContext.Session.GetString(ConnectModel.OAuthRedirect) ?? _google.RedirectUri(Request);
        var expected = HttpContext.Session.GetString(ConnectModel.OAuthState);
        HttpContext.Session.Remove(ConnectModel.OAuthState);
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || state != expected)
        {
            TempData["Error"] = string.IsNullOrEmpty(error)
                ? "Google sign-in could not be confirmed. Try connecting again."
                : "Google sign-in was cancelled.";
            return RedirectToPage("/Index");
        }

        var kind = HttpContext.Session.GetString(ConnectModel.PendingKind);
        var sourceId = HttpContext.Session.GetString(ConnectModel.PendingId);
        var title = HttpContext.Session.GetString(ConnectModel.PendingTitle);
        HttpContext.Session.Remove(ConnectModel.PendingKind);
        HttpContext.Session.Remove(ConnectModel.PendingId);
        HttpContext.Session.Remove(ConnectModel.PendingTitle);

        try
        {
            await _google.ExchangeCodeAsync(code, redirectUri, cancellationToken);
        }
        catch (FormSheetException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToPage("/Index");
        }

        if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(sourceId))
        {
            TempData["Status"] = "Signed in to Google. Choose a form below to create the Logi-Sys files.";
            return RedirectToPage("/Index");
        }

        try
        {
            var bytes = await _google.DownloadAsync(kind, sourceId, cancellationToken);
            var notice = new FormNotice(kind, sourceId, string.IsNullOrWhiteSpace(title) ? "form responses" : title, DateTime.UtcNow, null);
            await using var stream = new MemoryStream(bytes);
            var id = await _store.ConvertAsync(notice.FileName, stream, cancellationToken);
            return RedirectToPage("/Runs/Detail", new { id });
        }
        catch (Exception ex) when (ex is FormSheetException or ClientSheetException)
        {
            TempData["Error"] = ex.Message;
            return RedirectToPage("/Index");
        }
    }
}
