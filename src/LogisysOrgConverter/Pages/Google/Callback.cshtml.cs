using System.Security.Cryptography;
using LogisysOrgConverter.GoogleDrive;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LogisysOrgConverter.Pages.Google;

public class CallbackModel : PageModel
{
    private readonly GoogleDriveAccess _drive;
    private readonly IDataProtector _protector;

    public CallbackModel(GoogleDriveAccess drive, IDataProtectionProvider dataProtection)
    {
        _drive = drive;
        _protector = dataProtection.CreateProtector(IndexModel.SignInPurpose);
    }

    [TempData]
    public string? Message { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(string? code, string? state, string? error, CancellationToken cancellationToken)
    {
        var saved = Request.Cookies[IndexModel.SignInCookie];
        Response.Cookies.Delete(IndexModel.SignInCookie);

        if (!string.IsNullOrEmpty(error))
        {
            ErrorMessage = error == "access_denied"
                ? "Google sign-in was cancelled, so Drive is not connected."
                : $"Google sign-in failed ({error}).";
            return RedirectToPage("/Google/Index");
        }

        string[] parts;
        try
        {
            parts = saved is null ? [] : _protector.Unprotect(saved).Split('|');
        }
        catch (CryptographicException)
        {
            parts = [];
        }

        if (parts.Length != 2 || string.IsNullOrEmpty(code) || parts[0] != state)
        {
            ErrorMessage = "The Google sign-in expired or was started in another browser. Click Connect Google Drive again.";
            return RedirectToPage("/Google/Index");
        }

        try
        {
            var email = await _drive.CompleteSignInAsync(code, _drive.RedirectUri(Request), parts[1], cancellationToken);
            Message = $"Google Drive connected as {email}. GST certificates will be read on the next conversion.";
        }
        catch (GoogleDriveException ex)
        {
            ErrorMessage = $"Google Drive was not connected: {ex.Message}.";
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Google could not be reached. Check the internet connection and try again.";
        }

        return RedirectToPage("/Google/Index");
    }
}
