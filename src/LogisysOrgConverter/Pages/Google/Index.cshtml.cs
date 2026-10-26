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

    private readonly GoogleVisitorAccess _visitor;

    public IndexModel(
        GoogleDriveAccess drive,
        GoogleVisitorAccess visitor,
        GstCertificateReader certificates,
        IDataProtectionProvider dataProtection)
    {
        _drive = drive;
        _visitor = visitor;
        _certificates = certificates;
        _protector = dataProtection.CreateProtector(SignInPurpose);
    }

    public bool IsConfigured => _drive.IsConfigured;

    public bool VisitorConfigured => _visitor.IsConfigured;

    public string VisitorMailbox => _visitor.Mailbox;

    public DateTime? VisitorSessionSavedUtc => _visitor.SessionSavedUtc;

    public string VisitorDebugFolder => _visitor.DebugFolder;

    public bool CanRead => (IsConfigured && Connection is not null) || VisitorConfigured;

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

    public async Task<IActionResult> OnPostCheckOutlookAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _visitor.CheckOutlookAsync(cancellationToken);
            Message = $"The app can read {_visitor.Mailbox}, so it can pick up Google's code.";
        }
        catch (GoogleDriveException ex)
        {
            ErrorMessage = $"Outlook check failed: {ex.Message}.";
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Microsoft 365 could not be reached.";
        }

        return RedirectToPage();
    }

    public IActionResult OnPostForgetVisitor()
    {
        _visitor.ForgetSession();
        Message = "Visitor session removed. The next certificate read will ask Google for a new code.";
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
