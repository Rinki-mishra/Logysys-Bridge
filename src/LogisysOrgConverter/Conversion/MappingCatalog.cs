namespace LogisysOrgConverter.Conversion;

public sealed record FieldMapRule(
    string TemplateCode,
    string TargetHeader,
    string Transform,
    string? SourceColumn,
    string? ConstantValue,
    string? FlagKey,
    string? FallbackColumn,
    int SortOrder);

public sealed record CountryAliasRule(string CanonicalName, string Alias);

public sealed record KnownStateRule(string CountryCanonical, string Name, string? Abbreviation, string? GstCode);

public sealed class MappingCatalog
{
    public required IReadOnlyList<FieldMapRule> FieldMaps { get; init; }

    public required IReadOnlyDictionary<string, string> CountryAliases { get; init; }

    public required IReadOnlyList<KnownStateRule> KnownStates { get; init; }

    // Name key (see OrganisationConverter.NameKey) of every organisation already in Logi-Sys, with where it was found.
    public IReadOnlyDictionary<string, string> ExistingOrganisations { get; init; } = new Dictionary<string, string>();

    public const string IndiaTemplate = "IN";

    public const string OverseasTemplate = "OVERSEAS";

    public const string IndiaCountry = "India";
}
