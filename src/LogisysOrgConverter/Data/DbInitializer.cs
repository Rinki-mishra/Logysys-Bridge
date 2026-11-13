using LogisysOrgConverter.Conversion;
using Microsoft.EntityFrameworkCore;

namespace LogisysOrgConverter.Data;

public static class DbInitializer
{
    public static async Task SeedAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var retired = MappingSeed.RetiredTargetHeaders
            .Concat(["IsCashOnDelivery"])
            .ToList();
        db.FieldMaps.RemoveRange(await db.FieldMaps.Where(map => retired.Contains(map.TargetHeader)).ToListAsync(cancellationToken));

        if (!await db.FieldMaps.AnyAsync(cancellationToken))
        {
            db.FieldMaps.AddRange(MappingSeed.FieldMaps.Select(ToFieldMap));
        }
        else
        {
            // Field maps added to the seed later (for example IsCashOnDelivery) are filled in.
            var existing = await db.FieldMaps.ToListAsync(cancellationToken);
            var saved = existing
                .Select(map => map.TemplateCode + "\n" + map.TargetHeader)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var map in MappingSeed.FieldMaps)
            {
                var key = map.TemplateCode + "\n" + map.TargetHeader;
                var current = existing.FirstOrDefault(item =>
                    item.TemplateCode.Equals(map.TemplateCode, StringComparison.OrdinalIgnoreCase)
                    && item.TargetHeader.Equals(map.TargetHeader, StringComparison.OrdinalIgnoreCase));
                if (current is null)
                {
                    if (saved.Add(key))
                    {
                        db.FieldMaps.Add(ToFieldMap(map));
                    }

                    continue;
                }

                // Keep seeded constants in sync (e.g. Cash On Delivery must be Y for Softlink).
                if (map.Transform == "Constant"
                    && !string.Equals(current.ConstantValue, map.ConstantValue, StringComparison.Ordinal))
                {
                    current.ConstantValue = map.ConstantValue;
                    current.Transform = map.Transform;
                    current.SortOrder = map.SortOrder;
                }
            }
        }

        // Re-key saved aliases so punctuation variants (UAE / U.A.E / U.A.E.) share one key.
        var existingAliases = await db.CountryAliases.ToListAsync(cancellationToken);
        var savedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in existingAliases)
        {
            var key = MappingSeed.NormalizeAlias(alias.Alias);
            if (!savedKeys.Add(key))
            {
                db.CountryAliases.Remove(alias);
                continue;
            }

            alias.AliasKey = key;
        }

        // Countries added to the seed later are filled in; spellings already saved (and renamed) are left alone.
        db.CountryAliases.AddRange(MappingSeed.Countries
            .Select(country => (country, Key: MappingSeed.NormalizeAlias(country.Alias)))
            .Where(item => savedKeys.Add(item.Key))
            .Select(item => new CountryAlias
            {
                Alias = item.country.Alias,
                AliasKey = item.Key,
                CanonicalName = item.country.CanonicalName
            }));

        // States/regions added to the seed later (e.g. Italy) are filled in without wiping edits.
        var existingStates = await db.KnownStates.ToListAsync(cancellationToken);
        var stateKeys = existingStates
            .Select(state => state.CountryCanonical + "\n" + state.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        db.KnownStates.AddRange(MappingSeed.KnownStates
            .Where(state => stateKeys.Add(state.CountryCanonical + "\n" + state.Name))
            .Select(state => new KnownState
            {
                CountryCanonical = state.CountryCanonical,
                Name = state.Name,
                Abbreviation = state.Abbreviation,
                GstCode = state.GstCode
            }));

        await db.SaveChangesAsync(cancellationToken);
    }

    private static FieldMap ToFieldMap(FieldMapRule map) => new()
    {
        TemplateCode = map.TemplateCode,
        TargetHeader = map.TargetHeader,
        Transform = map.Transform,
        SourceColumn = map.SourceColumn,
        ConstantValue = map.ConstantValue,
        FlagKey = map.FlagKey,
        FallbackColumn = map.FallbackColumn,
        SortOrder = map.SortOrder
    };
}
