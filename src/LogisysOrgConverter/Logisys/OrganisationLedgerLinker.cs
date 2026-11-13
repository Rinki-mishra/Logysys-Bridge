using System.Text.Json;
using LogisysOrgConverter.Conversion;
using LogisysOrgConverter.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace LogisysOrgConverter.Logisys;

public sealed record LedgerLinkSummary(int Organisations, int Linked, int Skipped, int Failed, string Message);

// India Softlink ledger G/L only:
// Shipper/Consignee → Account Info → Customer → Customer G/L → TPY Receivable (+ Receivable).
// Agent/Sub-Agent/Carrier → Account Info → Vendor → Vendor G/L → TPY Payable (+ Payable).
// Overseas is out of scope for now.
public sealed class OrganisationLedgerLinker
{
    private const string ReceivableGl = "TPY Receivable";
    private const string PayableGl = "TPY Payable";

    private readonly AppDbContext _db;
    private readonly LogisysBrowserSession _browser;
    private readonly ILogger<OrganisationLedgerLinker> _logger;

    public OrganisationLedgerLinker(
        AppDbContext db,
        LogisysBrowserSession browser,
        ILogger<OrganisationLedgerLinker> logger)
    {
        _db = db;
        _browser = browser;
        _logger = logger;
    }

    public async Task<LedgerLinkSummary> LinkAsync(Guid runId, string? onlyName, CancellationToken cancellationToken)
    {
        // Only recently uploaded orgs (marked Created after Softlink Excel import) — not Update/already-in-Logi-Sys.
        var query = _db.RunRows.Where(row => row.RunId == runId
            && row.ImportStatus == ImportStatuses.Created
            && row.LedgerJson != null
            && row.LedgerLinkedAtUtc == null
            && row.LedgerLinkStatus != "Done"
            && row.LedgerLinkStatus != "Skipped");

        if (!string.IsNullOrWhiteSpace(onlyName))
        {
            var key = SoftlinkOrgNavigation.Key(onlyName);
            var all = await query.OrderBy(row => row.SourceRowNumber).ToListAsync(cancellationToken);
            var rows = all.Where(row => SoftlinkOrgNavigation.Key(row.OrganisationName).Contains(key, StringComparison.Ordinal))
                .Where(IsIndiaLedgerCandidate)
                .ToList();
            if (rows.Count == 0)
            {
                return new LedgerLinkSummary(0, 0, 0, 0,
                    $"No Created India ledger organisation matching \"{onlyName.Trim()}\". Upload/import in Softlink, mark Created here, then link G/L.");
            }

            return await LinkRowsAsync(rows, cancellationToken);
        }

        var candidates = (await query.OrderBy(row => row.SourceRowNumber).ToListAsync(cancellationToken))
            .Where(IsIndiaLedgerCandidate)
            .ToList();
        if (candidates.Count == 0)
        {
            return new LedgerLinkSummary(0, 0, 0, 0,
                "No Created India organisations waiting for Softlink G/L link. Upload the India file, mark Created, then link. Overseas is skipped for now.");
        }

        return await LinkRowsAsync(candidates, cancellationToken);
    }

