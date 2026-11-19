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

// One Softlink Edge for the whole flow:
// 1) Logisys:Headless starts a signed-in profile with --headless=new (no window).
// 2) The Security Code still opens a normal visible Edge.
// 3) Attach connects over CDP and leaves that Edge running when the run finishes.
public sealed class LogisysBrowserSession : IAsyncDisposable
{
    private const int DebugPort = 9333;

    private readonly LogisysOptions _options;
    private readonly ILogger<LogisysBrowserSession> _logger;
    private readonly string _profileDir;
    private readonly string _markerPath;
    private readonly string _debugFolder;
    private readonly LogisysOrgConverter.Logging.LogisysBrowserSessionAccessor _contextAccessor;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool? _edgeHeadless;
    private bool _viewportHooked;

    public LogisysBrowserSession(
        IOptions<LogisysOptions> options,
        IWebHostEnvironment environment,
        LogisysOrgConverter.Logging.LogisysBrowserSessionAccessor contextAccessor,
        ILogger<LogisysBrowserSession> logger)
    {
        _options = options.Value;
        _logger = logger;
        _contextAccessor = contextAccessor;
        _profileDir = Path.Combine(environment.ContentRootPath, "SoftlinkSession");
        _markerPath = Path.Combine(_profileDir, ".signed-in");
        _debugFolder = Path.Combine(environment.ContentRootPath, "App_Data", "softlink-debug");
    }

    public string BaseUrl => (_options.BaseUrl ?? string.Empty).TrimEnd('/');

