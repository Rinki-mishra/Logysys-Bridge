using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace LogisysOrgConverter.Logisys;

public sealed class LogisysException : Exception
{
    public LogisysException(string message) : base(message)
    {
    }
}

// One Softlink Edge window for the whole flow:
// 1) Sign in starts a normal Edge with only remote debugging (no Playwright --no-sandbox).
// 2) Attach connects over CDP to that same window and never closes it.
public sealed class LogisysBrowserSession : IAsyncDisposable
{
    private const int DebugPort = 9333;

    private readonly LogisysOptions _options;
    private readonly ILogger<LogisysBrowserSession> _logger;
    private readonly string _profileDir;
    private readonly string _markerPath;
    private readonly string _debugFolder;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public LogisysBrowserSession(
        IOptions<LogisysOptions> options,
        IWebHostEnvironment environment,
        ILogger<LogisysBrowserSession> logger)
    {
        _options = options.Value;
        _logger = logger;
        _profileDir = Path.Combine(environment.ContentRootPath, "SoftlinkSession");
        _markerPath = Path.Combine(_profileDir, ".signed-in");
        _debugFolder = Path.Combine(environment.ContentRootPath, "App_Data", "softlink-debug");
    }

    public string BaseUrl => (_options.BaseUrl ?? string.Empty).TrimEnd('/');

