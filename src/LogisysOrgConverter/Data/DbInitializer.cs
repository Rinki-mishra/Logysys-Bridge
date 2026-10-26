using LogisysOrgConverter.Conversion;
using Microsoft.EntityFrameworkCore;

namespace LogisysOrgConverter.Data;

public static class DbInitializer
{
    public static async Task SeedAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var retired = MappingSeed.RetiredTargetHeaders.ToList();
        db.FieldMaps.RemoveRange(await db.FieldMaps.Where(map => retired.Contains(map.TargetHeader)).ToListAsync(cancellationToken));

        if (!await db.FieldMaps.AnyAsync(cancellationToken))
        {
            db.FieldMaps.AddRange(MappingSeed.FieldMaps.Select(map => new FieldMap
            {
                TemplateCode = map.TemplateCode,
                TargetHeader = map.TargetHeader,
                Transform = map.Transform,
                SourceColumn = map.SourceColumn,
                ConstantValue = map.ConstantValue,
                FlagKey = map.FlagKey,
                FallbackColumn = map.FallbackColumn,
                SortOrder = map.SortOrder
            }));
        }

        // Countries added to the seed later are filled in; spellings already saved (and renamed) are left alone.
        var savedKeys = (await db.CountryAliases.Select(item => item.AliasKey).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        db.CountryAliases.AddRange(MappingSeed.Countries
            .Select(country => (country, Key: MappingSeed.NormalizeAlias(country.Alias)))
            .Where(item => savedKeys.Add(item.Key))
            .Select(item => new CountryAlias
            {
                Alias = item.country.Alias,
                AliasKey = item.Key,
                CanonicalName = item.country.CanonicalName
            }));

        if (!await db.KnownStates.AnyAsync(cancellationToken))
        {
            db.KnownStates.AddRange(MappingSeed.KnownStates.Select(state => new KnownState
            {
                CountryCanonical = state.CountryCanonical,
                Name = state.Name,
                Abbreviation = state.Abbreviation,
                GstCode = state.GstCode
            }));
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
