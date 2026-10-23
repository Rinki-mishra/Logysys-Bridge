using LogisysOrgConverter.Conversion;
using LogisysOrgConverter.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace LogisysOrgConverter.Pages.Rules;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db)
    {
        _db = db;
    }

    public IReadOnlyList<CountryGroup> Countries { get; private set; } = [];

    public IReadOnlyList<FieldMap> FieldMaps { get; private set; } = [];

    public string? Message { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        Message = TempData["Message"] as string;
    }

    public async Task<IActionResult> OnPostRenameAsync(string oldName, string canonical, CancellationToken cancellationToken)
    {
        var next = canonical?.Trim() ?? string.Empty;
        var previous = oldName?.Trim() ?? string.Empty;
        if (previous.Length == 0 || next.Length == 0)
        {
            TempData["Message"] = "The country name cannot be blank.";
            return RedirectToPage();
        }

        if (!previous.Equals(next, StringComparison.Ordinal))
        {
            var aliases = await _db.CountryAliases.Where(item => item.CanonicalName == previous).ToListAsync(cancellationToken);
            foreach (var alias in aliases)
            {
                alias.CanonicalName = next;
            }

            var states = await _db.KnownStates.Where(item => item.CountryCanonical == previous).ToListAsync(cancellationToken);
            foreach (var state in states)
            {
                state.CountryCanonical = next;
            }

            await _db.SaveChangesAsync(cancellationToken);
        }

        TempData["Message"] = $"Country name saved as {next}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddAsync(string alias, string canonical, CancellationToken cancellationToken)
    {
        var aliasText = alias?.Trim() ?? string.Empty;
        var canonicalText = canonical?.Trim() ?? string.Empty;
        if (aliasText.Length == 0 || canonicalText.Length == 0)
        {
            TempData["Message"] = "Enter both the client text and the Logi-Sys country name.";
            return RedirectToPage();
        }

        var key = MappingSeed.NormalizeAlias(aliasText);
        var existing = await _db.CountryAliases.FirstOrDefaultAsync(item => item.AliasKey == key, cancellationToken);
        if (existing is null)
        {
            _db.CountryAliases.Add(new CountryAlias
            {
                Alias = aliasText,
                AliasKey = key,
                CanonicalName = canonicalText
            });
            TempData["Message"] = $"Added {aliasText} as {canonicalText}.";
        }
        else
        {
            existing.Alias = aliasText;
            existing.CanonicalName = canonicalText;
            TempData["Message"] = $"Updated {aliasText} to {canonicalText}.";
        }

        await _db.SaveChangesAsync(cancellationToken);
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var aliases = await _db.CountryAliases.AsNoTracking().OrderBy(item => item.CanonicalName).ThenBy(item => item.Alias).ToListAsync(cancellationToken);
        Countries = aliases
            .GroupBy(item => item.CanonicalName)
            .Select(group => new CountryGroup(group.Key, group.Select(item => item.Alias).ToList()))
            .ToList();
        FieldMaps = await _db.FieldMaps.AsNoTracking().OrderBy(item => item.TemplateCode).ThenBy(item => item.SortOrder).ToListAsync(cancellationToken);
    }

    public sealed record CountryGroup(string Canonical, IReadOnlyList<string> Aliases);
}
