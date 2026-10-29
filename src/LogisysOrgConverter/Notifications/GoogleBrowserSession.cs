using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace LogisysOrgConverter.Notifications;

public sealed partial class GoogleBrowserSession
{
    private readonly FormMailbox _mailbox;
    private readonly GoogleOptions _google;
    private readonly GoogleVisitorOptions _visitor;
    private readonly string _profileDir;
    private readonly string _markerPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GoogleBrowserSession(
        FormMailbox mailbox,
        IOptions<GoogleOptions> google,
        IOptions<GoogleVisitorOptions> visitor,
        IWebHostEnvironment environment)
    {
        _mailbox = mailbox;
        _google = google.Value;
        _visitor = visitor.Value;
        _profileDir = Path.Combine(environment.ContentRootPath, "GoogleSession");
        _markerPath = Path.Combine(_profileDir, ".signed-in");
    }

    public bool HasSignedIn => File.Exists(_markerPath);

    public async Task<byte[]> DownloadAsync(string kind, string sourceId, CancellationToken cancellationToken, string? openUrl = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await RunAsync(kind, sourceId, openUrl, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<byte[]> RunAsync(string kind, string sourceId, string? openUrl, CancellationToken cancellationToken)
    {
        var target = !string.IsNullOrWhiteSpace(openUrl)
            ? openUrl
            : kind == FormSourceKinds.Form
                ? "https://docs.google.com/forms/d/" + sourceId + "/edit#responses"
                : FormNoticeParser.WithFormResponsesTab(
                    "https://docs.google.com/spreadsheets/d/" + sourceId + "/edit?usp=sharing");
        Directory.CreateDirectory(_profileDir);
        using var playwright = await Playwright.CreateAsync();
        var channel = string.IsNullOrWhiteSpace(_visitor.BrowserChannel) ? "msedge" : _visitor.BrowserChannel.Trim();
        await using var context = await playwright.Chromium.LaunchPersistentContextAsync(_profileDir, new BrowserTypeLaunchPersistentContextOptions
        {
            Channel = channel,
            Headless = false,
            AcceptDownloads = true,
            Args = ["--start-maximized", "--disable-blink-features=AutomationControlled"],
            IgnoreDefaultArgs = ["--enable-automation"]
        });
        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
        page.SetDefaultTimeout(60_000);
        try
        {
            await page.GotoAsync(target, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.BringToFrontAsync();
            if (await NeedsVisitorSignInAsync(page))
            {
                await RequestCodeAndSignInAsync(page, cancellationToken);
                await page.BringToFrontAsync();
            }

            await RejectIfNoAccessAsync(page);
            if (kind == FormSourceKinds.Sheet)
            {
                await OpenFormResponsesTabAsync(page);
            }

            var sheetId = kind == FormSourceKinds.Form
                ? await FindSheetIdAsync(page, cancellationToken)
                : sourceId;
            var exportUrl = "https://docs.google.com/spreadsheets/d/" + sheetId + "/export?format=xlsx";
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xlsx");
            await SaveExportAsync(page, exportUrl, path, cancellationToken);
            try
            {
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                if (bytes.Length < 4 || bytes[0] != 0x50 || bytes[1] != 0x4B)
                {
                    throw new FormSheetException("Google did not return the response sheet.");
                }

                await File.WriteAllTextAsync(_markerPath, DateTime.UtcNow.ToString("o"), cancellationToken);
                return bytes;
            }
            finally
            {
                File.Delete(path);
            }
        }
        catch (FormSheetException)
        {
            throw;
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("channel", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("msedge", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormSheetException("Microsoft Edge is not available, so the form could not be opened.");
        }
        catch (PlaywrightException ex)
        {
            throw new FormSheetException("The form could not be opened. " + FirstLine(ex.Message));
        }
        catch (TimeoutException)
        {
            throw new FormSheetException("Opening the spreadsheet timed out. Load it again.");
        }
    }

    private static async Task SaveExportAsync(IPage page, string exportUrl, string path, CancellationToken cancellationToken)
    {
        var bytes = await TryRequestBytesAsync(page, exportUrl);
        if (!IsWorkbook(bytes))
        {
            bytes = await TryPageFetchAsync(page, exportUrl);
        }

        if (!IsWorkbook(bytes))
        {
            bytes = await TryBrowserDownloadAsync(page, exportUrl, cancellationToken);
        }

        if (!IsWorkbook(bytes))
        {
            throw new FormSheetException(ExplainMissingWorkbook(bytes));
        }

        await File.WriteAllBytesAsync(path, bytes!, cancellationToken);
    }

    private static async Task<byte[]?> TryRequestBytesAsync(IPage page, string exportUrl)
    {
        try
        {
            var response = await page.Context.APIRequest.GetAsync(exportUrl, new APIRequestContextOptions { Timeout = 90_000 });
            return await response.BodyAsync();
        }
        catch (PlaywrightException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> TryPageFetchAsync(IPage page, string exportUrl)
    {
        try
        {
            var encoded = await page.EvaluateAsync<string>(
                """
                async (url) => {
                  const response = await fetch(url, { credentials: "include" });
                  const buffer = await response.arrayBuffer();
                  const bytes = new Uint8Array(buffer);
                  const chunk = 0x8000;
                  let binary = "";
                  for (let i = 0; i < bytes.length; i += chunk) {
                    binary += String.fromCharCode.apply(null, bytes.subarray(i, Math.min(i + chunk, bytes.length)));
                  }
                  return btoa(binary);
                }
                """,
                exportUrl);
            return string.IsNullOrEmpty(encoded) ? null : Convert.FromBase64String(encoded);
        }
        catch (PlaywrightException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> TryBrowserDownloadAsync(IPage page, string exportUrl, CancellationToken cancellationToken)
    {
        var downloadSource = new TaskCompletionSource<IDownload>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDownload(object? sender, IDownload download) => downloadSource.TrySetResult(download);
        page.Download += OnDownload;
        var saved = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            try
            {
                await page.GotoAsync(exportUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Commit, Timeout = 30_000 });
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Download is starting", StringComparison.OrdinalIgnoreCase))
            {
            }

            var downloadLink = page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Download anyway" });
            if (await downloadLink.CountAsync() > 0 && await downloadLink.First.IsVisibleAsync())
            {
                await downloadLink.First.ClickAsync();
            }

            var download = await downloadSource.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            await download.SaveAsAsync(saved);
            return await File.ReadAllBytesAsync(saved, cancellationToken);
        }
        catch (PlaywrightException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            page.Download -= OnDownload;
            if (File.Exists(saved))
            {
                File.Delete(saved);
            }
        }
    }

    private static bool IsWorkbook(byte[]? bytes) =>
        bytes is { Length: >= 4 } && bytes[0] == 0x50 && bytes[1] == 0x4B;

    private static string ExplainMissingWorkbook(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            return "Google did not return Organisation Creation Request Form (Responses).";
        }

        var text = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 400));
        if (text.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase))
        {
            return "Google asked for the verification code again, and the sheet was not downloaded.";
        }

        if (text.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Page not found", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unable to open the file", StringComparison.OrdinalIgnoreCase))
        {
            return "Google could not open Organisation Creation Request Form (Responses).";
        }

        return "Google did not return Organisation Creation Request Form (Responses).";
    }

    private async Task RequestCodeAndSignInAsync(IPage page, CancellationToken cancellationToken)
    {
        var hint = string.IsNullOrWhiteSpace(_google.LoginHint) ? _mailbox.Address : _google.LoginHint.Trim();
        var minutes = _visitor.CodeWaitMinutes < 1 ? 3 : _visitor.CodeWaitMinutes;
        var deadline = DateTimeOffset.UtcNow.AddMinutes(minutes);
        var giveUpControls = DateTimeOffset.UtcNow.AddSeconds(25);
        DateTimeOffset? requestedAt = null;
        string? usedCode = null;
        var emailSubmitted = false;
        var sendClicked = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await NeedsVisitorSignInAsync(page))
            {
                return;
            }

            if (!emailSubmitted && await SubmitEmailAsync(page, hint))
            {
                emailSubmitted = true;
                requestedAt = DateTimeOffset.UtcNow;
            }
            else if (!sendClicked && await ClickSendAsync(page))
            {
                sendClicked = true;
                requestedAt = DateTimeOffset.UtcNow;
            }

            if (requestedAt is DateTimeOffset sent && usedCode is null)
            {
                var code = await _mailbox.RecentSignInCodeAsync(sent, cancellationToken);
                if (!string.IsNullOrEmpty(code) && await EnterCodeAsync(page, code))
                {
                    usedCode = code;
                }
            }
            else if (requestedAt is null && DateTimeOffset.UtcNow > giveUpControls)
            {
                throw new FormSheetException("Edge is open at " + page.Url + ". The verification box was not found. Use " + hint + " in that window, then load the spreadsheet again.");
            }

            await page.WaitForTimeoutAsync(2000);
        }

        throw new FormSheetException(
            "Google did not accept the code sent to " + hint + ". Open the sheet again so a new code can be requested.");
    }

    private static async Task<bool> SubmitEmailAsync(IPage page, string email)
    {
        var input = page.Locator("input[type='email'], input[name='identifier']");
        if (await input.CountAsync() == 0 || !await input.First.IsVisibleAsync())
        {
            return false;
        }

        await input.First.FillAsync(email);
        var next = page.Locator("#identifierNext");
        if (await next.CountAsync() > 0 && await next.First.IsVisibleAsync())
        {
            await next.First.ClickAsync();
            return true;
        }

        if (await ClickButtonAsync(page, "Next") || await ClickButtonAsync(page, "Send"))
        {
            return true;
        }

        await input.First.PressAsync("Enter");
        return true;
    }

    private static async Task<bool> ClickSendAsync(IPage page)
    {
        return await ClickButtonAsync(page, "Send")
            || await ClickButtonAsync(page, "Resend")
            || await ClickTextAsync(page, "Get a verification code")
            || await ClickTextAsync(page, "Email code");
    }

    private static async Task<bool> EnterCodeAsync(IPage page, string code)
    {
        var input = page.Locator("input[type='tel'], input[autocomplete='one-time-code'], input[name='code'], input[name='pin']");
        if (await input.CountAsync() == 0 || !await input.First.IsVisibleAsync())
        {
            return false;
        }

        await input.First.FillAsync(code);
        await ClickButtonAsync(page, "Next");
        await ClickButtonAsync(page, "Verify");
        await ClickButtonAsync(page, "Continue");
        return true;
    }

    private static async Task<bool> ClickButtonAsync(IPage page, string name)
    {
        var button = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = name });
        if (await button.CountAsync() == 0 || !await button.First.IsVisibleAsync())
        {
            return false;
        }