    private async Task<LedgerLinkSummary> LinkRowsAsync(List<ConversionRunRow> rows, CancellationToken cancellationToken)
    {
        return await _browser.RunAsync(async (page, token) =>
        {
            var linked = 0;
            var skipped = 0;
            var failed = 0;

            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                page = await LogisysBrowserSession.EnsureLivePageAsync(page);
                if (await LogisysBrowserSession.IsLoginPageAsync(page))
                {
                    throw new LogisysException(
                        "Softlink is on Login. Click Sign in to Softlink, finish captcha + Log In + Submit, leave Edge open, then link India G/L again.");
                }

                try
                {
                    var roles = ResolveRoles(row);
                    if (!roles.Receivable && !roles.Payable)
                    {
                        row.LedgerLinkStatus = "Skipped";
                        row.LedgerLinkMessage =
                            $"Skipped Softlink G/L — not shipper/consignee/agent/carrier ({DateTime.UtcNow:u}).";
                        skipped++;
                        await _db.SaveChangesAsync(token);
                        continue;
                    }

                    var master = await SoftlinkOrgNavigation.OpenOrganisationAsync(
                        page, _browser.BaseUrl, row.OrganisationName, token);
                    var detail = await LinkOrganisationGlAsync(master, row.OrganisationName, roles);
                    row.LedgerLinkedAtUtc = DateTime.UtcNow;
                    row.LedgerLinkStatus = "Done";
                    row.LedgerLinkMessage = detail;
                    linked++;
                    await _db.SaveChangesAsync(token);
                }
                catch (Exception ex) when (ex is LogisysException or PlaywrightException or TimeoutException)
                {
                    var onLogin = false;
                    try
                    {
                        page = await LogisysBrowserSession.EnsureLivePageAsync(page);
                        onLogin = ex.Message.Contains("Login", StringComparison.OrdinalIgnoreCase)
                            || await LogisysBrowserSession.IsLoginPageAsync(page);
                    }
                    catch (PlaywrightException)
                    {
                    }

                    row.LedgerLinkStatus = "Failed";
                    row.LedgerLinkMessage = FirstLine(ex.Message);
                    await _db.SaveChangesAsync(token);
                    failed++;
                    _logger.LogWarning(ex, "Softlink India G/L link failed for {Name}", row.OrganisationName);

                    if (onLogin)
                    {
                        throw new LogisysException(
                            "Softlink returned to Login. Sign in again, leave Edge open, then link India G/L again.");
                    }
                }
                finally
                {
                    // Always clear leftover Master windows so the next organisation can open.
                    try
                    {
                        page = await LogisysBrowserSession.EnsureLivePageAsync(page);
                        await SoftlinkOrgNavigation.CloseOrgMasterPagesAsync(page.Context, keep: page);
                    }
                    catch (PlaywrightException)
                    {
                    }
                }
            }

            var message = linked == 0 && failed == 0
                ? $"Checked {rows.Count} India organisation(s); {skipped} skipped."
                : $"Linked Softlink G/L for {linked} of {rows.Count} India organisation(s)"
                  + (skipped > 0 ? $", {skipped} skipped" : string.Empty)
                  + (failed > 0 ? $", {failed} failed" : string.Empty)
                  + ".";
            return new LedgerLinkSummary(rows.Count, linked, skipped, failed, message);
        }, cancellationToken);
    }

    private static bool IsIndiaLedgerCandidate(ConversionRunRow row)
    {
        var checklist = LedgerJson.Read(row.LedgerJson);
        if (checklist is null)
        {
            return false;
        }

        if (string.Equals(checklist.Category, "India", StringComparison.OrdinalIgnoreCase))
        {
            return checklist.Sections.Count > 0;
        }

        if (string.Equals(row.Template, "India", StringComparison.OrdinalIgnoreCase)
            || string.Equals(row.Country, MappingCatalog.IndiaCountry, StringComparison.OrdinalIgnoreCase))
        {
            var roles = ResolveRoles(row);
            return roles.Receivable || roles.Payable;
        }

        return false;
    }

    private static (bool Receivable, bool Payable) ResolveRoles(ConversionRunRow row)
    {
        var checklist = LedgerJson.Read(row.LedgerJson);
        if (checklist is not null && string.Equals(checklist.Category, "India", StringComparison.OrdinalIgnoreCase))
        {
            var receivable = checklist.Sections.Any(section =>
                section.Title.Contains("receivable", StringComparison.OrdinalIgnoreCase));
            var payable = checklist.Sections.Any(section =>
                section.Title.Contains("payable", StringComparison.OrdinalIgnoreCase));
            if (receivable || payable)
            {
                return (receivable, payable);
            }
        }

        var flag = ReadOrgFlag(row.CellsJson);
        if (!string.IsNullOrWhiteSpace(flag))
        {
            return RolesFromFlag(flag);
        }

        return (false, false);
    }

    private static string? ReadOrgFlag(string? cellsJson)
    {
        if (string.IsNullOrWhiteSpace(cellsJson))
        {
            return null;
        }

        try
        {
            var cells = JsonSerializer.Deserialize<Dictionary<string, string?>>(cellsJson);
            if (cells is null)
            {
                return null;
            }

            if (cells.TryGetValue("X", out var x) && !string.IsNullOrWhiteSpace(x))
            {
                return x;
            }

            if (cells.TryGetValue("OrgFlag", out var flag) && !string.IsNullOrWhiteSpace(flag))
            {
                return flag;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static (bool Receivable, bool Payable) RolesFromFlag(string raw)
    {
        var receivable = false;
        var payable = false;
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var text = token.ToLowerInvariant();
            if (text.Contains("shipper", StringComparison.Ordinal) || text.Contains("consignee", StringComparison.Ordinal))
            {
                receivable = true;
            }

            if (text.Contains("agent", StringComparison.Ordinal)
                || text.Contains("carrier", StringComparison.Ordinal)
                || text.Contains("shipping line", StringComparison.Ordinal)
                || text.Contains("airline", StringComparison.Ordinal))
            {
                payable = true;
            }
        }

        return (receivable, payable);
    }

    private static async Task<string> LinkOrganisationGlAsync(
        IPage masterOrList,
        string organisationName,
        (bool Receivable, bool Payable) roles)
    {
        // Softlink opens Organization Master as its own Edge page — never keep working on NewHomePage.
        var page = await SoftlinkOrgNavigation.FindOrganizationMasterPageAsync(masterOrList, organisationName)
            ?? await SoftlinkOrgNavigation.TryResolveOrgMasterPageAsync(masterOrList, organisationName)
            ?? throw new LogisysException(
                $"Organisation Master for \"{organisationName}\" did not open. Search by Name, click toolbar Edit, then try again.");

        await page.BringToFrontAsync();
        page.SetDefaultTimeout(60_000);
        await page.WaitForTimeoutAsync(800);

        // Prefer form/ledger roles; Softlink type checkboxes are the fallback.
        var receivable = roles.Receivable;
        var payable = roles.Payable;
        if (!receivable && !payable)
        {
            var softlinkRoles = await ReadSoftlinkOrgTypesAsync(page);
            receivable = softlinkRoles.Receivable;
            payable = softlinkRoles.Payable;
        }

        if (!receivable && !payable)
        {
            await SafeCloseMasterAsync(page);
            throw new LogisysException(
                "Could not determine shipper/consignee vs agent/carrier on Organization Master.");
        }

        // Softlink-native controls (verified on OrganizationMaster.aspx):
        // Account Info = #tsMainItem6 / tsMain_Handler.clickTab(5)
        // Edit = #TaskButtons1_dvEdit, Save & Close = #TaskButtons1_dvSaveClose
        // Customer/Vendor = #tabCustVendAccInfoItem1 / Item2
        if (!await SoftlinkClickAccountInfoAsync(page))
        {
            throw new LogisysException(
                "Could not open Account Info (tsMainItem6). Leave Organization Master open and try again.");
        }

        if (!await SoftlinkClickEditAsync(page))
        {
            throw new LogisysException(
                "Could not click Edit (TaskButtons1_dvEdit) on Organization Master.");
        }

        // Edit can bounce Softlink back to General — open Account Info again.
        if (!await SoftlinkClickAccountInfoAsync(page))
        {
            throw new LogisysException("Account Info did not stay open after Edit.");
        }

        var parts = new List<string>();
        if (receivable)
        {
            await EnsureCustomerGlAsync(page, ReceivableGl);
            parts.Add($"Customer G/L → {ReceivableGl}");
        }

        if (payable)
        {
            await EnsureVendorGlAsync(page, PayableGl);
            parts.Add($"Vendor G/L → {PayableGl}");
        }

        if (!await SoftlinkSaveAndCloseAsync(page))
        {
            throw new LogisysException("Save & Close (TaskButtons1_dvSaveClose) was not available after linking G/L.");
        }

        return string.Join("; ", parts) + $" ({DateTime.UtcNow:u}).";
    }

    // Softlink tab strip: LI#tsMainItem6 onclick="tsMain_Handler.clickTab(5)".
    // Must use a DOM click — calling clickTab() from Playwright eval hits strict-mode errors.
    private static async Task<bool> SoftlinkClickAccountInfoAsync(IPage page)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (await SoftlinkAccountInfoActiveAsync(page))
            {
                return true;
            }

            var clicked = await page.EvaluateAsync<bool>("""
                () => {
                  const li = document.getElementById('tsMainItem6')
                    || [...document.querySelectorAll('li[id^="tsMainItem"]')].find(n =>
                         ((n.innerText || n.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase() === 'account info'));
                  if (!li) return false;
                  li.scrollIntoView({ block: 'center', inline: 'nearest' });
                  li.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                  return true;
                }
                """);
            if (!clicked)
            {
                await ClickSoftlinkTabAsync(page, "Account Info");
            }

            await page.WaitForTimeoutAsync(900);
            if (await SoftlinkAccountInfoActiveAsync(page))
            {
                return true;
            }
        }

        return await SoftlinkAccountInfoActiveAsync(page);
    }

    private static async Task<bool> SoftlinkAccountInfoActiveAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>("""
                () => {
                  // Softlink marks the active main tab with class linkOn — do not use hidden panel presence.
                  const li = document.getElementById('tsMainItem6');
                  return !!(li && (li.className || '').toString().includes('linkOn'));
                }
                """);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task<bool> SoftlinkClickEditAsync(IPage page)
    {
        if (await SoftlinkIsEditingAsync(page))
        {
            return true;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var clicked = await page.EvaluateAsync<bool>("""
                () => {
                  const btn = document.getElementById('TaskButtons1_dvEdit');
                  if (!btn || btn.disabled || btn.classList.contains('aspNetDisabled')) return false;
                  btn.scrollIntoView({ block: 'center', inline: 'nearest' });
                  btn.click();
                  return true;
                }
                """);
            if (!clicked)
            {
                await ClickMasterFooterAsync(page, "Edit");
            }

            await page.WaitForTimeoutAsync(1_000);
            if (await SoftlinkIsEditingAsync(page))
            {
                return true;
            }
        }

        return await SoftlinkIsEditingAsync(page);
    }

    private static async Task<bool> SoftlinkIsEditingAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>("""
                () => {
                  const edit = document.getElementById('TaskButtons1_dvEdit');
                  const save = document.getElementById('TaskButtons1_dvSave')
                    || document.getElementById('TaskButtons1_dvSaveClose');
                  if (!edit || !save) return false;
                  const editOff = edit.disabled || edit.classList.contains('aspNetDisabled');
                  const saveOn = !save.disabled && !save.classList.contains('aspNetDisabled');
                  return editOff && saveOn;
                }
                """);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task<bool> SoftlinkSaveAndCloseAsync(IPage page)
    {
        try
        {
            var clicked = await page.EvaluateAsync<bool>("""
                () => {
                  const btn = document.getElementById('TaskButtons1_dvSaveClose')
                    || document.getElementById('TaskButtons1_dvSave');
                  if (!btn || btn.disabled || btn.classList.contains('aspNetDisabled')) return false;
                  btn.scrollIntoView({ block: 'center', inline: 'nearest' });
                  btn.click();
                  return true;
                }
                """);
            if (!clicked && !await ClickMasterFooterAsync(page, "Save & Close") && !await ClickMasterFooterAsync(page, "Save"))
            {
                return false;
            }

            await SafeWaitAfterCloseAsync(page);
            return true;
        }
        catch (PlaywrightException ex) when (LogisysBrowserSession.IsClosedError(ex))
        {
            return true;
        }
    }

    // Softlink Organization Master footer: Edit / Save / Save & Close / Close.
    private static async Task<bool> ClickMasterFooterAsync(IPage page, string text)
    {
        try
        {
            var id = text switch
            {
                "Edit" => "TaskButtons1_dvEdit",
                "Save" => "TaskButtons1_dvSave",
                "Save & Close" => "TaskButtons1_dvSaveClose",
                "Close" => "TaskButtons1_dvClose",
                _ => null
            };
            if (id is not null)
            {
                var byId = await page.EvaluateAsync<bool>("""
                    (buttonId) => {
                      const el = document.getElementById(buttonId);
                      if (!el || el.disabled || el.classList.contains('aspNetDisabled')) return false;
                      el.click();
                      return true;
                    }
                    """, id);
                if (byId)
                {
                    return true;
                }
            }
        }
        catch (PlaywrightException)
        {
        }

        foreach (var frame in AllFrames(page))
        {
            try
            {
                var clicked = await frame.EvaluateAsync<bool>("""
                    (name) => {
                      const want = (name || '').replace(/\s+/g, ' ').trim().toLowerCase();
                      const val = (el) => (el.value || el.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
                      const inputs = [...document.querySelectorAll('input[type="button"], input[type="submit"], button')];
                      const el = inputs.find(n => val(n) === want);
                      if (!el) return false;
                      if (el.disabled || el.classList.contains('aspNetDisabled') || el.getAttribute('aria-disabled') === 'true') {
                        return false;
                      }
                      el.click();
                      return true;
                    }
                    """, text);
                if (clicked)
                {
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        return false;
    }

    private static async Task EnsureCustomerGlAsync(IPage page, string glName)
    {
        if (!await SoftlinkClickCustomerVendorAsync(page, customer: true))
        {
            throw new LogisysException("Could not open Account Info → Customer.");
        }

        await page.WaitForTimeoutAsync(700);
        await LinkGlAsync(page, "Customer G/L", glName, assetsType: true);
        await TickNamedCheckboxAsync(page, "Receivable");
    }

    private static async Task EnsureVendorGlAsync(IPage page, string glName)
    {
        if (!await SoftlinkClickCustomerVendorAsync(page, customer: false))
        {
            throw new LogisysException("Could not open Account Info → Vendor.");
        }

        await page.WaitForTimeoutAsync(700);
        await LinkGlAsync(page, "Vendor G/L", glName, assetsType: false);
        await TickNamedCheckboxAsync(page, "Payable");
    }

    private static async Task<bool> SoftlinkClickCustomerVendorAsync(IPage page, bool customer)
    {
        var itemId = customer ? "tabCustVendAccInfoItem1" : "tabCustVendAccInfoItem2";
        var clicked = await page.EvaluateAsync<bool>("""
            (id) => {
              const li = document.getElementById(id);
              if (!li) return false;
              li.scrollIntoView({ block: 'center', inline: 'nearest' });
              li.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
              return true;
            }
            """, itemId);
        if (clicked)
        {
            await page.WaitForTimeoutAsync(600);
            return true;
        }

        return await ClickSoftlinkTabAsync(page, customer ? "Customer" : "Vendor");
    }

    private static async Task LinkGlAsync(IPage page, string fieldLabel, string glName, bool assetsType)
    {
        if (await FieldAlreadyHasValueAsync(page, fieldLabel, glName))
        {
            return;
        }

        IPage? popup = null;
        var popupTask = page.Context.WaitForPageAsync(new() { Timeout = 8_000 });
        if (!await ClickLookupLensAsync(page, fieldLabel))
        {
            throw new LogisysException($"Could not open the search lens for {fieldLabel}.");
        }

        try
        {
            popup = await popupTask;
        }
        catch (TimeoutException)
        {
            popup = page.Context.Pages.LastOrDefault(item =>
                !item.IsClosed
                && item != page
                && (item.Url.Contains("GLListPopup", StringComparison.OrdinalIgnoreCase)
                    || item.Url.Contains("ListPopup", StringComparison.OrdinalIgnoreCase)));
        }

        if (popup is null || popup.IsClosed)
        {
            // Some Softlink builds open the GL list in a frame on the same page.
            if (!await SelectGlInFramesAsync(page, glName, assetsType))
            {
                throw new LogisysException($"General Ledger popup did not open for {fieldLabel}.");
            }

            await page.WaitForTimeoutAsync(600);
            if (!await FieldAlreadyHasValueAsync(page, fieldLabel, glName)
                && !await FieldContainsAsync(page, fieldLabel, "Receivable")
                && !await FieldContainsAsync(page, fieldLabel, "Payable"))
            {
                throw new LogisysException($"{fieldLabel} was not set to {glName}.");
            }

            return;
        }

        await popup.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await popup.WaitForTimeoutAsync(500);
        if (!await SelectGlOnPopupAsync(popup, glName, assetsType))
        {
            try
            {
                await popup.CloseAsync();
            }
            catch (PlaywrightException)
            {
            }

            throw new LogisysException($"Could not select \"{glName}\" in the General Ledger popup for {fieldLabel}.");
        }

        for (var wait = 0; wait < 20 && !popup.IsClosed; wait++)
        {
            await page.WaitForTimeoutAsync(400);
        }

        if (!popup.IsClosed)
        {
            try
            {
                await popup.CloseAsync();
            }
            catch (PlaywrightException)
            {
            }
        }

        await page.BringToFrontAsync();
        await page.WaitForTimeoutAsync(700);
        if (!await FieldAlreadyHasValueAsync(page, fieldLabel, glName)
            && !await FieldContainsAsync(page, fieldLabel, assetsType ? "Receivable" : "Payable"))
        {
            throw new LogisysException($"{fieldLabel} did not keep \"{glName}\" after the popup closed.");
        }
    }

    private static async Task<bool> SelectGlOnPopupAsync(IPage popup, string glName, bool assetsType)
    {
        await TrySetGlTypeAsync(popup, assetsType ? "ASSETS" : "LIABILITIES");
        await TryFillGlNameFilterAsync(popup, glName);

        if (await ClickGlRowAsync(popup, glName))
        {
            return true;
        }

        // Alphabetical index shortcut (T for TPY…).
        var letter = glName.Trim().Length > 0 ? glName.Trim()[0].ToString().ToUpperInvariant() : "T";
        await ClickLetterIndexAsync(popup, letter);
        await popup.WaitForTimeoutAsync(500);
        return await ClickGlRowAsync(popup, glName);
    }

    private static async Task<bool> SelectGlInFramesAsync(IPage page, string glName, bool assetsType)
    {
        foreach (var frame in AllFrames(page))
        {
            try
            {
                if (!frame.Url.Contains("GLListPopup", StringComparison.OrdinalIgnoreCase)
                    && !frame.Url.Contains("ListPopup", StringComparison.OrdinalIgnoreCase))
                {
                    var hasGrid = await frame.GetByText(glName, new() { Exact = false }).CountAsync() > 0;
                    if (!hasGrid)
                    {
                        continue;
                    }
                }

                await TrySetGlTypeAsync(frame, assetsType ? "ASSETS" : "LIABILITIES");
                await TryFillGlNameFilterAsync(frame, glName);
                if (await ClickGlRowAsync(frame, glName))
                {
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        return false;
    }

    private static async Task TrySetGlTypeAsync(IPage page, string typeName)
    {
        foreach (var root in FrameBodies(page))
        {
            try
            {
                var select = root.Locator("select").First;
                if (await select.CountAsync() == 0)
                {
                    continue;
                }

                var options = select.Locator("option");
                var count = await options.CountAsync();
                for (var i = 0; i < count; i++)
                {
                    var text = (await options.Nth(i).InnerTextAsync()).Trim();
                    if (text.Equals(typeName, StringComparison.OrdinalIgnoreCase)
                        || text.Contains(typeName, StringComparison.OrdinalIgnoreCase))
                    {
                        await select.SelectOptionAsync(new SelectOptionValue { Label = text });
                        await page.WaitForTimeoutAsync(400);
                        return;
                    }
                }
            }
            catch (PlaywrightException)
            {
            }
        }
    }

    private static async Task TrySetGlTypeAsync(IFrame frame, string typeName)
    {
        try
        {
            var select = frame.Locator("select").First;
            if (await select.CountAsync() == 0)
            {
                return;
            }

            var options = select.Locator("option");
            var count = await options.CountAsync();
            for (var i = 0; i < count; i++)
            {
                var text = (await options.Nth(i).InnerTextAsync()).Trim();
                if (text.Equals(typeName, StringComparison.OrdinalIgnoreCase)
                    || text.Contains(typeName, StringComparison.OrdinalIgnoreCase))
                {
                    await select.SelectOptionAsync(new SelectOptionValue { Label = text });
                    await frame.Page.WaitForTimeoutAsync(400);
                    return;
                }
            }
        }
        catch (PlaywrightException)
        {
        }
    }

    private static async Task TryFillGlNameFilterAsync(IPage page, string glName)
    {
        foreach (var root in FrameBodies(page))
        {
            try
            {
                var nameBox = root.Locator("input[type='text']").First;
                if (await nameBox.CountAsync() == 0 || !await nameBox.IsVisibleAsync())
                {
                    continue;
                }

                await nameBox.FillAsync(glName);
                if (await ClickAnywhereAsync(page, "Search", button: true)
                    || await ClickAnywhereAsync(page, "Go", button: true))
                {
                    await page.WaitForTimeoutAsync(500);
                }

                return;
            }
            catch (PlaywrightException)
            {
            }
        }
    }

    private static async Task TryFillGlNameFilterAsync(IFrame frame, string glName)
    {
        try
        {
            var nameBox = frame.Locator("input[type='text']").First;
            if (await nameBox.CountAsync() == 0)
            {
                return;
            }

            await nameBox.FillAsync(glName);
            var search = frame.GetByRole(AriaRole.Button, new() { Name = "Search" });
            if (await search.CountAsync() > 0)
            {
                await search.First.ClickAsync();
                await frame.Page.WaitForTimeoutAsync(500);
            }
        }
        catch (PlaywrightException)
        {
        }
    }

    private static async Task ClickLetterIndexAsync(IPage page, string letter)
    {
        foreach (var root in FrameBodies(page))
        {
            try
            {
                var link = root.GetByText(letter, new() { Exact = true });
                if (await link.CountAsync() > 0)
                {
                    for (var i = 0; i < Math.Min(await link.CountAsync(), 6); i++)
                    {
                        var item = link.Nth(i);
                        if (await item.IsVisibleAsync())
                        {
                            await item.ClickAsync(new() { Force = true });
                            return;
                        }
                    }
                }
            }
            catch (PlaywrightException)
            {
            }
        }
    }

    private static async Task<bool> ClickGlRowAsync(IPage page, string glName)
    {
        foreach (var root in FrameBodies(page))
        {
            try
            {
                var cell = root.GetByText(glName, new() { Exact = true });
                if (await cell.CountAsync() == 0)
                {
                    cell = root.GetByText(glName, new() { Exact = false });
                }

                if (await cell.CountAsync() == 0)
                {
                    continue;
                }

                for (var i = 0; i < Math.Min(await cell.CountAsync(), 8); i++)
                {
                    var item = cell.Nth(i);
                    if (!await item.IsVisibleAsync())
                    {
                        continue;
                    }

                    try
                    {
                        await item.DblClickAsync(new() { Timeout = 3_000 });
                        return true;
                    }
                    catch (PlaywrightException)
                    {
                        await item.ClickAsync(new() { Force = true, Timeout = 3_000 });
                        return true;
                    }
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        return false;
    }

    private static async Task<bool> ClickGlRowAsync(IFrame frame, string glName)
    {
        try
        {
            var cell = frame.GetByText(glName, new() { Exact = false });
            if (await cell.CountAsync() == 0)
            {
                return false;
            }

            var item = cell.First;
            try
            {
                await item.DblClickAsync(new() { Timeout = 3_000 });
            }
            catch (PlaywrightException)
            {
                await item.ClickAsync(new() { Force = true, Timeout = 3_000 });
            }

            return true;
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task<bool> ClickLookupLensAsync(IPage page, string fieldLabel)
    {
        // Softlink Customer G/L search lens lives in #tdCustGL (img onclick openPopupWin).
        if (fieldLabel.Contains("Customer", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var softlink = await page.EvaluateAsync<bool>("""
                    () => {
                      const img = document.querySelector('#tdCustGL img[id$="ImageButton1"], #tdCustGL img[onclick*="openPopupWin"], #ctl116_ImageButton1');
                      if (!img) return false;
                      img.scrollIntoView({ block: 'center', inline: 'nearest' });
                      img.click();
                      return true;
                    }
                    """);
                if (softlink)
                {
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        if (fieldLabel.Contains("Vendor", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var softlink = await page.EvaluateAsync<bool>("""
                    () => {
                      const td = document.getElementById('tdVendGL')
                        || document.getElementById('tdVendorGL')
                        || [...document.querySelectorAll('td')].find(n =>
                             ((n.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase() === 'vendor g/l'));
                      const scope = td && (document.getElementById('tdVendGL') || td.closest('tr') || td);
                      const img = scope && scope.querySelector('img[id$="ImageButton1"], img[onclick*="openPopupWin"], img[src*="search"]');
                      if (!img) return false;
                      img.scrollIntoView({ block: 'center', inline: 'nearest' });
                      img.click();
                      return true;
                    }
                    """);
                if (softlink)
                {
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        foreach (var frame in AllFrames(page))
        {
            try
            {
                var clicked = await frame.EvaluateAsync<bool>("""
                    (label) => {
                      const want = (label || '').replace(/\s+/g, ' ').trim().toLowerCase();
                      const nodes = [...document.querySelectorAll('label, td, span, div, th')];
                      const labelNode = nodes.find(n => {
                        const t = (n.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
                        return t === want || t.startsWith(want);
                      });
                      if (!labelNode) return false;
                      const scope = labelNode.closest('tr, .form-group, div, table, td') || labelNode.parentElement;
                      if (!scope) return false;
                      const candidates = [
                        ...scope.querySelectorAll('img, a, input[type="image"], button, span, i')
                      ];
                      const lens = candidates.find(el => {
                        const src = (el.getAttribute('src') || '').toLowerCase();
                        const title = (el.getAttribute('title') || el.getAttribute('alt') || '').toLowerCase();
                        const onclick = (el.getAttribute('onclick') || '').toLowerCase();
                        return src.includes('search') || onclick.includes('openpopupwin')
                          || title.includes('search') || title.includes('lookup');
                      }) || candidates.find(el => el.tagName === 'IMG');
                      if (!lens) return false;
                      lens.click();
                      return true;
                    }
                    """, fieldLabel);
                if (clicked)
                {
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        return false;
    }

    private static async Task<bool> FieldAlreadyHasValueAsync(IPage page, string fieldLabel, string expected)
    {
        var value = await ReadFieldValueAsync(page, fieldLabel);
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> FieldContainsAsync(IPage page, string fieldLabel, string fragment)
    {
        var value = await ReadFieldValueAsync(page, fieldLabel);
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains(fragment, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> ReadFieldValueAsync(IPage page, string fieldLabel)
    {
        foreach (var frame in AllFrames(page))
        {
            try
            {
                var value = await frame.EvaluateAsync<string?>("""
                    (label) => {
                      const want = (label || '').replace(/\s+/g, ' ').trim().toLowerCase();
                      const nodes = [...document.querySelectorAll('label, td, span, div, th')];
                      const labelNode = nodes.find(n => {
                        const t = (n.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
                        return t === want || t.startsWith(want);
                      });
                      if (!labelNode) return null;
                      const scope = labelNode.closest('tr, .form-group, div, table, td') || labelNode.parentElement;
                      const input = scope && (
                        scope.querySelector('input[type="text"], input:not([type]), textarea')
                        || document.evaluate("following::input[not(@type='hidden') and not(@type='checkbox')][1]", labelNode, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null).singleNodeValue
                      );
                      return input ? ((input.value || input.getAttribute('value') || '').trim()) : null;
                    }
                    """, fieldLabel);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        return null;
    }

    private static async Task TickNamedCheckboxAsync(IPage page, string labelText)
    {
        var box = await FindNamedCheckboxAsync(page, labelText);
        if (box is null)
        {
            return;
        }

        try
        {
            if (await box.IsCheckedAsync())
            {
                return;
            }
        }
        catch (PlaywrightException)
        {
            return;
        }

        try
        {
            await box.CheckAsync(new LocatorCheckOptions { Force = true, Timeout = 3_000 });
        }
        catch (PlaywrightException)
        {
            try
            {
                await box.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 3_000 });
            }
            catch (PlaywrightException)
            {
            }
        }
    }

    private static async Task<ILocator?> FindNamedCheckboxAsync(IPage page, string labelText)
    {
        foreach (var root in FrameBodies(page))
        {
            var candidate = root.GetByText(labelText, new() { Exact = true });
            if (await candidate.CountAsync() == 0)
            {
                continue;
            }

            for (var i = 0; i < Math.Min(await candidate.CountAsync(), 8); i++)
            {
                var label = candidate.Nth(i);
                if (!await label.IsVisibleAsync())
                {
                    continue;
                }

                var nearby = label.Locator(
                    "xpath=ancestor::*[self::label or self::td or self::tr or self::div][1]//input[@type='checkbox']");
                if (await nearby.CountAsync() == 0)
                {
                    nearby = label.Locator("xpath=preceding::input[@type='checkbox'][1]");
                }

                if (await nearby.CountAsync() == 0)
                {
                    nearby = label.Locator("xpath=following::input[@type='checkbox'][1]");
                }

                if (await nearby.CountAsync() > 0)
                {
                    return nearby.First;
                }
            }
        }

        return null;
    }

    private static async Task<(bool Receivable, bool Payable)> ReadSoftlinkOrgTypesAsync(IPage page)
    {
        foreach (var frame in AllFrames(page))
        {
            try
            {
                var flags = await frame.EvaluateAsync<string>("""
                    () => {
                      const norm = (t) => (t || '').replace(/\s+/g, ' ').trim().toLowerCase();
                      const labels = ['shipper', 'consignee', 'agent', 'carrier', 'sub-agent', 'sub agent'];
                      const nodes = [...document.querySelectorAll('label, td, span, div')];
                      const on = [];
                      for (const want of labels) {
                        const label = nodes.find(n => norm(n.textContent) === want && n.children.length < 3);
                        if (!label) continue;
                        const scope = label.closest('label, td, tr, div') || label.parentElement;
                        const box = (scope && scope.querySelector('input[type="checkbox"]'))
                          || document.evaluate("preceding::input[@type='checkbox'][1]", label, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null).singleNodeValue
                          || document.evaluate("following::input[@type='checkbox'][1]", label, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null).singleNodeValue;
                        if (box && box.checked) on.push(want);
                      }
                      return on.join(',');
                    }
                    """);
                if (!string.IsNullOrWhiteSpace(flags))
                {
                    return RolesFromFlag(flags);
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        return (false, false);
    }

    private static async Task SafeCloseMasterAsync(IPage page)
    {
        try
        {
            if (!await ClickToolbarAsync(page, "Close"))
            {
                await ClickToolbarAsync(page, "Save & Close");
            }

            await SafeWaitAfterCloseAsync(page);
        }
        catch (PlaywrightException ex) when (LogisysBrowserSession.IsClosedError(ex))
        {
        }
    }

    private static async Task SafeWaitAfterCloseAsync(IPage page)
    {
        try
        {
            await page.WaitForTimeoutAsync(800);
        }
        catch (PlaywrightException ex) when (LogisysBrowserSession.IsClosedError(ex))
        {
        }
    }

    private static async Task<bool> IsAlreadyEditingAsync(IPage page)
    {
        foreach (var root in FrameBodies(page))
        {
            try
            {
                var edit = root.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true });
                var saveClose = root.GetByRole(AriaRole.Button, new() { Name = "Save & Close", Exact = true });
                if (await edit.CountAsync() > 0 && await saveClose.CountAsync() > 0)
                {
                    var editDisabled = await edit.First.IsDisabledAsync();
                    var saveEnabled = !await saveClose.First.IsDisabledAsync();
                    if (editDisabled && saveEnabled)
                    {
                        return true;
                    }
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        foreach (var frame in AllFrames(page))
        {
            try
            {
                var editing = await frame.EvaluateAsync<bool>("""
                    () => {
                      const val = (el) => (el.value || el.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
                      const inputs = [...document.querySelectorAll('input[type="button"], input[type="submit"], button')];
                      const edit = inputs.find(el => val(el) === 'edit');
                      const saveClose = inputs.find(el => val(el) === 'save & close');
                      if (!edit || !saveClose) return false;
                      const editOff = edit.disabled || edit.classList.contains('aspNetDisabled');
                      const saveOn = !saveClose.disabled && !saveClose.classList.contains('aspNetDisabled');
                      return editOff && saveOn;
                    }
                    """);
                if (editing)
                {
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        return false;
    }

    private static async Task<bool> ClickAccountInfoTabAsync(IPage page) =>
        await ClickSoftlinkTabAsync(page, "Account Info");

    // Softlink tabs are ASP.NET postbacks. Prefer JS click / __doPostBack — never wait for navigation.
    private static async Task<bool> ClickSoftlinkTabAsync(IPage page, string name)
    {
        // Prefer OrganizationMaster frame when Softlink hosts Master inside the shell page.
        var frames = AllFrames(page)
            .OrderByDescending(frame =>
            {
                try
                {
                    var url = frame.Url ?? string.Empty;
                    return url.Contains("OrganizationMaster", StringComparison.OrdinalIgnoreCase)
                        || url.Contains("Org_Id=", StringComparison.OrdinalIgnoreCase)
                        ? 1
                        : 0;
                }
                catch (PlaywrightException)
                {
                    return 0;
                }
            })
            .ToList();

        foreach (var frame in frames)
        {
            try
            {
                var clicked = await frame.EvaluateAsync<bool>("""
                    (want) => {
                      const target = (want || '').replace(/\s+/g, ' ').trim().toLowerCase();
                      const norm = (t) => (t || '').replace(/\s+/g, ' ').trim().toLowerCase();
                      const ownText = (n) => {
                        let t = '';
                        for (const c of n.childNodes) {
                          if (c.nodeType === 3) t += c.textContent || '';
                        }
                        return norm(t);
                      };
                      const nodes = [...document.querySelectorAll('a, span, td, li, div, label, input')];
                      let matches = nodes.filter(n => {
                        if (!n || n.offsetParent === null) return false;
                        const ot = ownText(n);
                        const full = norm(n.textContent);
                        return ot === target || full === target;
                      });
                      if (matches.length === 0 && target === 'account info') {
                        matches = [...document.querySelectorAll('[id*="AccountInfo"], [id*="accountinfo"], [id*="Account_Info"], [id*="tabAccount"]')]
                          .filter(n => n.offsetParent !== null);
                      }
                      if (matches.length === 0) return false;

                      const preferred = matches.find(n => {
                        const row = n.closest('tr, ul, ol, div, table') || n.parentElement;
                        if (!row) return false;
                        const sib = norm(row.textContent);
                        return sib.includes('billing') || sib.includes('services')
                          || sib.includes('shipper') || sib.includes('general')
                          || sib.includes('customer') || sib.includes('vendor');
                      }) || matches[0];

                      const fire = (el) => {
                        if (!el) return false;
                        el.scrollIntoView({ block: 'center', inline: 'nearest' });
                        const anchor = el.closest('a') || (el.tagName === 'A' ? el : null);
                        const href = anchor && (anchor.getAttribute('href') || '');
                        if (href && href.toLowerCase().includes('__dopostback')) {
                          const m = href.match(/__doPostBack\('([^']*)'\s*,\s*'([^']*)'\)/i)
                            || href.match(/__doPostBack\("([^"]*)"\s*,\s*"([^"]*)"\)/i);
                          if (m && typeof window.__doPostBack === 'function') {
                            window.__doPostBack(m[1], m[2]);
                            return true;
                          }
                        }
                        const onclick = (el.getAttribute('onclick') || (anchor && anchor.getAttribute('onclick')) || '');
                        if (onclick.toLowerCase().includes('__dopostback') && typeof window.__doPostBack === 'function') {
                          try { eval(onclick); return true; } catch (e) {}
                        }
                        el.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true, view: window }));
                        el.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true, view: window }));
                        el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                        if (typeof el.click === 'function') el.click();
                        if (anchor && anchor !== el && typeof anchor.click === 'function') anchor.click();
                        return true;
                      };

                      return fire(preferred);
                    }
                    """, name);
                if (clicked)
                {
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        foreach (var root in FrameBodies(page))
        {
            try
            {
                var tab = root.GetByText(name, new() { Exact = true });
                for (var i = 0; i < Math.Min(await tab.CountAsync(), 8); i++)
                {
                    var item = tab.Nth(i);
                    if (!await item.IsVisibleAsync())
                    {
                        continue;
                    }

                    // Softlink postback must not block Playwright waiting for navigation.
                    await item.ClickAsync(new() { Force = true, NoWaitAfter = true, Timeout = 2_000 });
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        return false;
    }

    private static async Task<bool> ClickSideTabAsync(IPage page, string name) =>
        await ClickSoftlinkTabAsync(page, name);

    private static async Task<bool> ClickToolbarAsync(IPage page, string text)
    {
        if (await ClickAnywhereAsync(page, text, button: true))
        {
            return true;
        }

        foreach (var frame in AllFrames(page))
        {
            try
            {
                var clicked = await frame.EvaluateAsync<bool>("""
                    (name) => {
                      const want = (name || '').replace(/\s+/g, ' ').trim().toLowerCase();
                      const inputs = [...document.querySelectorAll('input[type="button"], input[type="submit"], button, a, span')];
                      const el = inputs.find(n => {
                        const v = (n.value || n.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
                        return v === want;
                      });
                      if (!el) return false;
                      if (el.disabled || el.classList.contains('aspNetDisabled') || el.getAttribute('aria-disabled') === 'true') {
                        return false;
                      }
                      el.click();
                      return true;
                    }
                    """, text);
                if (clicked)
                {
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        return false;
    }

    private static async Task<bool> ClickAnywhereAsync(IPage page, string text, bool button = false)
    {
        foreach (var root in FrameBodies(page))
        {
            try
            {
                if (button)
                {
                    var btn = root.GetByRole(AriaRole.Button, new() { Name = text });
                    if (await btn.CountAsync() > 0 && await btn.First.IsVisibleAsync() && !await btn.First.IsDisabledAsync())
                    {
                        await btn.First.ClickAsync(new() { Force = true, NoWaitAfter = true, Timeout = 2_000 });
                        return true;
                    }
                }

                var exact = root.GetByText(text, new() { Exact = true });
                if (await exact.CountAsync() > 0 && await exact.First.IsVisibleAsync())
                {
                    await exact.First.ClickAsync(new() { Force = true, NoWaitAfter = true, Timeout = 2_000 });
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        return false;
    }

    private static async Task<bool> HasVisibleTextAsync(IPage page, string text)
    {
        foreach (var root in FrameBodies(page))
        {
            try
            {
                var node = root.GetByText(text, new() { Exact = true });
                if (await node.CountAsync() > 0 && await node.First.IsVisibleAsync())
                {
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        return false;
    }

    private static IEnumerable<IFrame> AllFrames(IPage page)
    {
        yield return page.MainFrame;
        foreach (var frame in page.Frames.Where(frame => frame != page.MainFrame))
        {
            yield return frame;
        }
    }

    private static IEnumerable<ILocator> FrameBodies(IPage page)
    {
        yield return page.Locator("body");
        foreach (var frame in page.Frames.Where(frame => frame != page.MainFrame))
        {
            yield return frame.Locator("body");
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 300 ? line[..300] : line;
    }
}
