using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace LogisysOrgConverter.GoogleDrive;

public sealed class GoogleVisitorOptions
{
    public const string Section = "GoogleVisitor";

    // "msedge" or "chrome" uses the browser already installed on the PC;
    // blank uses Playwright's own Chromium, downloaded on first use.
    public string BrowserChannel { get; set; } = string.Empty;

    // Opens a visible browser window, useful when Google blocks hidden browsers.
    public bool ShowBrowser { get; set; }

    public int CodeWaitMinutes { get; set; } = 3;
}

// Opens the GST certificates the way riya@tnpsfl.com does in a browser: Google Drive visitor
// access, confirmed with the one-time code Google emails to that Outlook mailbox. Google has no
// API for visitor access, so this drives Google's sign-in pages and keeps the ~7-day session.
public sealed class GoogleVisitorAccess
{
    private readonly GoogleVisitorOptions _options;
    private readonly OutlookCodeReader _outlook;
    private readonly ILogger<GoogleVisitorAccess> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public GoogleVisitorAccess(
        IOptions<GoogleVisitorOptions> options,
        OutlookCodeReader outlook,
        IWebHostEnvironment environment,
        ILogger<GoogleVisitorAccess> logger)
    {
        _options = options.Value;
        _outlook = outlook;
        _logger = logger;
        var data = Path.Combine(environment.ContentRootPath, "App_Data");
        StateFile = Path.Combine(data, "google-visitor-session.json");
        DebugFolder = Path.Combine(data, "google-visitor-debug");
    }

    public bool IsConfigured => _outlook.IsConfigured;

    public string Mailbox => _outlook.Mailbox;

    public string StateFile { get; }

    public string DebugFolder { get; }

    public DateTime? SessionSavedUtc => File.Exists(StateFile) ? File.GetLastWriteTimeUtc(StateFile) : null;

    public void ForgetSession()
    {
        if (File.Exists(StateFile))
        {
            File.Delete(StateFile);
        }
    }

    public Task CheckOutlookAsync(CancellationToken cancellationToken) => _outlook.CheckAccessAsync(cancellationToken);

    public async Task<GoogleVisitorSession> OpenAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new GoogleDriveException("visitor access is not set up; fill the Microsoft section of appsettings.Local.json");
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var playwright = await Playwright.CreateAsync();
            var browser = await LaunchAsync(playwright);
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                StorageStatePath = File.Exists(StateFile) ? StateFile : null,
                Locale = "en-US",
                AcceptDownloads = true
            });
            return new GoogleVisitorSession(this, playwright, browser, context, _outlook, _options, _logger, () => _lock.Release());
        }
        catch (Exception ex)
        {
            _lock.Release();
            if (ex is GoogleDriveException)
            {
                throw;
            }

            _logger.LogError(ex, "Could not start the browser for Google visitor access.");
            throw new GoogleDriveException($"the browser for Google visitor access could not start ({ex.Message.Split('\n')[0]})");
        }
    }

    private async Task<IBrowser> LaunchAsync(IPlaywright playwright)
    {
        var launch = new BrowserTypeLaunchOptions
        {
            Headless = !_options.ShowBrowser,
            Channel = string.IsNullOrWhiteSpace(_options.BrowserChannel) ? null : _options.BrowserChannel.Trim(),
            // Without these Google's sign-in refuses the browser as "may not be secure".
            Args = ["--disable-blink-features=AutomationControlled"],
            IgnoreDefaultArgs = ["--enable-automation"]
        };

        try
        {
            return await playwright.Chromium.LaunchAsync(launch);
        }
        catch (PlaywrightException ex) when (launch.Channel is null && ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Downloading Chromium for Google visitor access (first use only).");
            if (Microsoft.Playwright.Program.Main(["install", "chromium"]) != 0)
            {
                throw new GoogleDriveException("Chromium could not be downloaded; set GoogleVisitor:BrowserChannel to \"msedge\" to use Edge instead");
            }

            return await playwright.Chromium.LaunchAsync(launch);
        }
    }
}