        await button.First.ClickAsync();
        return true;
    }

    private static async Task<bool> ClickTextAsync(IPage page, string text)
    {
        var locator = page.GetByText(text, new PageGetByTextOptions { Exact = false });
        if (await locator.CountAsync() == 0 || !await locator.First.IsVisibleAsync())
        {
            return false;
        }

        await locator.First.ClickAsync();
        return true;
    }

    private static async Task<bool> NeedsVisitorSignInAsync(IPage page)
    {
        if (IsLogin(page.Url))
        {
            return true;
        }

        var email = page.Locator("input[type='email'], input[name='identifier']");
        if (await email.CountAsync() > 0 && await email.First.IsVisibleAsync())
        {
            return true;
        }

        var pin = page.Locator("input[type='tel'], input[autocomplete='one-time-code']");
        return await pin.CountAsync() > 0 && await pin.First.IsVisibleAsync();
    }

    private static async Task OpenFormResponsesTabAsync(IPage page)
    {
        if (page.Url.Contains("gid=" + FormNotice.FormResponsesGid, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var names = new[] { "Form Responses 1", "Form_Responses1", "Form Responses" };
        foreach (var name in names)
        {
            var tab = page.GetByText(name, new PageGetByTextOptions { Exact = true });
            if (await tab.CountAsync() == 0)
            {
                continue;
            }

            try
            {
                await tab.First.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                await page.WaitForTimeoutAsync(1000);
                return;
            }
            catch (PlaywrightException)
            {
            }
        }

        var withTab = FormNoticeParser.WithFormResponsesTab(page.Url);
        if (!string.Equals(withTab, page.Url, StringComparison.Ordinal))
        {
            await page.GotoAsync(withTab, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        }
    }

    private static async Task RejectIfNoAccessAsync(IPage page)
    {
        var text = await page.Locator("body").InnerTextAsync(new LocatorInnerTextOptions { Timeout = 15_000 });
        if (text.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Page not found", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormSheetException("Google could not open the spreadsheet from that link. Load it again so the link in the Spreadsheet folder is used.");
        }

        if (text.Contains("You need permission", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Request access", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormSheetException("Google opened the file, but this sign-in does not have access. Choose riya@awgmen.com in the Edge window.");
        }
    }

    private static async Task<string> FindSheetIdAsync(IPage page, CancellationToken cancellationToken)
    {
        var html = await page.ContentAsync();
        var match = SpreadsheetId().Match(html);
        if (match.Success)
        {
            return match.Groups["id"].Value;
        }

        var link = page.Locator("a[href*='spreadsheets/d/']");
        var wait = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < wait)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await link.CountAsync() > 0)
            {
                var href = await link.First.GetAttributeAsync("href") ?? string.Empty;
                match = SpreadsheetId().Match(href);
                if (match.Success)
                {
                    return match.Groups["id"].Value;
                }
            }

            html = await page.ContentAsync();
            match = SpreadsheetId().Match(html);
            if (match.Success)
            {
                return match.Groups["id"].Value;
            }

            await page.WaitForTimeoutAsync(1000);
        }

        throw new FormSheetException("The form opened, but its response spreadsheet was not linked on the page.");
    }

    private static bool IsLogin(string url) =>
        url.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase);

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 240 ? line[..240] : line;
    }

    [GeneratedRegex(@"spreadsheets/d/(?<id>[a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SpreadsheetId();

}