    public string UserName => (_options.UserName ?? string.Empty).Trim();

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);

    public bool HasSignedIn => File.Exists(_markerPath);

    public string DebugFolder => _debugFolder;

    // Starts (or focuses) Softlink Edge for captcha. Does not use Playwright.
    public string OpenSignInWindow()
    {
        if (!IsConfigured)
        {
            throw new LogisysException("Logisys:BaseUrl is not set in appsettings.");
        }

        Directory.CreateDirectory(_profileDir);
        if (PortOpen(DebugPort))
        {
            var whoOpen = string.IsNullOrWhiteSpace(UserName) ? "your Softlink User Id" : UserName;
            return $"Softlink Edge is already open. Finish Security Code + Log In + Submit for Gurugram in that window, leave it open, then click Attach documents. Signed in as {whoOpen} when credentials are configured.";
        }

        // Only kill leftover Softlink Edge so we can bind the debug port cleanly.
        StopSoftlinkEdge();
        var edge = FindEdge() ?? throw new LogisysException("Microsoft Edge was not found.");
        var args = string.Join(' ',
            $"--remote-debugging-port={DebugPort}",
            "--remote-allow-origins=*",
            $"--user-data-dir=\"{_profileDir}\"",
            "--no-first-run",
            "--no-default-browser-check",
            "--start-maximized",
            $"\"{BaseUrl}/Login\"");
        if (Process.Start(new ProcessStartInfo { FileName = edge, Arguments = args, UseShellExecute = false }) is null)
        {
            throw new LogisysException("Microsoft Edge could not be started.");
        }

        var who = string.IsNullOrWhiteSpace(UserName) ? "your Softlink User Id" : UserName;
        return $"Softlink Edge opened. Sign in as {who}, type the Security Code, click Log In, Submit for Gurugram, then leave that Edge window open and click Attach documents. Do not close Edge.";
    }

    public void MarkSignedIn()
    {
        Directory.CreateDirectory(_profileDir);
        File.WriteAllText(_markerPath, DateTime.UtcNow.ToString("o"));
    }

    public async Task<T> RunAsync<T>(Func<IPage, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new LogisysException("Logisys:BaseUrl is not set in appsettings.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_profileDir);
            Directory.CreateDirectory(_debugFolder);

            if (!await PortOpenAsync(DebugPort, cancellationToken))
            {
                throw new LogisysException(
                    "Softlink Edge is not open. Click “Sign in to Softlink”, finish captcha + Log In + Submit, leave Edge open, then Attach.");
            }

            _playwright ??= await Playwright.CreateAsync();
            _browser ??= await _playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{DebugPort}");

            IBrowserContext context;
            try
            {
                context = _browser.Contexts.Count > 0 ? _browser.Contexts[0] : await _browser.NewContextAsync();
            }
            catch (PlaywrightException)
            {
                _browser = null;
                _browser = await _playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{DebugPort}");
                context = _browser.Contexts.Count > 0 ? _browser.Contexts[0] : await _browser.NewContextAsync();
            }

            var page = await PickSoftlinkPageAsync(context);
            page.SetDefaultTimeout(60_000);

            try
            {
                await page.BringToFrontAsync();
                _logger.LogInformation("Softlink Attach using tab {Url}", page.Url);

                // Softlink sometimes bounces back to Login when Attach connects. Wait in the same
                // window for you to sign in again — do not navigate away or autofill captcha.
                if (await IsLoginPageAsync(page))
                {
                    // Prefer another already-signed-in tab before waiting on Login.
                    page = await PickSoftlinkPageAsync(context);
                    await page.BringToFrontAsync();
                }

                if (await IsLoginPageAsync(page))
                {
                    _logger.LogInformation(
                        "Softlink is on Login after Attach connected; waiting up to {Minutes} minute(s) for Security Code + Log In in the open Edge window.",
                        Math.Max(1, _options.LoginWaitMinutes));
                    var deadline = DateTime.UtcNow.AddMinutes(Math.Max(1, _options.LoginWaitMinutes));
                    while (DateTime.UtcNow < deadline && await IsLoginPageAsync(page))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var better = await PickSoftlinkPageAsync(context);
                        if (!await IsLoginPageAsync(better))
                        {
                            page = better;
                            await page.BringToFrontAsync();
                            break;
                        }

                        await page.WaitForTimeoutAsync(2_000);
                    }
                }

                if (await IsLoginPageAsync(page))
                {
                    await CaptureAsync(page, "still-on-login");
                    throw new LogisysException(
                        "Softlink is still on Login. In the open Edge window type Security Code, click Log In, Submit for Gurugram, leave Edge open — Attach is waiting on that same window.");
                }

                await TrySubmitCompanyAsync(page);
                if (await IsLoginPageAsync(page))
                {
                    throw new LogisysException(
                        "Softlink sign-in did not finish. Complete Log In and Submit in the open Edge window, leave it open, then Attach will continue.");
                }

                MarkSignedIn();
                // Never close Edge here — ConnectOverCDP does not own the process; leave the window open.
                return await work(page, cancellationToken);
            }
            catch (PlaywrightException ex) when (IsClosed(ex))
            {
                _browser = null;
                throw ClosedWindow();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public static bool IsClosedError(Exception ex) =>
        ex.Message.Contains("has been closed", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Target closed", StringComparison.OrdinalIgnoreCase)
        || ex.GetType().Name.Contains("TargetClosed", StringComparison.OrdinalIgnoreCase);

    // Save & Close closes Organization Master; pick another live Softlink tab for the next org / Notes.
    public static async Task<IPage> EnsureLivePageAsync(IPage page)
    {
        try
        {
            if (!page.IsClosed)
            {
                return page;
            }
        }
        catch (PlaywrightException)
        {
        }

        return await PickSoftlinkPageAsync(page.Context);
    }

    public static async Task<IPage> PickSoftlinkPageAsync(IBrowserContext context)
    {
        var pages = context.Pages.Where(candidate =>
        {
            try
            {
                return !candidate.IsClosed;
            }
            catch (PlaywrightException)
            {
                return false;
            }
        }).ToList();
        if (pages.Count == 0)
        {
            return await context.NewPageAsync();
        }

        // Prefer a signed-in Softlink tab (home / org master), never a Login tab if another exists.
        foreach (var page in pages)
        {
            var url = page.Url ?? string.Empty;
            if (IsLoginUrl(url))
            {
                continue;
            }

            if (url.Contains("NewHomePage", StringComparison.OrdinalIgnoreCase)
                || url.Contains("OrganizationMaster", StringComparison.OrdinalIgnoreCase)
                || url.Contains("Default.aspx", StringComparison.OrdinalIgnoreCase)
                || url.Contains("mylogisys.com", StringComparison.OrdinalIgnoreCase))
            {
                return page;
            }
        }

        return pages.FirstOrDefault(page => !IsLoginUrl(page.Url)) ?? pages[0];
    }

    public static async Task<bool> IsLoginPageAsync(IPage page)
    {
        // Only the real Softlink Login screen — home/org pages can contain other user/password fields.
        if (IsLoginUrl(page.Url))
        {
            return true;
        }

        try
        {
            var security = page.GetByPlaceholder("Security Code");
            return await security.CountAsync() > 0 && await security.First.IsVisibleAsync();
        }
        catch (PlaywrightException)
        {
            return IsLoginUrl(page.Url);
        }
    }

    private static async Task TrySubmitCompanyAsync(IPage page)
    {
        try
        {
            var submit = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Submit" });
            if (await submit.CountAsync() > 0 && await submit.First.IsVisibleAsync() && !await submit.First.IsDisabledAsync())
            {
                await submit.First.ClickAsync();
                await page.WaitForTimeoutAsync(2_000);
            }
        }
        catch (PlaywrightException)
        {
        }
    }

    private void StopSoftlinkEdge()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments =
                    $"-NoProfile -Command \"Get-CimInstance Win32_Process -Filter \\\"Name='msedge.exe'\\\" | Where-Object {{ $_.CommandLine -match 'SoftlinkSession|remote-debugging-port={DebugPort}' }} | ForEach-Object {{ Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi)?.WaitForExit(10_000);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not stop prior Softlink Edge processes.");
        }
    }

    private static bool PortOpen(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect("127.0.0.1", port);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            return false;
        }
    }

    private static async Task<bool> PortOpenAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or HttpRequestException or IOException)
        {
            return false;
        }
    }

    private static string? FindEdge()
    {
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
        };
        return paths.FirstOrDefault(File.Exists);
    }

    private static bool IsClosed(PlaywrightException ex) => IsClosedError(ex);

    private static LogisysException ClosedWindow() =>
        new("Softlink Edge was closed while Attach was running. Click Sign in to Softlink again, finish login, leave Edge open, then Attach. Do not close Edge.");

    private static bool IsLoginUrl(string url) =>
        url.Contains("/Login", StringComparison.OrdinalIgnoreCase)
        || url.Contains("login.aspx", StringComparison.OrdinalIgnoreCase);

    public async Task CaptureAsync(IPage page, string label)
    {
        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Sanitize(label);
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(_debugFolder, stamp + ".png"),
                FullPage = true
            });
            await File.WriteAllTextAsync(Path.Combine(_debugFolder, stamp + ".html"), await page.ContentAsync());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save Softlink debug capture for {Label}.", label);
        }
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').Take(40).ToArray();
        return new string(chars);
    }

    public ValueTask DisposeAsync()
    {
        // Do not call Browser.CloseAsync — over CDP that can kill Softlink Edge.
        _browser = null;
        try
        {
            _playwright?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Playwright dispose.");
        }

        _playwright = null;
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}

