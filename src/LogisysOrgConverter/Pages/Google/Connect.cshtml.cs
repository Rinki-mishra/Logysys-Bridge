using LogisysOrgConverter.Notifications;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LogisysOrgConverter.Pages.Google;

public class ConnectModel : PageModel
{
    public const string PendingKind = "pending-form-kind";

    public const string PendingId = "pending-form-id";

    public const string PendingTitle = "pending-form-title";

    public const string OAuthState = "google-oauth-state";

    public const string OAuthRedirect = "google-oauth-redirect";

    private readonly GoogleSheets _google;

    public ConnectModel(GoogleSheets google)
    {
        _google = google;
    }

    public string RedirectUri { get; private set; } = string.Empty;

    public IActionResult OnGet()
    {
        RedirectUri = _google.RedirectUri(Request);
        if (!_google.IsConfigured)
        {
            return Page();
        }

        var state = Guid.NewGuid().ToString("N");
        HttpContext.Session.SetString(OAuthState, state);
        HttpContext.Session.SetString(OAuthRedirect, RedirectUri);
        return Redirect(_google.BuildAuthorizeUrl(RedirectUri, state));
    }
}
