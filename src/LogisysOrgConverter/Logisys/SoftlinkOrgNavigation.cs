using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace LogisysOrgConverter.Logisys;

// Softlink: top Organization (or Directories → Organizations), then Name field + Search — not the sidebar "search".
public static class SoftlinkOrgNavigation
{
    private static readonly Regex NonAlpha = new("[^A-Z0-9]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Key(string value) => NonAlpha.Replace(value.ToUpperInvariant(), string.Empty);

    public static async Task<IPage> OpenOrganisationAsync(IPage page, string baseUrl, string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Leftover Organization Master tabs from the previous org make Notes attach to the wrong company.
        await CloseOrgMasterPagesAsync(page.Context, keep: page);
        await OpenOrganizationsListAsync(page, baseUrl, cancellationToken);

        await page.WaitForTimeoutAsync(1_200);
        if (!await SearchByNameAndOpenAsync(page, name))
        {
            throw new LogisysException(
                $"Could not open organisation \"{name}\" in Softlink. Search the Name, select the row, click toolbar Modify (pencil), then try again.");
        }

        var master = await FindOrganizationMasterPageAsync(page, name)
            ?? await TryResolveOrgMasterPageAsync(page, name)
            ?? throw new LogisysException(
                $"Organisation Master for \"{name}\" opened but could not be attached. Leave the Softlink Edge window open and try again.");
        await master.BringToFrontAsync();
        return master;
    }

    // Organizations list only (for Excel import / Upload Organizations).
    public static async Task OpenOrganizationsListAsync(IPage page, string baseUrl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await LogisysBrowserSession.IsLoginPageAsync(page))
        {
            throw new LogisysException("Logi-Sys signed out. Sign in again in the Edge window.");
        }

        await page.GotoAsync(baseUrl.TrimEnd('/') + "/NewHomePage.aspx", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60_000
        });
        await page.WaitForTimeoutAsync(1_000);