    public string UserName => (_options.UserName ?? string.Empty).Trim();

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);

    public bool HasSignedIn => File.Exists(_markerPath);

    public bool PrefersHeadless => _options.Headless;

    public string DebugFolder => _debugFolder;

    // Starts a visible Softlink Edge for the Security Code. Does not use Playwright.
    public string OpenSignInWindow()
    {
        if (!IsConfigured)
        {
            throw new LogisysException("Logisys:BaseUrl is not set in appsettings.");
        }

        Directory.CreateDirectory(_profileDir);
        if (PortOpen(DebugPort) && !EdgeIsHeadless())
        {
            var whoOpen = string.IsNullOrWhiteSpace(UserName) ? "your Softlink User Id" : UserName;
            return $"Softlink Edge is already open. Finish Security Code + Log In + Submit for Gurugram in that window, leave it open, then click Attach documents. Signed in as {whoOpen} when credentials are configured.";
        }

        // A hidden Edge holds the same profile, so it has to close before a visible window can open.
        StopSoftlinkEdge();
        WaitForPortClosed();
        StartEdge(headless: false);
        var who = string.IsNullOrWhiteSpace(UserName) ? "your Softlink User Id" : UserName;
        return $"Softlink Edge opened. Sign in as {who}, type the Security Code, click Log In, Submit for Gurugram, then leave that Edge window open and click Attach documents. Do not close Edge.";
    }

    public void MarkSignedIn()
    {
        Directory.CreateDirectory(_profileDir);
        File.WriteAllText(_markerPath, DateTime.UtcNow.ToString("o"));
    }

    /// <summary>
    /// Opens Softlink Edge (same as the old Sign-in button) if needed, waits for the debug port,
    /// then waits for Security Code + Log In + company Submit — so automation does not require
    /// Edge to be left open beforehand.
    /// </summary>
    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
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
            await EnsureEdgeOpenAsync(cancellationToken);
            var page = await ConnectAndWaitForSignInAsync(cancellationToken);
            MarkSignedIn();
            _logger.LogInformation("Softlink session ready at {Url}", page.Url);
        }
        finally
        {
            _gate.Release();
        }
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

            try
            {
                await EnsureEdgeOpenAsync(cancellationToken);
                var page = await ConnectAndWaitForSignInAsync(cancellationToken);
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

    private async Task EnsureEdgeOpenAsync(CancellationToken cancellationToken)
    {
        if (await PortOpenAsync(DebugPort, cancellationToken))
        {
            // A signed-in window left over from a headed run would keep Logi-Sys on screen.
            if (_options.Headless && HasSignedIn && !EdgeIsHeadless())
            {
                _logger.LogInformation("Closing the visible Softlink Edge so this signed-in run can continue without a window.");
                await ReleaseBrowserAsync();
                StopSoftlinkEdge();
                await WaitForPortClosedAsync(cancellationToken);
            }

            if (await PortOpenAsync(DebugPort, cancellationToken))
            {
                _logger.LogInformation(
                    EdgeIsHeadless()
                        ? "Reusing the headless Softlink Edge already running."
                        : "Reusing the Softlink Edge window already open.");
                return;
            }
        }

        // First sign-in has no saved session, so the Security Code needs a visible window
        // even when headless runs are enabled.
        var headless = _options.Headless && HasSignedIn;
        if (headless)
        {
            _logger.LogInformation("Opening headless Softlink Edge. A window appears only if the Security Code is required.");
        }
        else if (_options.Headless)
        {
            _logger.LogInformation("Opening a visible Softlink Edge for the first Security Code sign-in.");
        }
        else
        {
            _logger.LogInformation("Opening Softlink Edge for Security Code + Log In.");
        }

        StartEdge(headless);
        var startup = headless
            ? TimeSpan.FromSeconds(30)
            : TimeSpan.FromMinutes(Math.Max(1, _options.LoginWaitMinutes));
        if (await WaitForPortAsync(cancellationToken, startup))
        {
            return;
        }

        if (!headless)
        {
            throw EdgeDidNotOpen();
        }

        _logger.LogWarning("Headless Softlink Edge did not open. Starting a visible window instead.");
        await ReleaseBrowserAsync();
        StopSoftlinkEdge();
        await WaitForPortClosedAsync(cancellationToken);
        StartEdge(headless: false);
        if (!await WaitForPortAsync(cancellationToken, TimeSpan.FromMinutes(Math.Max(1, _options.LoginWaitMinutes))))
        {
            throw EdgeDidNotOpen();
        }
    }

    private async Task<IPage> ConnectAndWaitForSignInAsync(CancellationToken cancellationToken)
    {
        var page = await AttachSoftlinkPageAsync();

        // A hidden Edge cannot show the captcha. Close it and open the normal window.
        if (await IsLoginPageAsync(page) && _options.Headless && EdgeIsHeadless())
        {
            _logger.LogInformation("Headless Softlink Edge is on Login. Opening a visible window for the Security Code.");
            await ReleaseBrowserAsync();
            StopSoftlinkEdge();
            await WaitForPortClosedAsync(cancellationToken);
            StartEdge(headless: false);
            if (!await WaitForPortAsync(cancellationToken, TimeSpan.FromSeconds(45)))
            {
                throw new LogisysException(
                    "Visible Softlink Edge did not open for the Security Code. Sign in, then run again.");
            }

            page = await AttachSoftlinkPageAsync();
        }

        var context = page.Context;

        // Softlink sometimes bounces back to Login when Attach connects. Wait in the same
        // window for you to sign in again — do not navigate away or autofill captcha.
        if (await IsLoginPageAsync(page))
        {
            page = await PickSoftlinkPageAsync(context);
            await page.BringToFrontAsync();
        }

        if (await IsLoginPageAsync(page))
        {
            var minutes = Math.Max(1, _options.LoginWaitMinutes);
            await TryFillLoginCredentialsAsync(page);
            _logger.LogInformation(
                "Softlink is on Login; User Id/Password filled from config when set. Waiting up to {Minutes} minute(s) for Security Code + Log In.",
                minutes);
            var deadline = DateTime.UtcNow.AddMinutes(minutes);
            var lastFill = DateTime.UtcNow;
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

                // Softlink sometimes clears the form; refill credentials every few seconds.
                // Never move focus here - the operator is typing the Security Code.
                if ((DateTime.UtcNow - lastFill).TotalSeconds >= 4)
                {
                    await TryFillLoginCredentialsAsync(page, focusSecurityCode: false);
                    lastFill = DateTime.UtcNow;
                }

                await page.WaitForTimeoutAsync(2_000);
            }
        }

        if (await IsLoginPageAsync(page))
        {
            await CaptureAsync(page, "still-on-login");
            throw new LogisysException(
                "Softlink is still on Login. User Id and Password are filled from config when set — type the Security Code, click Log In, Submit for Gurugram, and leave Edge open.");
        }

        await TrySubmitCompanyAsync(page);
        if (await IsLoginPageAsync(page))
        {
            throw new LogisysException(
                "Softlink sign-in did not finish. Complete Log In and Submit in the Edge window the service opened, leave it open, and the pipeline will continue.");
        }

        return page;
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

    /// <summary>
    /// Fills Softlink User Id + Password from Logisys options. Does not touch Security Code (captcha).
    /// </summary>
    private async Task TryFillLoginCredentialsAsync(IPage page, bool focusSecurityCode = true)
    {
        var userName = UserName;
        var password = (_options.Password ?? string.Empty);
        if (string.IsNullOrWhiteSpace(userName) && string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        try
        {
            if (!await IsLoginPageAsync(page))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(userName))
            {
                var user = page.GetByPlaceholder("User Id");
                if (await user.CountAsync() > 0 && await user.First.IsVisibleAsync())
                {
                    var current = await user.First.InputValueAsync();
                    if (!string.Equals(current, userName, StringComparison.Ordinal))
                    {
                        await user.First.FillAsync(userName);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(password))
            {
                var pass = page.GetByPlaceholder("Password", new PageGetByPlaceholderOptions { Exact = true });
                if (await pass.CountAsync() == 0)
                {
                    pass = page.Locator("input[type='password']:visible");
                }

                if (await pass.CountAsync() > 0 && await pass.First.IsVisibleAsync())
                {
                    var current = await pass.First.InputValueAsync();
                    if (!string.Equals(current, password, StringComparison.Ordinal))
                    {
                        await pass.First.FillAsync(password);
                    }
                }
            }

            // Leave focus on Security Code so the operator only types the captcha - but ONLY on
            // the first pass. The refill loop runs every 4s, and stealing focus on every pass
            // interrupted the operator mid-captcha and could fire validation that refreshed it.
            if (focusSecurityCode)
            {
                var security = page.GetByPlaceholder("Security Code");
                if (await security.CountAsync() > 0 && await security.First.IsVisibleAsync())
                {
                    await security.First.FocusAsync();
                }

                _logger.LogInformation(
                    "Filled Softlink login fields for {UserName}; enter Security Code and click Log In.",
                    string.IsNullOrWhiteSpace(userName) ? "(password only)" : userName);
            }
        }
        catch (PlaywrightException ex)
        {
            _logger.LogDebug(ex, "Could not autofill Softlink User Id / Password.");
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

    private async Task<IPage> AttachSoftlinkPageAsync()
    {
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

        // Hand the live context to the recorder so it can film from sign-in onwards.
        _contextAccessor.Current = context;
        await WidenHeadlessPagesAsync(context);

        var page = await PickSoftlinkPageAsync(context);
        page.SetDefaultTimeout(60_000);
        await page.BringToFrontAsync();
        _logger.LogInformation("Softlink using tab {Url}", page.Url);
        return page;
    }

    // Headless Edge defaults to a small viewport. Softlink's tab strip and footer
    // were built against a desktop window, so give every page the same size.
    private async Task WidenHeadlessPagesAsync(IBrowserContext context)
    {
        if (!EdgeIsHeadless())
        {
            return;
        }

        if (!_viewportHooked)
        {
            _viewportHooked = true;
            context.Page += (_, opened) => _ = WidenAsync(opened);
        }

        foreach (var opened in context.Pages)
        {
            await WidenAsync(opened);
        }
    }

    private static async Task WidenAsync(IPage page)
    {
        try
        {
            if (!page.IsClosed)
            {
                await page.SetViewportSizeAsync(1920, 1080);
            }
        }
        catch (PlaywrightException)
        {
        }
    }

    private void StartEdge(bool headless)
    {
        Directory.CreateDirectory(_profileDir);
        var edge = FindEdge() ?? throw new LogisysException("Microsoft Edge was not found.");
        var windowArgs = headless
            ? "--headless=new --window-size=1920,1080"
            : "--start-maximized";
        var args = string.Join(' ',
            $"--remote-debugging-port={DebugPort}",
            "--remote-allow-origins=*",
            $"--user-data-dir=\"{_profileDir}\"",
            "--no-first-run",
            "--no-default-browser-check",
            windowArgs,
            $"\"{BaseUrl}/Login\"");
        if (Process.Start(new ProcessStartInfo { FileName = edge, Arguments = args, UseShellExecute = false }) is null)
        {
            throw new LogisysException("Microsoft Edge could not be started.");
        }

        RememberEdgeMode(headless);
        _logger.LogInformation(headless
            ? "Started headless Softlink Edge on port {Port}."
            : "Started visible Softlink Edge on port {Port}.", DebugPort);
    }

    private async Task ReleaseBrowserAsync()
    {
        _contextAccessor.Current = null;
        _viewportHooked = false;
        _edgeHeadless = null;
        var browser = _browser;
        _browser = null;
        if (browser is null)
        {
            return;
        }

        try
        {
            await browser.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Closing the Playwright connection before restarting Edge.");
        }
    }

    private async Task<bool> WaitForPortAsync(CancellationToken cancellationToken, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline && !await PortOpenAsync(DebugPort, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(1_000, cancellationToken);
        }

        return await PortOpenAsync(DebugPort, cancellationToken);
    }

    private async Task WaitForPortClosedAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && await PortOpenAsync(DebugPort, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(400, cancellationToken);
        }

        if (await PortOpenAsync(DebugPort, cancellationToken))
        {
            StopSoftlinkEdge();
        }

        await Task.Delay(500, cancellationToken);
    }

    private void WaitForPortClosed()
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && PortOpen(DebugPort))
        {
            Thread.Sleep(300);
        }
    }

    private bool EdgeIsHeadless()
    {
        if (_edgeHeadless is bool cached)
        {
            return cached;
        }

        var headless = QueryEdgeHeadless();
        _edgeHeadless = headless;
        return headless;
    }

    private void RememberEdgeMode(bool headless) => _edgeHeadless = headless;

    private bool QueryEdgeHeadless()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments =
                    "-NoProfile -Command \"" +
                    "$rows = @(Get-CimInstance Win32_Process -Filter \\\"Name='msedge.exe'\\\" | " +
                    "Where-Object { $_.CommandLine -match 'remote-debugging-port=" + DebugPort + "' }); " +
                    "if ($rows | Where-Object { $_.CommandLine -match 'headless' }) { 'yes' } else { 'no' }\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(8_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Timed out checking whether Softlink Edge is headless.");
                }

                return false;
            }

            return output.Contains("yes", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not tell whether Softlink Edge is headless.");
            return false;
        }
    }

    private LogisysException EdgeDidNotOpen()
    {
        var minutes = Math.Max(1, _options.LoginWaitMinutes);
        return new LogisysException(
            $"Softlink Edge did not open within {minutes} minute(s). When Edge opens, enter Security Code, click Log In, Submit for Gurugram, and leave that window open.");
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

