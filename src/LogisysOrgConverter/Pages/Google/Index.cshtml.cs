using LogisysOrgConverter.Conversion;
using LogisysOrgConverter.Data;
using LogisysOrgConverter.GoogleDrive;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LogisysOrgConverter.Pages.Google;

public class IndexModel : PageModel
{
    public const string SignInCookie = "google_sign_in";
    public const string SignInPurpose = "GoogleDrive.SignIn";

    private readonly GoogleDriveAccess _drive;
    private readonly GstCertificateReader _certificates;
    private readonly IDataProtector _protector;

    public IndexModel(GoogleDriveAccess drive, GstCertificateReader certificates, IDataProtectionProvider dataProtection)
    {
        _drive = drive;
        _certificates = certificates;
        _protector = dataProtection.CreateProtector(SignInPurpose);
    }

    public bool IsConfigured => _drive.IsConfigured;

    public string LoginHint => _drive.LoginHint;

    public string RedirectUri => _drive.RedirectUri(Request);

    public GoogleConnection? Connection { get; private set; }

    [TempData]
    public string? Message { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    [BindProperty]
    public string? Link { get; set; }

    [BindProperty]
    public string? Pan { get; set; }

    public GstCertificate? TestResult { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Connection = await _drive.GetConnectionAsync(cancellationToken);
    }

    public IActionResult OnPostConnect()
    {
        if (!_drive.IsConfigured)
        {
            ErrorMessage = "Add the Google client ID and secret to appsettings.Local.json and restart the app first.";
            return RedirectToPage();
        }

        var state = Guid.NewGuid().ToString("N");
        var (verifier, challenge) = GoogleDriveAccess.CreatePkce();
        Response.Cookies.Append(SignInCookie, _protector.Protect($"{state}|{verifier}"), new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps,
            MaxAge = TimeSpan.FromMinutes(15)
        });
        return Redirect(_drive.AuthorizeUrl(RedirectUri, state, challenge));
    }

    public async Task<IActionResult> OnPostDisconnectAsync(CancellationToken cancellationToken)
    {
        await _drive.DisconnectAsync(cancellationToken);
        Message = "Google Drive disconnected. GST certificates will not be read until you connect again.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(CancellationToken cancellationToken)
    {
        Connection = await _drive.GetConnectionAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(Link))
        {
            ErrorMessage = "Paste a GST document link from the request sheet.";
            return RedirectToPage();
        }

        try
        {
            TestResult = await _certificates.ReadOneAsync(Link.Trim(), Pan, cancellationToken);
        }
        catch (GoogleDriveException ex)
        {
            TestResult = GstCertificate.Failed(ex.Message);
        }

        return Page();
    }
}