        if (!await OpenOrganizationsScreenAsync(page))
        {
            throw new LogisysException(
                "Could not open Softlink Organizations. Click Organization (top bar) or Directories → Organizations, then try again.");
        }
    }

    // 1) Top "Organization" shortcut, or Directories → Organizations.
    private static async Task<bool> OpenOrganizationsScreenAsync(IPage page)
    {
        foreach (var root in Roots(page))
        {
            try
            {
                // Top toolbar icon/link — preferred path the user described.
                if (await ClickTitleOrTextAsync(root, "Organization")
                    || await ClickTitleOrTextAsync(root, "Organizations")
                    || await ClickTitleOrTextAsync(root, "Organisation")
                    || await ClickTitleOrTextAsync(root, "Organisations"))
                {
                    await page.WaitForTimeoutAsync(1_000);
                    if (await HasNameSearchAsync(page))
                    {
                        return true;
                    }
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        foreach (var root in Roots(page))
        {
            try
            {
                await ClickExactAsync(root, "Directories");
                await page.WaitForTimeoutAsync(500);
                if (await ClickExactAsync(root, "Organizations") || await ClickExactAsync(root, "Organisations"))
                {
                    await page.WaitForTimeoutAsync(1_000);
                    if (await HasNameSearchAsync(page))
                    {
                        return true;
                    }
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        return await HasNameSearchAsync(page);
    }

    // 2) OrganizationList.aspx iframe: search Name → Softlink selectgriditem → toolbar Modify.
    // Plain DOM row clicks do NOT select Softlink's DBList — Modify then does nothing.
    private static async Task<bool> SearchByNameAndOpenAsync(IPage page, string name)
    {
        var searchName = name.Trim().TrimEnd('.');
        var before = SnapshotPages(page.Context);

        // Prefer Softlink OrganizationList frame (DBList1_Handler + ToolBarsCtrl1_dvEdit).
        if (await SoftlinkSearchSelectAndModifyAsync(page, searchName))
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await page.WaitForTimeoutAsync(400);
                if (await TryBringMasterAsync(page, name, before))
                {
                    return true;
                }
            }
        }

        // Fallback: older locator-based search/open (less reliable on Softlink grids).
        var key = Key(name);
        foreach (var root in Roots(page))
        {
            try
            {
                var nameBox = await FindOrganizationsNameInputAsync(root);
                if (nameBox is null)
                {
                    continue;
                }

                await nameBox.ClickAsync();
                await nameBox.FillAsync(string.Empty);
                await nameBox.FillAsync(searchName);
                await page.WaitForTimeoutAsync(300);

                var searchBtn = root.GetByRole(AriaRole.Button, new() { Name = "Search" });
                if (await searchBtn.CountAsync() > 0 && await searchBtn.First.IsVisibleAsync())
                {
                    await searchBtn.First.ClickAsync();
                }
                else
                {
                    await nameBox.PressAsync("Enter");
                }

                await page.WaitForTimeoutAsync(1_800);

                if (await SoftlinkSearchSelectAndModifyAsync(page, searchName)
                    && await TryBringMasterAsync(page, name, before))
                {
                    return true;
                }

                var row = await FindResultRowAsync(root, name, key);
                if (row is null)
                {
                    continue;
                }

                await row.ClickAsync(new() { Force = true });
                await page.WaitForTimeoutAsync(400);
                await SoftlinkSearchSelectAndModifyAsync(page, searchName);
                if (await TryBringMasterAsync(page, name, before))
                {
                    return true;
                }

                try
                {
                    await row.DblClickAsync(new() { Force = true, Timeout = 3_000 });
                }
                catch (PlaywrightException)
                {
                }

                await page.WaitForTimeoutAsync(1_200);
                if (await TryBringMasterAsync(page, name, before))
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

    // Softlink OrganizationList: #txtName + Search, then DBList1_Handler.selectgriditem(tr) + Modify.
    private static async Task<bool> SoftlinkSearchSelectAndModifyAsync(IPage page, string searchName)
    {
        foreach (var frame in page.Frames)
        {
            try
            {
                var url = frame.Url ?? string.Empty;
                var onList = url.Contains("OrganizationList", StringComparison.OrdinalIgnoreCase)
                    || await frame.EvaluateAsync<bool>("() => !!(window.DBList1_Handler && document.getElementById('txtName'))");
                if (!onList)
                {
                    continue;
                }

                var opened = await frame.EvaluateAsync<bool>("""
                    (want) => {
                      const norm = (t) => (t || '').replace(/[^a-z0-9]/gi, '').toUpperCase();
                      const key = norm(want);
                      const nameBox = document.getElementById('txtName');
                      if (nameBox) {
                        nameBox.focus();
                        nameBox.value = want || '';
                        nameBox.dispatchEvent(new Event('input', { bubbles: true }));
                        nameBox.dispatchEvent(new Event('change', { bubbles: true }));
                      }
                      const searchBtn = [...document.querySelectorAll('input[type="button"], input[type="submit"], button, a')]
                        .find(el => ((el.value || el.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase() === 'search'));
                      if (searchBtn) searchBtn.click();
                      return true;
                    }
                    """, searchName);
                if (!opened)
                {
                    continue;
                }

                await page.WaitForTimeoutAsync(1_800);

                var selected = await frame.EvaluateAsync<bool>("""
                    (want) => {
                      const norm = (t) => (t || '').replace(/[^a-z0-9]/gi, '').toUpperCase();
                      const key = norm(want);
                      const h = window.DBList1_Handler;
                      if (!h) return false;
                      const rows = [...document.querySelectorAll('tr.SGridItem')];
                      const row = rows.find(tr => {
                        const t = norm(tr.innerText || '');
                        return t.includes(key) || key.includes(norm((tr.innerText || '').split(/[\\t\\n]/)[0] || ''));
                      }) || rows[0];
                      if (!row) return false;
                      // Softlink requires selectgriditem — plain DOM click does not select the DBList row.
                      try { if (h.selectgriditem) h.selectgriditem(row); } catch (e) {}
                      try { if (h.SetSelectedRowByIndex) h.SetSelectedRowByIndex(rows.indexOf(row)); } catch (e) {}
                      const selected = !!(h.GetSelectedRowItem && h.GetSelectedRowItem());
                      if (!selected) return false;
                      const edit = document.getElementById('ToolBarsCtrl1_dvEdit')
                        || document.getElementById('ToolBarsCtrl1_imgModify');
                      if (!edit) return false;
                      edit.click();
                      return true;
                    }
                    """, searchName);
                if (selected)
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

    private static async Task<ILocator?> FindResultRowAsync(ILocator root, string name, string key)
    {
        // Prefer grid cells / name links — never the Name filter field above Search.
        var cells = root.Locator(
            "table tbody td, [role='gridcell'], .ag-cell, .dx-data-row td, tr.dx-row td, a");
        var count = await cells.CountAsync();
        for (var i = 0; i < Math.Min(count, 80); i++)
        {
            var item = cells.Nth(i);
            try
            {
                if (!await item.IsVisibleAsync())
                {
                    continue;
                }

                var text = (await item.InnerTextAsync()).Trim();
                if (text.Length == 0
                    || text.Equals("Name", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("Alias", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("Search", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("Entity Type", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("Clear", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var textKey = Key(text);
                if (textKey != key && !textKey.Contains(key, StringComparison.Ordinal) && !key.Contains(textKey, StringComparison.Ordinal))
                {
                    continue;
                }

                // Prefer the Name cell; Softlink Entity Type can also contain the search string rarely.
                if (text.Contains(',', StringComparison.Ordinal) && text.Contains("Shipper", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return item;
            }
            catch (PlaywrightException)
            {
            }
        }

        // Last resort: exact visible text match anywhere in the results area.
        var exact = root.GetByText(name, new() { Exact = true });
        for (var i = 0; i < Math.Min(await exact.CountAsync(), 10); i++)
        {
            var item = exact.Nth(i);
            if (await item.IsVisibleAsync())
            {
                return item;
            }
        }

        return null;
    }

    // Select-row → toolbar Edit is the reliable Softlink path (icon, not a text button).
    private static async Task<bool> OpenSelectedOrganisationAsync(IPage page, ILocator root, string name)
    {
        var before = SnapshotPages(page.Context);
        if (!await ClickListToolbarEditAsync(page, root))
        {
            return false;
        }

        // Softlink usually opens Organization Master in a new tab/window.
        for (var attempt = 0; attempt < 15; attempt++)
        {
            await page.WaitForTimeoutAsync(400);
            if (await TryBringMasterAsync(page, name, before))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> TryBringMasterAsync(
        IPage page,
        string name,
        IReadOnlyCollection<IPage>? pagesBeforeOpen)
    {
        // Prefer any new OrganizationMaster window first (Softlink opens a separate Edge page).
        var master = await FindOrganizationMasterPageAsync(page, name)
            ?? await TryResolveOrgMasterPageAsync(page, name, pagesBeforeOpen);
        if (master is null)
        {
            return false;
        }

        await master.BringToFrontAsync();
        return true;
    }

    private static async Task<bool> ClickListToolbarEditAsync(IPage page, ILocator root)
    {
        // Softlink OrganizationList.aspx toolbar: Modify pencil — #ToolBarsCtrl1_dvEdit / #ToolBarsCtrl1_imgModify
        // (title is "Modify", src Images/Edit.png — not the word "Edit").
        foreach (var frame in page.Frames)
        {
            try
            {
                var clicked = await frame.EvaluateAsync<bool>("""
                    () => {
                      const visible = (el) => !!(el && el.offsetParent !== null);
                      const prefer = document.getElementById('ToolBarsCtrl1_dvEdit')
                        || document.getElementById('ToolBarsCtrl1_imgModify');
                      if (prefer && visible(prefer)) {
                        prefer.click();
                        return true;
                      }
                      const meta = (el) => ({
                        title: (el.getAttribute('title') || el.getAttribute('alt') || el.getAttribute('aria-label') || '').toLowerCase(),
                        id: (el.id || '').toLowerCase(),
                        src: (el.getAttribute('src') || '').toLowerCase(),
                        val: (el.value || '').replace(/\s+/g, ' ').trim().toLowerCase()
                      });
                      const nodes = [...document.querySelectorAll(
                        'img, a, input[type="image"], input[type="button"], button, span')];
                      let edit = nodes.find(n => visible(n) && (meta(n).title === 'modify' || meta(n).id.includes('imgmodify') || meta(n).id.includes('dvedit')));
                      if (!edit) {
                        edit = nodes.find(n => {
                          if (!visible(n)) return false;
                          const m = meta(n);
                          return m.title === 'edit' || m.val === 'edit' || m.val === 'modify'
                            || m.src.includes('edit.png') || m.src.includes('/edit') || m.src.includes('modify')
                            || (m.id.includes('edit') && !m.id.includes('credit'))
                            || (m.title.includes('edit') && !m.title.includes('credit'));
                        });
                      }
                      if (!edit) return false;
                      edit.click();
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

        try
        {
            var byId = root.Locator("#ToolBarsCtrl1_dvEdit, #ToolBarsCtrl1_imgModify, [title='Modify'], [title='Edit'], img[src*='Edit.png']");
            if (await byId.CountAsync() > 0 && await byId.First.IsVisibleAsync())
            {
                await byId.First.ClickAsync(new() { Force = true });
                return true;
            }
        }
        catch (PlaywrightException)
        {
        }

        return false;
    }

    private static async Task<ILocator?> FindOrganizationsNameInputAsync(ILocator root)
    {
        // Prefer the filter labeled Name (next to Alias) — never the sidebar placeholder "search".
        var byLabel = root.GetByLabel("Name", new() { Exact = true });
        if (await byLabel.CountAsync() > 0)
        {
            for (var i = 0; i < await byLabel.CountAsync(); i++)
            {
                var box = byLabel.Nth(i);
                if (await box.IsVisibleAsync() && await IsLikelyOrgNameFilterAsync(box))
                {
                    return box;
                }
            }
        }

        var byPlaceholder = root.GetByPlaceholder("Name", new() { Exact = false });
        if (await byPlaceholder.CountAsync() > 0)
        {
            for (var i = 0; i < await byPlaceholder.CountAsync(); i++)
            {
                var box = byPlaceholder.Nth(i);
                if (!await box.IsVisibleAsync())
                {
                    continue;
                }

                var ph = (await box.GetAttributeAsync("placeholder")) ?? string.Empty;
                if (ph.Equals("search", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (await IsLikelyOrgNameFilterAsync(box))
                {
                    return box;
                }
            }
        }

        // Softlink often has a Name label with a sibling input (not placeholder=search).
        var labeled = root.Locator("xpath=//*[normalize-space()='Name']/following::input[1]");
        if (await labeled.CountAsync() > 0)
        {
            for (var i = 0; i < Math.Min(await labeled.CountAsync(), 5); i++)
            {
                var box = labeled.Nth(i);
                if (await box.IsVisibleAsync() && await IsLikelyOrgNameFilterAsync(box))
                {
                    return box;
                }
            }
        }

        return null;
    }

    private static async Task<bool> IsLikelyOrgNameFilterAsync(ILocator box)
    {
        var placeholder = ((await box.GetAttributeAsync("placeholder")) ?? string.Empty).Trim();
        if (placeholder.Equals("search", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var id = ((await box.GetAttributeAsync("id")) ?? string.Empty).ToLowerInvariant();
        var nameAttr = ((await box.GetAttributeAsync("name")) ?? string.Empty).ToLowerInvariant();
        if (id.Contains("menusearch") || nameAttr.Contains("menusearch"))
        {
            return false;
        }

        return true;
    }

    private static async Task<bool> HasNameSearchAsync(IPage page)
    {
        foreach (var root in Roots(page))
        {
            if (await FindOrganizationsNameInputAsync(root) is not null)
            {
                return true;
            }

            var searchBtn = root.GetByRole(AriaRole.Button, new() { Name = "Search" });
            if (await searchBtn.CountAsync() > 0 && await searchBtn.First.IsVisibleAsync())
            {
                return true;
            }
        }

        return false;
    }

    // Softlink opens Organization Master as a separate tab/window after double-click.
    // When expectedName is set, ignore leftover Master tabs for a different organisation.
    public static async Task<IPage> ResolveOrgMasterPageAsync(
        IPage current,
        string? expectedName = null,
        IReadOnlyCollection<IPage>? pagesBeforeOpen = null)
    {
        var matched = await TryResolveOrgMasterPageAsync(current, expectedName, pagesBeforeOpen);
        return matched ?? current;
    }

    public static async Task<IPage?> TryResolveOrgMasterPageAsync(
        IPage current,
        string? expectedName = null,
        IReadOnlyCollection<IPage>? pagesBeforeOpen = null)
    {
        var expectedKey = string.IsNullOrWhiteSpace(expectedName) ? null : Key(expectedName);
        for (var attempt = 0; attempt < 25; attempt++)
        {
            // Softlink may open Organization Master in another CDP context/window.
            foreach (var candidate in EnumeratePages(current))
            {
                try
                {
                    if (!await IsOrgMasterOpenAsync(candidate))
                    {
                        continue;
                    }

                    if (expectedKey is null || await MasterShowsOrganisationAsync(candidate, expectedKey))
                    {
                        return candidate;
                    }

                    // Name field can lag after open — still accept a fresh OrganizationMaster tab.
                    if (pagesBeforeOpen is not null && !pagesBeforeOpen.Contains(candidate))
                    {
                        return candidate;
                    }
                }
                catch (PlaywrightException)
                {
                }
            }

            await current.WaitForTimeoutAsync(400);
        }

        return null;
    }

    // Softlink Organization Master often opens as a separate Edge window/page.
    public static async Task<IPage?> FindOrganizationMasterPageAsync(IPage anyPage, string? expectedName = null)
    {
        var expectedKey = string.IsNullOrWhiteSpace(expectedName) ? null : Key(expectedName);
        foreach (var candidate in EnumeratePages(anyPage))
        {
            try
            {
                var url = candidate.Url ?? string.Empty;
                if (!url.Contains("OrganizationMaster", StringComparison.OrdinalIgnoreCase)
                    && !url.Contains("Org_Id=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (expectedKey is null || await MasterShowsOrganisationAsync(candidate, expectedKey))
                {
                    return candidate;
                }

                // Fresh Master window — name field may still be hydrating.
                return candidate;
            }
            catch (PlaywrightException)
            {
            }
        }

        return null;
    }

    public static IEnumerable<IPage> EnumeratePages(IPage current)
    {
        var seen = new HashSet<IPage>();
        IEnumerable<IBrowserContext> Contexts()
        {
            yield return current.Context;
            var browser = current.Context.Browser;
            if (browser is null)
            {
                yield break;
            }

            foreach (var context in browser.Contexts)
            {
                yield return context;
            }
        }

        foreach (var context in Contexts())
        {
            IReadOnlyList<IPage> pages;
            try
            {
                pages = context.Pages;
            }
            catch (PlaywrightException)
            {
                continue;
            }

            // Newest pages first — Organization Master usually opens last.
            foreach (var page in pages.Reverse())
            {
                try
                {
                    if (page.IsClosed || !seen.Add(page))
                    {
                        continue;
                    }
                }
                catch (PlaywrightException)
                {
                    continue;
                }

                yield return page;
            }
        }
    }

    public static async Task CloseOrgMasterPagesAsync(IBrowserContext context, IPage? keep = null)
    {
        foreach (var page in context.Pages.ToList())
        {
            try
            {
                if (page.IsClosed || (keep is not null && ReferenceEquals(page, keep)))
                {
                    continue;
                }

                if (!await IsOrgMasterOpenAsync(page))
                {
                    continue;
                }

                try
                {
                    await page.CloseAsync();
                }
                catch (PlaywrightException)
                {
                }
            }
            catch (PlaywrightException)
            {
            }
        }
    }

    private static List<IPage> SnapshotPages(IBrowserContext context) =>
        context.Pages
            .Where(page =>
            {
                try
                {
                    return !page.IsClosed;
                }
                catch (PlaywrightException)
                {
                    return false;
                }
            })
            .ToList();

    private static async Task<bool> MasterShowsOrganisationAsync(IPage page, string expectedKey)
    {
        foreach (var root in Roots(page))
        {
            try
            {
                // Softlink shows the organisation name in the master header / Name field.
                var text = await root.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 2_000 });
                if (Key(text).Contains(expectedKey, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        try
        {
            var title = await page.TitleAsync();
            if (Key(title).Contains(expectedKey, StringComparison.Ordinal))
            {
                return true;
            }
        }
        catch (PlaywrightException)
        {
        }

        return false;
    }

    public static async Task<bool> IsOrgMasterOpenAsync(IPage page)
    {
        var url = page.Url ?? string.Empty;
        if (url.Contains("OrganizationMaster", StringComparison.OrdinalIgnoreCase)
            || url.Contains("Org_Id=", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var frame in page.Frames)
        {
            try
            {
                var frameUrl = frame.Url ?? string.Empty;
                if (frameUrl.Contains("OrganizationMaster", StringComparison.OrdinalIgnoreCase)
                    || frameUrl.Contains("Org_Id=", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        foreach (var root in Roots(page))
        {
            try
            {
                // Master form shows Account Info as a primary tab with Edit/Save at the bottom.
                var account = root.GetByText("Account Info", new() { Exact = true });
                if (await account.CountAsync() == 0 || !await account.First.IsVisibleAsync())
                {
                    continue;
                }

                var edit = root.GetByRole(AriaRole.Button, new() { Name = "Edit" });
                var saveClose = root.GetByRole(AriaRole.Button, new() { Name = "Save & Close" });
                if ((await edit.CountAsync() > 0 && await edit.First.IsVisibleAsync())
                    || (await saveClose.CountAsync() > 0 && await saveClose.First.IsVisibleAsync()))
                {
                    return true;
                }

                // Softlink often uses <input type="button" value="Edit">.
                var editInput = root.Locator("input[type='button'][value='Edit'], input[type='submit'][value='Edit']");
                if (await editInput.CountAsync() > 0 && await editInput.First.IsVisibleAsync())
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

    private static async Task<bool> ClickTitleOrTextAsync(ILocator root, string text)
    {
        var byTitle = root.Locator($"[title='{text}'], [aria-label='{text}']");
        if (await byTitle.CountAsync() > 0 && await byTitle.First.IsVisibleAsync())
        {
            await byTitle.First.ClickAsync();
            return true;
        }

        return await ClickExactAsync(root, text);
    }

    private static async Task<bool> ClickExactAsync(ILocator root, string text)
    {
        var link = root.GetByRole(AriaRole.Link, new() { Name = text, Exact = true });
        if (await link.CountAsync() > 0 && await link.First.IsVisibleAsync())
        {
            await link.First.ClickAsync();
            return true;
        }

        var exact = root.GetByText(text, new() { Exact = true });
        if (await exact.CountAsync() > 0 && await exact.First.IsVisibleAsync())
        {
            await exact.First.ClickAsync();
            return true;
        }

        return false;
    }
}
