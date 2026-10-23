using LogisysOrgConverter.Conversion;
using Microsoft.EntityFrameworkCore;

namespace LogisysOrgConverter.Data;

public static class DbInitializer
{
    public static async Task SeedAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
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

        if (!await db.CountryAliases.AnyAsync(cancellationToken))
        {
            db.CountryAliases.AddRange(MappingSeed.Countries.Select(country => new CountryAlias
            {
                Alias = country.Alias,
                AliasKey = MappingSeed.NormalizeAlias(country.Alias),
                CanonicalName = country.CanonicalName
            }));
        }

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
