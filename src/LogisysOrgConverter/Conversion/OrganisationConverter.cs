using System.Text.RegularExpressions;

namespace LogisysOrgConverter.Conversion;

public sealed record ReadyOrganisation(
    int SourceRowNumber,
    string TemplateCode,
    string Name,
    string BranchName,
    string City,
    string State,
    string Country,
    IReadOnlyDictionary<string, string> Cells,
    string? RequesterEmail = null,
    string? RequesterName = null);

public sealed record HeldOrganisation(
    int SourceRowNumber,
    string Name,
    string? BranchName,
    string? City,
    string? State,
    string? Country,
    string Reason,
    string? RequesterEmail = null,
    string? RequesterName = null);

public sealed class ConversionResult
{
    public List<ReadyOrganisation> India { get; } = [];

    public List<ReadyOrganisation> Overseas { get; } = [];

    public List<HeldOrganisation> Held { get; } = [];
}

public sealed class OrganisationConverter
{
    private static readonly string[] StreetSuffixes =
    [
        "DRIVE", "DR", "STREET", "ST", "ROAD", "RD", "AVENUE", "AVE", "LANE", "LN",
        "BOULEVARD", "BLVD", "PARKWAY", "PKWY", "COURT", "CT", "WAY", "PLACE", "PL",
        "HIGHWAY", "HWY", "FLOOR", "SUITE", "STE", "TRAIL", "CIRCLE", "CIR"
    ];

    private static readonly Regex PanPattern = new("^[A-Z]{5}[0-9]{4}[A-Z]$", RegexOptions.Compiled);

    private static readonly Regex GstinPattern = new("^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][0-9A-Z]Z[0-9A-Z]$", RegexOptions.Compiled);

    // When the requested organisation is to be invoiced (column I = Yes), the form stores
    // its details in J..U and the official email in AI, leaving W..AG blank.
    private static readonly (string Main, string Invoiced)[] InvoicedSectionColumns =
    [
        ("W", "J"), ("X", "K"), ("Y", "N"), ("Z", "O"), ("AA", "P"), ("AB", "Q"),
        ("AC", "R"), ("AD", "S"), ("AE", "T"), ("AF", "U"), ("AG", "AI")
    ];

