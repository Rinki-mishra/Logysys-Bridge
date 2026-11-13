using LogisysOrgConverter.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace LogisysOrgConverter.Logisys;

public sealed record CodMarkSummary(int Organisations, int Marked, int Skipped, int Failed, string Message);

// Softlink Org Master import does not apply the Excel "Cash On Delivery" column to Account Info.
// After create, tick Account Info → Customer → Cash On Delivery in Softlink for new orgs only.
public sealed class OrganisationCodMarker
{
    private readonly AppDbContext _db;
    private readonly LogisysBrowserSession _browser;
    private readonly ILogger<OrganisationCodMarker> _logger;

    public OrganisationCodMarker(
        AppDbContext db,
        LogisysBrowserSession browser,
        ILogger<OrganisationCodMarker> logger)
    {
        _db = db;
        _browser = browser;
        _logger = logger;
    }

    public async Task<CodMarkSummary> MarkAsync(Guid runId, string? onlyName, CancellationToken cancellationToken)
    {
        // Only Created — org must already exist in Softlink after Excel import.
        var query = _db.RunRows.Where(row => row.RunId == runId
            && row.ImportStatus == ImportStatuses.Created
            && row.CodMarkStatus != "Done"
            && row.CodMarkStatus != "Skipped");

        if (!string.IsNullOrWhiteSpace(onlyName))
        {
            var key = Key(onlyName);
            var all = await query.OrderBy(row => row.SourceRowNumber).ToListAsync(cancellationToken);
            var rows = all.Where(row => Key(row.OrganisationName).Contains(key, StringComparison.Ordinal)).ToList();
            if (rows.Count == 0)
            {
                return new CodMarkSummary(0, 0, 0, 0,
                    $"No Created organisation matching \"{onlyName.Trim()}\". Import the India file in Softlink, mark it Created here, then Finish again.");
            }

            return await MarkRowsAsync(rows, cancellationToken);
        }

        var created = await query.OrderBy(row => row.SourceRowNumber).ToListAsync(cancellationToken);
        if (created.Count == 0)
        {
            return new CodMarkSummary(0, 0, 0, 0,
                "No Created organisations waiting for Cash On Delivery. Import the India file in Softlink, mark Created here, then Finish.");
        }

        return await MarkRowsAsync(created, cancellationToken);
    }