public sealed class GoogleVisitorSession : IAsyncDisposable
{
    private static readonly Regex NextButton = new(@"^\s*(next|continue|verify|confirm|submit|done|send|get (a )?code|send code|i agree|allow)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AsksForCode = new(@"verification code|enter (the )?code|enter (the )?pin|\bPIN\b|code (was )?sent|we sent|sent (a|an) (code|email)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RobotCheck = new(@"not a robot|type the text you hear or see|captcha|unusual traffic", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NoAccount = new(@"couldn.t find (this|your google) account|enter a valid email", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BrowserBlocked = new(@"this browser or app may not be secure", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WrongCode = new(@"wrong code|incorrect code|code (is )?(invalid|expired)|try again", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly GoogleVisitorAccess _access;
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly IBrowserContext _context;
    private readonly OutlookCodeReader _outlook;
    private readonly GoogleVisitorOptions _options;
    private readonly ILogger _logger;
    private readonly Action _release;
    private bool _signedInThisSession;
    private string? _signInFailure;

    internal GoogleVisitorSession(
        GoogleVisitorAccess access,
        IPlaywright playwright,
        IBrowser browser,
        IBrowserContext context,
        OutlookCodeReader outlook,
        GoogleVisitorOptions options,
        ILogger logger,
        Action release)
    {
        _access = access;
        _playwright = playwright;
        _browser = browser;
        _context = context;
        _outlook = outlook;
        _options = options;
        _logger = logger;
        _release = release;
    }

    public async Task<DriveFile> DownloadAsync(string fileId, CancellationToken cancellationToken)
    {
        var first = await TryDownloadAsync(fileId);
        if (first.File is not null)
        {
            return first.File;
        }

        if (!first.NeedsSignIn)
        {
            throw new GoogleDriveException(first.Problem!);
        }

        if (_signInFailure is not null)
        {
            throw new GoogleDriveException(_signInFailure);
        }

        if (!_signedInThisSession)
        {
            try
            {
                await SignInAsync(fileId, cancellationToken);
            }
            catch (GoogleDriveException ex)
            {
                _signInFailure = ex.Message;
                throw;
            }

            var second = await TryDownloadAsync(fileId);
            if (second.File is not null)
            {
                return second.File;
            }

            throw new GoogleDriveException(second.NeedsSignIn
                ? $"Google accepted the code but still does not let {_outlook.Mailbox} open this file"
                : second.Problem!);
        }

        throw new GoogleDriveException($"{_outlook.Mailbox} has no visitor access to this file");
    }

    private async Task<(DriveFile? File, bool NeedsSignIn, string? Problem)> TryDownloadAsync(string fileId)
    {
        var response = await _context.APIRequest.GetAsync(
            $"https://drive.google.com/uc?export=download&id={Uri.EscapeDataString(fileId)}",
            new APIRequestContextOptions { MaxRedirects = 10, Timeout = 60_000 });
        var body = await response.BodyAsync();
        response.Headers.TryGetValue("content-type", out var contentType);
        contentType ??= string.Empty;
        var isPdf = body.Length > 4 && body[0] == '%' && body[1] == 'P' && body[2] == 'D' && body[3] == 'F';

        if (response.Ok && (isPdf || !contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)))
        {
            var mime = isPdf ? "application/pdf" : contentType.Split(';')[0].Trim();
            return (new DriveFile(fileId, mime, body), false, null);
        }

        var url = response.Url;
        var html = System.Text.Encoding.UTF8.GetString(body);
        if (url.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase)
            || html.Contains("accounts.google.com/v3/signin", StringComparison.OrdinalIgnoreCase)
            || html.Contains("ServiceLogin", StringComparison.Ordinal)
            || response.Status is 401 or 403)
        {
            return (null, true, null);
        }

        if (response.Status == 404)
        {
            return (null, false, "Google Drive says the file does not exist");
        }

        return (null, false, "Google showed a page instead of the file (the owner may have turned off downloading)");
    }

    private async Task SignInAsync(string fileId, CancellationToken cancellationToken)
    {
        var page = await _context.NewPageAsync();
        DateTime? codeRequestedUtc = null;
        var codesEntered = 0;
        try
        {
            await page.GotoAsync($"https://drive.google.com/file/d/{Uri.EscapeDataString(fileId)}/view", new PageGotoOptions { Timeout = 60_000 });
            for (var step = 0; step < 15; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SettleAsync(page);

                var host = new Uri(page.Url).Host;
                if (!host.StartsWith("accounts.", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Google visitor access confirmed for {Mailbox}.", _outlook.Mailbox);
                    await SaveStateAsync();
                    _signedInThisSession = true;
                    return;
                }

                var text = await page.InnerTextAsync("body", new PageInnerTextOptions { Timeout = 10_000 });
                if (RobotCheck.IsMatch(text) || await page.Locator("iframe[src*='recaptcha']").CountAsync() > 0)
                {
                    throw await FailAsync(page, "Google asked for a \"not a robot\" check, which the app cannot answer. Set GoogleVisitor:ShowBrowser to true and convert again so you can solve it in the window");
                }

                if (BrowserBlocked.IsMatch(text))
                {
                    throw await FailAsync(page, "Google refused the hidden browser. Set GoogleVisitor:BrowserChannel to \"msedge\" and ShowBrowser to true, then try again");
                }

                if (NoAccount.IsMatch(text))
                {
                    throw await FailAsync(page, $"Google's sign-in page says {_outlook.Mailbox} has no Google account, so it offers no code there");
                }

                var identifier = page.Locator("input[name='identifier']:visible, input[type='email']:visible");
                if (await identifier.CountAsync() > 0 && string.IsNullOrWhiteSpace(await identifier.First.InputValueAsync()))
                {
                    _logger.LogInformation("Google sign-in: entering {Mailbox}.", _outlook.Mailbox);
                    await identifier.First.FillAsync(_outlook.Mailbox);
                    codeRequestedUtc = DateTime.UtcNow;
                    await ClickNextAsync(page, identifier.First);
                    continue;
                }

                var codeInput = page.Locator(
                    "input[autocomplete='one-time-code']:visible, input[type='tel']:visible, input[name='Pin']:visible, input[name='pin']:visible, input[name='code']:visible, input[type='number']:visible, input[type='text']:not([name='identifier']):visible");
                if (AsksForCode.IsMatch(text) && await codeInput.CountAsync() > 0)
                {
                    if (codesEntered >= 2)
                    {
                        throw await FailAsync(page, WrongCode.IsMatch(text)
                            ? "Google did not accept the code from the email"
                            : "Google kept asking for the code after it was entered");
                    }

                    _logger.LogInformation("Google sign-in: waiting for the code in {Mailbox}.", _outlook.Mailbox);
                    string code;
                    try
                    {
                        code = await _outlook.WaitForCodeAsync(codeRequestedUtc ?? DateTime.UtcNow.AddMinutes(-2), TimeSpan.FromMinutes(Math.Max(1, _options.CodeWaitMinutes)), cancellationToken);
                    }
                    catch (GoogleDriveException ex)
                    {
                        throw await FailAsync(page, ex.Message);
                    }

                    await codeInput.First.FillAsync(code);
                    codesEntered++;
                    codeRequestedUtc = DateTime.UtcNow;
                    await ClickNextAsync(page, codeInput.First);
                    continue;
                }

                var button = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameRegex = NextButton });
                if (await button.CountAsync() > 0 && await button.First.IsVisibleAsync())
                {
                    var name = (await button.First.InnerTextAsync()).Trim();
                    _logger.LogInformation("Google sign-in: clicking \"{Button}\".", name);
                    if (Regex.IsMatch(name, "send|code", RegexOptions.IgnoreCase))
                    {
                        codeRequestedUtc = DateTime.UtcNow;
                    }

                    await button.First.ClickAsync();
                    continue;
                }

                throw await FailAsync(page, "the Google sign-in showed a screen the app does not recognise");
            }

            throw await FailAsync(page, "the Google sign-in did not finish");
        }
        catch (TimeoutException)
        {
            throw await FailAsync(page, "Google took too long to answer");
        }
        catch (PlaywrightException ex)
        {
            throw await FailAsync(page, $"the browser failed during Google sign-in ({ex.Message.Split('\n')[0]})");
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static async Task ClickNextAsync(IPage page, ILocator input)
    {
        var button = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameRegex = NextButton });
        if (await button.CountAsync() > 0 && await button.First.IsVisibleAsync())
        {
            await button.First.ClickAsync();
        }
        else
        {
            await input.PressAsync("Enter");
        }
    }

    private static async Task SettleAsync(IPage page)
    {
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 15_000 });
        }
        catch (TimeoutException)
        {
        }

        await page.WaitForTimeoutAsync(1_500);
    }

    private async Task<GoogleDriveException> FailAsync(IPage page, string message)
    {
        try
        {
            Directory.CreateDirectory(_access.DebugFolder);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(_access.DebugFolder, $"{stamp}.png"), FullPage = true });
            await File.WriteAllTextAsync(Path.Combine(_access.DebugFolder, $"{stamp}.html"), await page.ContentAsync());
            _logger.LogWarning("Google visitor sign-in stopped: {Message}. Screenshot {Stamp}.png in {Folder}.", message, stamp, _access.DebugFolder);
            return new GoogleDriveException($"{message}. A screenshot ({stamp}.png) was saved in {_access.DebugFolder}");
        }
        catch (Exception)
        {
            return new GoogleDriveException(message);
        }
    }

    private async Task SaveStateAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_access.StateFile)!);
        await _context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = _access.StateFile });
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_signedInThisSession)
            {
                await SaveStateAsync();
            }

            await _context.CloseAsync();
            await _browser.CloseAsync();
            _playwright.Dispose();
        }
        finally
        {
            _release();
        }
    }
}