    public ConversionResult Convert(IReadOnlyList<ClientRow> rows, MappingCatalog catalog)
    {
        var result = new ConversionResult();
        var seenNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceRow in rows)
        {
            var row = UseInvoicedSectionWhenMainIsBlank(sourceRow);
            var name = First(row, "W", "J");
            var city = Value(row, "AB");
            var state = Value(row, "AC");
            var countryRaw = Value(row, "AD");
            var canonical = ResolveCountry(countryRaw, catalog);
            var requesterEmail = CleanEmail(Value(row, "B"));
            var requesterName = Value(row, "D");
            var reasons = new List<string>();

            if (name.Length == 0)
            {
                reasons.Add(HoldReason.MissingName);
            }
            else if (seenNames.TryGetValue(name, out var firstRow))
            {
                reasons.Add(HoldReason.DuplicateName(name, firstRow));
            }
            else
            {
                seenNames[name] = row.RowNumber;
            }

            if (countryRaw.Length == 0)
            {
                reasons.Add(HoldReason.BlankCountry);
            }
            else if (canonical is null)
            {
                reasons.Add(HoldReason.UnknownCountry(countryRaw));
            }

            if (LooksLikeStreet(city))
            {
                reasons.Add(HoldReason.StreetCity(city));
            }
            else if (canonical is not null
                && city.Length > 0
                && state.Length > 0
                && city.Equals(state, StringComparison.OrdinalIgnoreCase)
                && !IsKnownState(catalog, canonical, state))
            {
                reasons.Add(HoldReason.CitySameAsState(state, canonical));
            }

            var template = canonical == MappingCatalog.IndiaCountry
                ? MappingCatalog.IndiaTemplate
                : MappingCatalog.OverseasTemplate;
            var cells = BuildCells(row, catalog, template, canonical);

            if (template == MappingCatalog.IndiaTemplate && canonical is not null)
            {
                reasons.AddRange(IndiaRegistrationProblems(cells, state, catalog));
            }

            if (reasons.Count > 0)
            {
                result.Held.Add(new HeldOrganisation(
                    row.RowNumber,
                    name,
                    city,
                    city,
                    state,
                    canonical ?? countryRaw,
                    string.Join(" ", reasons),
                    requesterEmail,
                    requesterName));
                continue;
            }

            var ready = new ReadyOrganisation(
                row.RowNumber,
                template,
                name,
                city,
                city,
                state,
                canonical!,
                cells,
                requesterEmail,
                requesterName);

            if (template == MappingCatalog.IndiaTemplate)
            {
                result.India.Add(ready);
            }
            else
            {
                result.Overseas.Add(ready);
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> BuildCells(
        ClientRow row,
        MappingCatalog catalog,
        string template,
        string? canonicalCountry)
    {
        var flags = ReadFlags(Value(row, "X"));
        var cells = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var map in catalog.FieldMaps.Where(map => map.TemplateCode == template).OrderBy(map => map.SortOrder))
        {
            var value = map.Transform switch
            {
                "Name" => First(row, map.SourceColumn, map.FallbackColumn),
                "Direct" => Value(row, map.SourceColumn),
                "Constant" => map.ConstantValue?.Trim() ?? string.Empty,
                "Country" => canonicalCountry ?? string.Empty,
                "Email" => CleanEmail(Value(row, map.SourceColumn)) ?? string.Empty,
                "OrgFlag" => map.FlagKey is not null && flags.Contains(map.FlagKey) ? "yes" : string.Empty,
                "Registration" => CleanRegistration(Value(row, map.SourceColumn)),
                _ => string.Empty
            };

            if (value.Length > 0)
            {
                cells[map.TargetHeader] = value;
            }
        }

        return cells;
    }

    private static IEnumerable<string> IndiaRegistrationProblems(
        IReadOnlyDictionary<string, string> cells,
        string state,
        MappingCatalog catalog)
    {
        cells.TryGetValue("REG-PAN NO", out var pan);
        cells.TryGetValue("REG-GSTIN", out var gstin);
        pan ??= string.Empty;
        gstin ??= string.Empty;

        if (pan.Length > 0 && !PanPattern.IsMatch(pan))
        {
            yield return HoldReason.InvalidPan(pan);
        }

        if (gstin.Length == 0)
        {
            yield break;
        }

        if (!GstinPattern.IsMatch(gstin))
        {
            yield return HoldReason.InvalidGstin(gstin);
            yield break;
        }

        if (pan.Length > 0 && PanPattern.IsMatch(pan) && gstin.Substring(2, 10) != pan)
        {
            yield return HoldReason.GstinPanMismatch(gstin, pan);
        }

        var matches = catalog.KnownStates
            .Where(known => known.CountryCanonical.Equals(MappingCatalog.IndiaCountry, StringComparison.OrdinalIgnoreCase)
                && known.Name.Equals(state, StringComparison.OrdinalIgnoreCase))
            .Select(known => known.GstCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (matches.Count == 0)
        {
            yield return HoldReason.UnknownGstState(state);
        }
        else if (!matches.Contains(gstin[..2]))
        {
            yield return HoldReason.GstinStateMismatch(gstin, state);
        }
    }

    private static HashSet<string> ReadFlags(string raw)
    {
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var text = token.ToLowerInvariant();
            if (text.Contains("shipping line") || text.Contains("airline") || text.Contains("carrier"))
            {
                flags.Add("Carrier");
            }
            else if (text.Contains("shipper"))
            {
                flags.Add("Shipper");
            }
            else if (text.Contains("consignee"))
            {
                flags.Add("Consignee");
            }
            else if (text.Contains("agent"))
            {
                flags.Add("Agent");
            }
            else if (text.Contains("service"))
            {
                flags.Add("ServiceProvider");
            }
        }

        return flags;
    }

    public static string? CleanEmail(string raw)
    {
        var text = raw.Trim();
        var at = text.IndexOf('@');
        if (at <= 0 || at != text.LastIndexOf('@'))
        {
            return null;
        }

        var domain = text[(at + 1)..];
        if (!domain.Contains('.') || domain.StartsWith('.') || domain.EndsWith('.') || domain.Contains(' '))
        {
            return null;
        }

        return text;
    }

    private static string CleanRegistration(string raw) =>
        raw.Trim().Replace(" ", string.Empty).ToUpperInvariant();

    private static bool LooksLikeStreet(string city)
    {
        var parts = city.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var last = parts[^1].TrimEnd('.').ToUpperInvariant();
        return StreetSuffixes.Contains(last);
    }

    private static bool IsKnownState(MappingCatalog catalog, string country, string state) =>
        catalog.KnownStates.Any(known =>
            known.CountryCanonical.Equals(country, StringComparison.OrdinalIgnoreCase)
            && (known.Name.Equals(state, StringComparison.OrdinalIgnoreCase)
                || (known.Abbreviation is not null && known.Abbreviation.Equals(state, StringComparison.OrdinalIgnoreCase))));

    private static string? ResolveCountry(string raw, MappingCatalog catalog)
    {
        if (raw.Length == 0)
        {
            return null;
        }

        var key = MappingSeed.NormalizeAlias(raw);
        return catalog.CountryAliases.TryGetValue(key, out var canonical) ? canonical : null;
    }

    private static ClientRow UseInvoicedSectionWhenMainIsBlank(ClientRow row)
    {
        if (Value(row, "W").Length > 0 || Value(row, "J").Length == 0)
        {
            return row;
        }

        var cells = new Dictionary<string, string>(row.Cells, StringComparer.OrdinalIgnoreCase);
        foreach (var (main, invoiced) in InvoicedSectionColumns)
        {
            var value = Value(row, invoiced);
            if (value.Length > 0)
            {
                cells[main] = value;
            }
            else
            {
                cells.Remove(main);
            }
        }

        return row with { Cells = cells };
    }

    private static string Value(ClientRow row, string? column)
    {
        if (column is null)
        {
            return string.Empty;
        }

        return row.Cells.TryGetValue(column, out var value) ? value.Trim() : string.Empty;
    }

    private static string First(ClientRow row, params string?[] columns)
    {
        foreach (var column in columns)
        {
            var value = Value(row, column);
            if (value.Length > 0)
            {
                return value;
            }
        }

        return string.Empty;
    }
}