    private async Task<CodMarkSummary> MarkRowsAsync(List<ConversionRunRow> rows, CancellationToken cancellationToken)
    {
        return await _browser.RunAsync(async (page, token) =>
        {
            var marked = 0;
            var skipped = 0;
            var failed = 0;
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                page = await LogisysBrowserSession.EnsureLivePageAsync(page);
                if (await LogisysBrowserSession.IsLoginPageAsync(page))
                {
                    throw new LogisysException(
                        "Softlink is on Login. Click Sign in to Softlink, finish captcha + Log In + Submit, leave Edge open, then Mark Cash On Delivery again.");
                }

                try
                {
                    var master = await SoftlinkOrgNavigation.OpenOrganisationAsync(
                        page, _browser.BaseUrl, row.OrganisationName, token);
                    var outcome = await EnsureCashOnDeliveryAsync(master, row.OrganisationName);
                    switch (outcome)
                    {
                        case CodOutcome.Marked:
                            row.CodMarkStatus = "Done";
                            row.CodMarkMessage = $"Cash On Delivery checked at {DateTime.UtcNow:u}.";
                            marked++;
                            break;
                        case CodOutcome.AlreadySet:
                            row.CodMarkStatus = "Done";
                            row.CodMarkMessage = $"Cash On Delivery was already checked ({DateTime.UtcNow:u}).";
                            marked++;
                            break;
                        case CodOutcome.SkippedCredit:
                            row.CodMarkStatus = "Skipped";
                            row.CodMarkMessage =
                                $"Skipped Cash On Delivery — Credit Limit or Unlimited is set ({DateTime.UtcNow:u}).";
                            skipped++;
                            break;
                    }
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

                    if (onLogin)
                    {
                        row.CodMarkStatus = "Failed";
                        row.CodMarkMessage = FirstLine(ex.Message);
                        await _db.SaveChangesAsync(token);
                        throw new LogisysException(
                            "Softlink returned to Login. Sign in again, leave Edge open, then Mark Cash On Delivery.");
                    }

                    failed++;
                    row.CodMarkStatus = "Failed";
                    row.CodMarkMessage = FirstLine(ex.Message);
                    _logger.LogWarning(ex, "Could not mark COD for {Organisation}.", row.OrganisationName);
                    try
                    {
                        await _browser.CaptureAsync(page, "cod-" + row.OrganisationName);
                    }
                    catch (PlaywrightException)
                    {
                    }
                }

                await _db.SaveChangesAsync(token);
                page = await LogisysBrowserSession.EnsureLivePageAsync(page);
            }

            return new CodMarkSummary(rows.Count, marked, skipped, failed,
                $"Cash On Delivery: {marked} marked, {skipped} skipped (credit/unlimited), {failed} failed (of {rows.Count}).");
        }, cancellationToken);
    }

    private enum CodOutcome
    {
        Marked,
        AlreadySet,
        SkippedCredit
    }

    private static async Task<CodOutcome> EnsureCashOnDeliveryAsync(IPage listOrMaster, string organisationName)
    {
        // Softlink opens Organization Master in a popup/new tab — Account Info is there, not on the list page.
        var page = await SoftlinkOrgNavigation.FindOrganizationMasterPageAsync(listOrMaster, organisationName)
            ?? await SoftlinkOrgNavigation.TryResolveOrgMasterPageAsync(listOrMaster, organisationName)
            ?? throw new LogisysException(
                $"Organisation Master for \"{organisationName}\" did not open. Search by Name, click Modify (pencil), then try again.");
        await page.BringToFrontAsync();

        // Softlink Organization Master is flaky: tabs/buttons hydrate after the window opens, and Edit
        // often snaps back to General. Wait for controls, Edit first, then keep forcing Account Info.
        if (!await WaitForMasterControlsAsync(page))
        {
            throw new LogisysException(
                "Organization Master did not finish loading (Account Info / Edit controls missing). Leave Edge open and Finish again.");
        }

        var alreadyEditing = await IsAlreadyEditingAsync(page);
        if (!alreadyEditing)
        {
            if (!await SoftlinkClickEditAsync(page) && !await ClickToolbarAsync(page, "Edit"))
            {
                throw new LogisysException("Could not click Edit on Organization Master.");
            }

            await page.WaitForTimeoutAsync(1_200);
            await WaitForMasterControlsAsync(page);
        }

        // Softlink path: Edit → Account Info (#tsMainItem6) → Customer → COD → Save & Close.
        // Do NOT click "General" after Account Info — that jumps back to Address and hides COD.
        if (!await EnsureAccountInfoAsync(page))
        {
            throw new LogisysException(
                "Account Info tab did not open (still on Address/General). Softlink tab strip was slow — leave Organization Master open and Finish again.");
        }

        if (!await SoftlinkClickCustomerAsync(page) && !await ClickCustomerTabAsync(page))
        {
            throw new LogisysException("Could not open Account Info → Customer.");
        }

        await page.WaitForTimeoutAsync(800);

        // Rule: any Credit Limit (> 0) or Unlimited → never tick Cash On Delivery.
        if (await HasCreditOrUnlimitedAsync(page))
        {
            await SafeCloseMasterAsync(page);
            return CodOutcome.SkippedCredit;
        }

        var box = await FindCodCheckboxAsync(page)
            ?? throw new LogisysException("Cash On Delivery was not found on Account Info → Customer.");

        await box.ScrollIntoViewIfNeededAsync();
        if (await box.IsCheckedAsync())
        {
            // Already set — Close is enough; Save & Close also closes the tab (expected).
            await SafeCloseMasterAsync(page);
            return CodOutcome.AlreadySet;
        }

        // Edit disabled / already Update → tick COD immediately (do not require another Edit click).
        if (!alreadyEditing && !await EnsureEditableAsync(page, box))
        {
            throw new LogisysException(
                "Cash On Delivery is still locked. Softlink must be in Edit/Update mode, then Finish again.");
        }

        await TickCodAsync(page, box);
        await page.WaitForTimeoutAsync(400);

        box = await FindCodCheckboxAsync(page) ?? box;
        if (!await box.IsCheckedAsync())
        {
            throw new LogisysException("Cash On Delivery did not stay checked.");
        }

        // Save & Close intentionally closes Organization Master — treat tab-closed as success.
        try
        {
            if (!await SoftlinkClickSaveCloseAsync(page) && !await ClickToolbarAsync(page, "Save & Close"))
            {
                throw new LogisysException("Save & Close was not available after checking Cash On Delivery.");
            }

            await SafeWaitAfterCloseAsync(page);
        }
        catch (PlaywrightException ex) when (LogisysBrowserSession.IsClosedError(ex))
        {
            // Softlink closed the master tab after saving — COD is already stored.
        }

        return CodOutcome.Marked;
    }

    // Softlink Save & Close / Close disposes the Organization Master tab; Playwright then throws.
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
            if (!page.IsClosed)
            {
                await page.WaitForTimeoutAsync(800);
            }
        }
        catch (PlaywrightException ex) when (LogisysBrowserSession.IsClosedError(ex))
        {
        }
    }

    // Softlink Account Info → Customer: Credit Limit amount or Unlimited checkbox means COD must stay off.
    private static async Task<bool> HasCreditOrUnlimitedAsync(IPage page)
    {
        foreach (var frame in AllFrames(page))
        {
            try
            {
                var result = await frame.EvaluateAsync<string>("""
                    () => {
                      const norm = (t) => (t || '').replace(/\s+/g, ' ').trim().toLowerCase();
                      const nodes = [...document.querySelectorAll('label, span, td, div, th')];
                      const creditLabel = nodes.find(n => norm(n.textContent) === 'credit limit');
                      let amountText = '';
                      if (creditLabel) {
                        const root = creditLabel.closest('tr, td, div, label') || creditLabel.parentElement;
                        const input = root && root.querySelector
                          ? (root.querySelector('input:not([type="checkbox"]):not([type="hidden"])')
                             || creditLabel.parentElement?.querySelector('input:not([type="checkbox"]):not([type="hidden"])'))
                          : null;
                        const nearby = creditLabel.parentElement
                          && [...creditLabel.parentElement.querySelectorAll('input')].find(i => i.type !== 'checkbox' && i.type !== 'hidden');
                        const box = input || nearby
                          || document.evaluate("following::input[not(@type='checkbox') and not(@type='hidden')][1]", creditLabel, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null).singleNodeValue;
                        if (box) amountText = (box.value || box.getAttribute('value') || '').trim();
                      }

                      const unlimitedLabel = nodes.find(n => norm(n.textContent) === 'unlimited');
                      let unlimited = false;
                      if (unlimitedLabel) {
                        const scope = unlimitedLabel.closest('label, td, tr, div') || unlimitedLabel.parentElement;
                        const cb = (scope && scope.querySelector('input[type="checkbox"]'))
                          || document.evaluate("preceding::input[@type='checkbox'][1]", unlimitedLabel, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null).singleNodeValue
                          || document.evaluate("following::input[@type='checkbox'][1]", unlimitedLabel, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null).singleNodeValue;
                        unlimited = !!(cb && (cb.checked || cb.getAttribute('checked') != null));
                      }

                      const cleaned = (amountText || '').replace(/,/g, '').replace(/[^\d.\-]/g, '');
                      const amount = cleaned === '' ? 0 : Number.parseFloat(cleaned);
                      const hasLimit = Number.isFinite(amount) && Math.abs(amount) > 0.00001;
                      if (hasLimit) return 'limit:' + amountText;
                      if (unlimited) return 'unlimited';
                      return '';
                    }
                    """);
                if (!string.IsNullOrEmpty(result))
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

    private static async Task<bool> WaitForMasterControlsAsync(IPage page)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                var ready = await page.EvaluateAsync<bool>("""
                    () => !!(document.getElementById('tsMainItem6') || document.getElementById('tsMainLink6'))
                      && !!(document.getElementById('TaskButtons1_dvEdit')
                        || document.getElementById('TaskButtons1_dvSaveClose')
                        || document.getElementById('TaskButtons1_dvClose'));
                    """);
                if (ready)
                {
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }

            await page.WaitForTimeoutAsync(400);
        }

        return false;
    }

    private static async Task<bool> EnsureAccountInfoAsync(IPage page)
    {
        // Softlink is intermittent here: Edit snaps back to General, and linkOn can lag behind the click.
        for (var attempt = 0; attempt < 15; attempt++)
        {
            if (await SoftlinkAccountInfoActiveAsync(page))
            {
                return true;
            }

            await SoftlinkClickAccountInfoAsync(page);
            for (var wait = 0; wait < 6; wait++)
            {
                await page.WaitForTimeoutAsync(450);
                if (await SoftlinkAccountInfoActiveAsync(page))
                {
                    return true;
                }
            }
        }

        return await SoftlinkAccountInfoActiveAsync(page);
    }

    // Softlink main tab: LI#tsMainItem6 / A#tsMainLink6 — DOM click only (direct clickTab() breaks in strict mode).
    private static async Task SoftlinkClickAccountInfoAsync(IPage page)
    {
        try
        {
            var clicked = await page.EvaluateAsync<bool>("""
                () => {
                  const fire = (el) => {
                    if (!el) return false;
                    el.scrollIntoView({ block: 'center', inline: 'nearest' });
                    el.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true, view: window }));
                    el.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true, view: window }));
                    el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                    if (typeof el.click === 'function') el.click();
                    return true;
                  };
                  const li = document.getElementById('tsMainItem6');
                  const a = document.getElementById('tsMainLink6') || (li && li.querySelector('a'));
                  const td = a && a.querySelector('td');
                  // Softlink binds tab switching on the LI / inner A / TD inconsistently across builds.
                  return fire(td) || fire(a) || fire(li);
                }
                """);
            if (!clicked)
            {
                await ClickAccountInfoTabAsync(page);
            }
        }
        catch (PlaywrightException)
        {
            await ClickAccountInfoTabAsync(page);
        }
    }

    private static async Task<bool> SoftlinkAccountInfoActiveAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>("""
                () => {
                  const li = document.getElementById('tsMainItem6');
                  if (li && (li.className || '').toString().includes('linkOn')) return true;
                  // Fallback: Account Info panel widgets are present and General Address branch list is not the focus.
                  const customer = document.getElementById('tabCustVendAccInfoItem1');
                  const credit = (document.body.innerText || '').includes('Credit Limit');
                  const cod = (document.body.innerText || '').includes('Cash On Delivery');
                  const generalOn = (() => {
                    const g = document.getElementById('tsMainItem1');
                    return !!(g && (g.className || '').toString().includes('linkOn'));
                  })();
                  return !generalOn && !!customer && (credit || cod);
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
        try
        {
            return await page.EvaluateAsync<bool>("""
                () => {
                  const btn = document.getElementById('TaskButtons1_dvEdit');
                  if (!btn || btn.disabled || btn.classList.contains('aspNetDisabled')) return false;
                  btn.click();
                  return true;
                }
                """);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task<bool> SoftlinkClickSaveCloseAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>("""
                () => {
                  const btn = document.getElementById('TaskButtons1_dvSaveClose')
                    || document.getElementById('TaskButtons1_dvSave');
                  if (!btn || btn.disabled || btn.classList.contains('aspNetDisabled')) return false;
                  btn.click();
                  return true;
                }
                """);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task<bool> SoftlinkClickCustomerAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>("""
                () => {
                  const li = document.getElementById('tabCustVendAccInfoItem1');
                  if (!li) return false;
                  li.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                  return true;
                }
                """);
        }
        catch (PlaywrightException)
        {
            return false;
        }
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

    private static async Task<bool> IsAlreadyEditingAsync(IPage page)
    {
        try
        {
            var softlink = await page.EvaluateAsync<bool>("""
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
            if (softlink)
            {
                return true;
            }
        }
        catch (PlaywrightException)
        {
        }

        // Softlink Update mode: Edit disabled/grey, Save / Save & Close enabled.
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

        // Fallback: toolbar inputs (Softlink often uses <input type="button" value="Edit">).
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

    private static async Task<bool> ClickCustomerTabAsync(IPage page)
    {
        // Prefer a short "Customer" tab label — not long sentences containing the word.
        foreach (var frame in AllFrames(page))
        {
            try
            {
                var clicked = await frame.EvaluateAsync<bool>("""
                    () => {
                      const nodes = [...document.querySelectorAll('a, span, td, li, div, label')];
                      const el = nodes.find(n => {
                        const t = (n.textContent || '').replace(/\s+/g, ' ').trim();
                        return t === 'Customer' && n.children.length < 3;
                      });
                      if (!el) return false;
                      el.click();
                      return true;
                    }
                    """);
                if (clicked)
                {
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        return await ClickTabAsync(page, "Customer");
    }

    private static async Task<ILocator?> FindCodCheckboxAsync(IPage page)
    {
        foreach (var root in FrameBodies(page))
        {
            var candidate = root.GetByText("Cash On Delivery", new() { Exact = true });
            if (await candidate.CountAsync() == 0)
            {
                candidate = root.GetByText("Cash On Delivery", new() { Exact = false });
            }

            if (await candidate.CountAsync() == 0)
            {
                continue;
            }

            for (var i = 0; i < Math.Min(await candidate.CountAsync(), 6); i++)
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
                    nearby = label.Locator("xpath=following::input[@type='checkbox'][1]");
                }

                if (await nearby.CountAsync() == 0)
                {
                    nearby = label.Locator("xpath=preceding::input[@type='checkbox'][1]");
                }

                if (await nearby.CountAsync() > 0)
                {
                    return nearby.First;
                }
            }
        }

        return null;
    }

    private static async Task<bool> EnsureEditableAsync(IPage page, ILocator box)
    {
        if (await IsEditableCheckboxAsync(box))
        {
            return true;
        }

        if (!await ClickToolbarAsync(page, "Edit"))
        {
            return false;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await page.WaitForTimeoutAsync(400);
            var current = await FindCodCheckboxAsync(page) ?? box;
            if (await IsEditableCheckboxAsync(current))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> IsEditableCheckboxAsync(ILocator box)
    {
        try
        {
            if (!await box.IsVisibleAsync())
            {
                return false;
            }

            if (await box.IsDisabledAsync())
            {
                return false;
            }

            // Softlink often leaves the control "enabled" in DOM but read-only via parent/CSS until Edit.
            var locked = await box.EvaluateAsync<bool>("""
                el => {
                  if (el.disabled || el.readOnly) return true;
                  if (el.getAttribute('aria-disabled') === 'true') return true;
                  const root = el.closest('[disabled], .aspNetDisabled, .disabled, [aria-disabled="true"]');
                  return !!root;
                }
                """);
            return !locked;
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task TickCodAsync(IPage page, ILocator box)
    {
        // Prefer Force — Softlink Update mode (Edit disabled) still posts forced checkbox changes.
        try
        {
            await box.CheckAsync(new LocatorCheckOptions { Force = true, Timeout = 3_000 });
            if (await box.IsCheckedAsync())
            {
                return;
            }
        }
        catch (PlaywrightException)
        {
        }

        try
        {
            await box.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 3_000 });
            if (await box.IsCheckedAsync())
            {
                return;
            }
        }
        catch (PlaywrightException)
        {
        }

        // Softlink sometimes only toggles when the label text is clicked.
        foreach (var root in FrameBodies(page))
        {
            var label = root.GetByText("Cash On Delivery", new() { Exact = true });
            if (await label.CountAsync() == 0)
            {
                continue;
            }

            await label.First.ClickAsync(new() { Force = true });
            await page.WaitForTimeoutAsync(300);
            var current = await FindCodCheckboxAsync(page);
            if (current is not null && await current.IsCheckedAsync())
            {
                return;
            }
        }

        throw new LogisysException("Could not tick Cash On Delivery.");
    }

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

    private static async Task<bool> ClickAccountInfoTabAsync(IPage page)
    {
        // Prefer the main strip tab between Services and Billing (not any random "Account Info" text).
        foreach (var frame in AllFrames(page))
        {
            try
            {
                var clicked = await frame.EvaluateAsync<bool>("""
                    () => {
                      const norm = (t) => (t || '').replace(/\s+/g, ' ').trim().toLowerCase();
                      const nodes = [...document.querySelectorAll('a, span, td, li, div')];
                      const account = nodes.find(n => {
                        if (norm(n.textContent) !== 'account info' || n.children.length > 2) return false;
                        const row = n.parentElement;
                        if (!row) return true;
                        const siblings = [...row.children].map(c => norm(c.textContent));
                        return siblings.some(s => s === 'services' || s === 'billing' || s === 'shipper' || s === 'general');
                      });
                      if (!account) return false;
                      account.click();
                      return true;
                    }
                    """);
                if (clicked)
                {
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        return await ClickTabAsync(page, "Account Info");
    }

    private static async Task<bool> ClickTabAsync(IPage page, string name)
    {
        foreach (var root in FrameBodies(page))
        {
            try
            {
                var tab = root.GetByRole(AriaRole.Tab, new() { Name = name, Exact = true });
                if (await tab.CountAsync() > 0 && await tab.First.IsVisibleAsync())
                {
                    await tab.First.ClickAsync();
                    return true;
                }

                var exact = root.GetByText(name, new() { Exact = true });
                if (await exact.CountAsync() > 0)
                {
                    for (var i = 0; i < Math.Min(await exact.CountAsync(), 8); i++)
                    {
                        var item = exact.Nth(i);
                        if (await item.IsVisibleAsync())
                        {
                            await item.ClickAsync(new() { Force = true });
                            return true;
                        }
                    }
                }

                var fuzzy = root.GetByText(name, new() { Exact = false });
                if (await fuzzy.CountAsync() > 0 && await fuzzy.First.IsVisibleAsync())
                {
                    await fuzzy.First.ClickAsync(new() { Force = true });
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
                        await btn.First.ClickAsync();
                        return true;
                    }
                }

                var exact = root.GetByText(text, new() { Exact = true });
                if (await exact.CountAsync() > 0 && await exact.First.IsVisibleAsync())
                {
                    await exact.First.ClickAsync();
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        return false;
    }

    private static string Key(string value) => SoftlinkOrgNavigation.Key(value);

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 300 ? line[..300] : line;
    }
}
