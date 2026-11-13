using System.Text.RegularExpressions;
using LogisysOrgConverter.Data;
using Microsoft.Playwright;

namespace LogisysOrgConverter.Logisys;

public sealed record OrgImportSummary(bool IndiaUploaded, bool OverseasUploaded, string Message);

// Softlink: Organizations → ⋮ → Upload Organizations → Excel (India, then Overseas).
public sealed class OrganisationImportUploader
{
    private readonly ConversionStore _store;
    private readonly LogisysBrowserSession _browser;
    private readonly ILogger<OrganisationImportUploader> _logger;

    public OrganisationImportUploader(
        ConversionStore store,
        LogisysBrowserSession browser,
        ILogger<OrganisationImportUploader> logger)
    {
        _store = store;
        _browser = browser;
        _logger = logger;
    }

    public async Task<OrgImportSummary> UploadAsync(Guid runId, CancellationToken cancellationToken)
    {
        var india = await _store.GetExportFileAsync(runId, "india", cancellationToken);
        var overseas = await _store.GetExportFileAsync(runId, "overseas", cancellationToken);
        if (india is null && overseas is null)
        {
            throw new LogisysException("That conversion run was not found.");
        }

        var indiaBytes = india?.Bytes;
        var overseasBytes = overseas?.Bytes;
        var stamp = (india ?? overseas)!.Value.CreatedAtUtc.ToString("yyyyMMdd-HHmm");
        var hasIndia = indiaBytes is { Length: > 0 };
        var hasOverseas = overseasBytes is { Length: > 0 };
        if (!hasIndia && !hasOverseas)
        {
            return new OrgImportSummary(false, false, "This run has no India or overseas file to upload.");
        }

        var workDir = Path.Combine(Path.GetTempPath(), "logisys-org-import-" + runId.ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            string? indiaPath = null;
            string? overseasPath = null;
            if (hasIndia)
            {
                indiaPath = Path.Combine(workDir, $"OrgMaster-IN-{stamp}.xlsx");
                await File.WriteAllBytesAsync(indiaPath, indiaBytes!, cancellationToken);
            }

            if (hasOverseas)
            {
                overseasPath = Path.Combine(workDir, $"OrgMaster-Overseas-{stamp}.xlsx");
                await File.WriteAllBytesAsync(overseasPath, overseasBytes!, cancellationToken);
            }

            return await _browser.RunAsync(async (page, token) =>
            {
                await SoftlinkOrgNavigation.OpenOrganizationsListAsync(page, _browser.BaseUrl, token);
                await page.WaitForTimeoutAsync(1_000);

                var indiaOk = false;
                var overseasOk = false;
                if (indiaPath is not null)
                {
                    indiaOk = await UploadOneFileAsync(page, indiaPath, "India", token);
                }

                if (overseasPath is not null)
                {
                    // Softlink may close Organization Master / upload popup; return to the list.
                    page = await LogisysBrowserSession.EnsureLivePageAsync(page);
                    await SoftlinkOrgNavigation.OpenOrganizationsListAsync(page, _browser.BaseUrl, token);
                    await page.WaitForTimeoutAsync(800);
                    overseasOk = await UploadOneFileAsync(page, overseasPath, "Overseas", token);
                }

                var parts = new List<string>();
                if (hasIndia)
                {
                    parts.Add(indiaOk ? "India uploaded" : "India failed");
                }

                if (hasOverseas)
                {
                    parts.Add(overseasOk ? "Overseas uploaded" : "Overseas failed");
                }

                if ((hasIndia && !indiaOk) || (hasOverseas && !overseasOk))
                {
                    throw new LogisysException(
                        "Softlink organisation upload did not finish. " + string.Join("; ", parts)
                        + ". Leave Edge open on Organizations and try again.");
                }

                return new OrgImportSummary(indiaOk, overseasOk, string.Join("; ", parts) + ".");
            }, cancellationToken);
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDir))
                {
                    Directory.Delete(workDir, recursive: true);
                }
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not delete temp Softlink import folder {Dir}.", workDir);
            }
        }
    }

    private async Task<bool> UploadOneFileAsync(IPage listPage, string filePath, string label, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation("Softlink Upload Organizations: {Label} ({File})", label, Path.GetFileName(filePath));

        var uploadPage = await OpenUploadDialogAsync(listPage);
        AttachDialogAccept(uploadPage);

        try
        {
            await EnsureOrganizationDetailsSourceAsync(uploadPage);
            var fileInput = await FindFileInputAsync(uploadPage)
                ?? throw new LogisysException($"The Excel File box was not found for the {label} upload.");

            await fileInput.SetInputFilesAsync(filePath);
            await uploadPage.WaitForTimeoutAsync(500);

            var uploadBtn = uploadPage.Locator("#btnUpload");
            if (await uploadBtn.CountAsync() > 0 && await uploadBtn.First.IsVisibleAsync())
            {
                await uploadBtn.First.ClickAsync();
            }
            else if (!await ClickNamedAsync(uploadPage, "Upload"))
            {
                throw new LogisysException($"The Upload button was not found for the {label} file.");
            }

            // Softlink processes the sheet, then may alert / show a result grid.
            await uploadPage.WaitForTimeoutAsync(4_000);
            await WaitForUploadIdleAsync(uploadPage);
            await TryCloseUploadDialogAsync(uploadPage, listPage);
            return true;
        }
        catch (PlaywrightException ex) when (LogisysBrowserSession.IsClosedError(ex))
        {
            // Upload often closes its own popup after success.
            _logger.LogInformation("Softlink upload popup closed after {Label} (treated as done).", label);
            return true;
        }
    }

    private static async Task<IPage> OpenUploadDialogAsync(IPage listPage)
    {
        var orgFrame = FindOrganizationListFrame(listPage)
            ?? throw new LogisysException(
                "Softlink Organizations list was not found. Open Directories → Organizations, leave Edge open, then try again.");

        // Softlink: #ToolBarsCtrl1_dvMore → menu → #ToolBarsCtrl1_dvMoreMenu_UploadOrganizations
        // opens UploadOrganization.aspx in a new window.
        var popupTask = listPage.Context.WaitForPageAsync(new BrowserContextWaitForPageOptions { Timeout = 15_000 });

        var more = orgFrame.Locator("#ToolBarsCtrl1_dvMore");
        if (await more.CountAsync() == 0 || !await more.First.IsVisibleAsync())
        {
            more = orgFrame.Locator("#ToolBarsCtrl1_imgMore");
        }

        if (await more.CountAsync() == 0 || !await more.First.IsVisibleAsync())
        {
            throw new LogisysException(
                "Could not find Softlink Organizations ⋯ (More Options). Open Organizations, leave Edge open, then try again.");
        }

        await more.First.ClickAsync();
        var menu = orgFrame.Locator("#ToolBarsCtrl1_dvMainMoreMenu");
        await menu.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });

        var uploadItem = orgFrame.Locator("#ToolBarsCtrl1_dvMoreMenu_UploadOrganizations");
        if (await uploadItem.CountAsync() == 0 || !await uploadItem.First.IsVisibleAsync())
        {
            throw new LogisysException(
                "Softlink ⋯ menu opened but Upload Organizations was not visible. Try again.");
        }

        await uploadItem.First.ClickAsync();

        IPage? popup = null;
        try
        {
            popup = await popupTask;
        }
        catch (TimeoutException)
        {
            popup = null;
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
        {
            popup = null;
        }

        // Popup may already exist from a prior attempt.
        popup ??= listPage.Context.Pages.LastOrDefault(p =>
            (p.Url ?? string.Empty).Contains("UploadOrganization", StringComparison.OrdinalIgnoreCase)
            && !p.IsClosed);

        if (popup is null)
        {
            throw new LogisysException(
                "Upload Organization window did not open. In Softlink click Organizations → ⋯ → Upload Organizations, then try again.");
        }

        popup.SetDefaultTimeout(60_000);
        await popup.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await popup.WaitForTimeoutAsync(500);
        return popup;
    }

    private static IFrame? FindOrganizationListFrame(IPage page)
    {
        foreach (var frame in page.Frames)
        {
            var url = frame.Url ?? string.Empty;
            if (url.Contains("OrganizationList", StringComparison.OrdinalIgnoreCase)
                || url.Contains("OrganisationList", StringComparison.OrdinalIgnoreCase))
            {
                return frame;
            }
        }

        return null;
    }

    private static async Task EnsureOrganizationDetailsSourceAsync(IPage page)
    {
        var select = page.Locator("#ddlTypeOfSource");
        if (await select.CountAsync() == 0)
        {
            return;
        }

        try
        {
            await select.First.SelectOptionAsync(new SelectOptionValue { Label = "Organization Details" });
        }
        catch (PlaywrightException)
        {
            try
            {
                await select.First.SelectOptionAsync(new SelectOptionValue { Value = "Organization" });
            }
            catch (PlaywrightException)
            {
            }
        }
    }

    private static async Task<ILocator?> FindFileInputAsync(IPage page)
    {
        var exact = page.Locator("#fileUploadExcel, input[name='fileUploadExcel'], input[type='file']");
        if (await exact.CountAsync() > 0)
        {
            return exact.First;
        }

        return null;
    }

    private static async Task WaitForUploadIdleAsync(IPage page)
    {
        // Give Softlink time to finish processing; dismiss leftover alerts.
        for (var i = 0; i < 30; i++)
        {
            if (page.IsClosed)
            {
                return;
            }

            try
            {
                var busy = page.Locator(".blockUI, .loading, [class*='progress' i], [id*='progress' i]");
                if (await busy.CountAsync() == 0 || !await busy.First.IsVisibleAsync())
                {
                    // Result text often appears after import.
                    var done = page.GetByText(new Regex("success|uploaded|imported|error|failed|complete", RegexOptions.IgnoreCase));
                    if (await done.CountAsync() > 0)
                    {
                        await page.WaitForTimeoutAsync(800);
                        return;
                    }
                }
            }
            catch (PlaywrightException)
            {
                return;
            }

            await page.WaitForTimeoutAsync(1_000);
        }
    }

    private static async Task TryCloseUploadDialogAsync(IPage uploadPage, IPage listPage)
    {
        if (uploadPage.IsClosed)
        {
            return;
        }

        var closeBtn = uploadPage.Locator("#btnClose");
        if (await closeBtn.CountAsync() > 0 && await closeBtn.First.IsVisibleAsync())
        {
            await closeBtn.First.ClickAsync();
        }
        else if (!await ClickNamedAsync(uploadPage, "Close") && !await ClickNamedAsync(uploadPage, "Cancel"))
        {
            try
            {
                if (!ReferenceEquals(uploadPage, listPage))
                {
                    await uploadPage.CloseAsync();
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        await listPage.WaitForTimeoutAsync(600);
    }

    private static async Task<bool> HasUploadDialogAsync(IPage page)
    {
        foreach (var root in Roots(page))
        {
            try
            {
                var title = root.GetByText(new Regex(@"Upload\s+Organi[sz]ation", RegexOptions.IgnoreCase));
                if (await title.CountAsync() > 0 && await title.First.IsVisibleAsync())
                {
                    return true;
                }

                if (await root.Locator("input[type='file']").CountAsync() > 0
                    && await root.GetByText("Excel File", new() { Exact = false }).CountAsync() > 0)
                {
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        var url = page.Url ?? string.Empty;
        return url.Contains("UploadOrganization", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> ClickNamedAsync(IPage page, string name)
    {
        foreach (var root in Roots(page))
        {
            try
            {
                var button = root.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
                if (await button.CountAsync() > 0 && await button.First.IsVisibleAsync())
                {
                    await button.First.ClickAsync();
                    return true;
                }

                var input = root.Locator($"input[type='button'][value='{name}'], input[type='submit'][value='{name}']");
                if (await input.CountAsync() > 0 && await input.First.IsVisibleAsync())
                {
                    await input.First.ClickAsync();
                    return true;
                }

                var text = root.GetByText(name, new() { Exact = true });
                if (await text.CountAsync() > 0 && await text.First.IsVisibleAsync())
                {
                    await text.First.ClickAsync();
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        return false;
    }

    private static void AttachDialogAccept(IPage page)
    {
        page.Dialog += async (_, dialog) =>
        {
            try
            {
                await dialog.AcceptAsync();
            }
            catch (PlaywrightException)
            {
            }
        };
    }

    private static IEnumerable<ILocator> Roots(IPage page)
    {
        yield return page.Locator("body");
        foreach (var frame in page.Frames)
        {
            if (frame == page.MainFrame)
            {
                continue;
            }

            yield return frame.Locator("body");
        }
    }
}
