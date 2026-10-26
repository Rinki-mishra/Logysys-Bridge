using System.Globalization;
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
    string? RequesterName = null,
    string? Note = null,
    LedgerChecklist? Ledger = null);

public sealed record MergedOrganisation(
    int SourceRowNumber,
    string Name,
    int MergedIntoRowNumber,
    bool AddressUsed,
    string? City,
    string? Country,
    string? RequesterEmail,
    string? RequesterName);

public sealed record AddressUpdate(
    int SourceRowNumber,
    string Name,
    string ExistsIn,
    string BranchAddress1,
    string BranchAddress2,
    string BranchAddress3,
    string City,
    string State,
    string Country,
    string PostalCode,
    string Telephone,
    bool HasAddress,
    DateTime? RequestedAt,
    int? AddressFromRow,
    string? RequesterEmail = null,
    string? RequesterName = null,
    LedgerChecklist? Ledger = null);

public sealed record HeldOrganisation(
    int SourceRowNumber,
    string Name,
    string? BranchName,
    string? City,
    string? State,
    string? Country,
    string Reason,
    string? RequesterEmail = null,
    string? RequesterName = null,
    LedgerChecklist? Ledger = null);

public sealed class ConversionResult
{
    public List<ReadyOrganisation> India { get; } = [];

    public List<ReadyOrganisation> Overseas { get; } = [];

    public List<HeldOrganisation> Held { get; } = [];

    public List<MergedOrganisation> Merged { get; } = [];

    public List<AddressUpdate> Updates { get; } = [];
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

    private static readonly string[] AddressColumns = ["Y", "Z", "AA", "AB", "AC", "AD", "AE"];

    private static readonly Regex NonAlphanumeric = new("[^A-Z0-9]", RegexOptions.Compiled);

    public ConversionResult Convert(IReadOnlyList<ClientRow> rows, MappingCatalog catalog)
    {
        var result = new ConversionResult();
        var locationNotes = new Dictionary<int, string>();
        var prepared = rows
            .Select(UseInvoicedSectionWhenMainIsBlank)
            .Select(row => FillLocationFromAddress(row, catalog, locationNotes))
            .ToList();
        var organisations = MergeDuplicates(prepared, result.Merged);

        foreach (var (row, addressFromRow) in organisations)
        {
            var name = First(row, "W", "J");
            var notes = new List<string>();
            if (addressFromRow is int from)
            {
                notes.Add(HoldReason.AddressUpdatedFrom(from));
            }

            if (locationNotes.TryGetValue(addressFromRow ?? row.RowNumber, out var located))
            {
                notes.Add(located);
            }

            var note = notes.Count > 0 ? string.Join(" ", notes) : null;
            var city = Value(row, "AB");
            var state = Value(row, "AC");
            var countryRaw = Value(row, "AD");
            var canonical = ResolveCountry(countryRaw, catalog);
            var requesterEmail = CleanEmail(Value(row, "B"));
            var requesterName = Value(row, "D");
            var reasons = new List<string>();
            var ledger = LedgerRules.IsInvoiced(row.Cells) ? LedgerRules.Build(row.Cells, canonical) : null;

            if (name.Length > 0 && catalog.ExistingOrganisations.TryGetValue(NameKey(name), out var existsIn))
            {
                var requestedAt = RequestedAt(row);
                result.Updates.Add(new AddressUpdate(
                    row.RowNumber,
                    name,
                    existsIn,
                    Value(row, "Y"),
                    Value(row, "Z"),
                    Value(row, "AA"),
                    city,
                    state,
                    canonical ?? countryRaw,
                    Value(row, "AE"),
                    Value(row, "AF"),
                    HasText(city) && HasText(countryRaw),
                    requestedAt == DateTime.MinValue ? null : requestedAt,
                    addressFromRow,
                    requesterEmail,
                    requesterName,
                    ledger));
                continue;
            }

            if (name.Length == 0)
            {
                reasons.Add(HoldReason.MissingName);
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
            else if (ResolveCountry(city, catalog) is { } cityCountry && cityCountry != canonical)
            {
                reasons.Add(HoldReason.CityIsCountry(city));
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
                if (note is not null)
                {
                    reasons.Add(note);
                }

                result.Held.Add(new HeldOrganisation(
                    row.RowNumber,
                    name,
                    city,
                    city,
                    state,
                    canonical ?? countryRaw,
                    string.Join(" ", reasons),
                    requesterEmail,
                    requesterName,
                    ledger));
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
                requesterName,
                note,
                ledger);

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

        var key = MappingSeed.NormalizeAlias(raw.Trim().TrimEnd('.', ',', ';'));
        return catalog.CountryAliases.TryGetValue(key, out var canonical) ? canonical : null;
    }

    // Rows naming the same organisation (ignoring case, spaces and punctuation) become one
    // organisation, created once from the first row in the sheet. Its address comes from the
    // latest request (column A timestamp) that has a real city and country.
    private static List<(ClientRow Row, int? AddressFromRow)> MergeDuplicates(
        IEnumerable<ClientRow> rows,
        List<MergedOrganisation> merged)
    {
        var groups = new List<List<ClientRow>>();
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var key = NameKey(First(row, "W", "J"));
            if (key.Length > 0 && indexByName.TryGetValue(key, out var index))
            {
                groups[index].Add(row);
                continue;
            }

            if (key.Length > 0)
            {
                indexByName[key] = groups.Count;
            }

            groups.Add([row]);
        }

        var organisations = new List<(ClientRow Row, int? AddressFromRow)>();
        foreach (var group in groups)
        {
            var kept = group[0];
            if (group.Count == 1)
            {
                organisations.Add((kept, null));
                continue;
            }

            var latestAddress = group
                .Where(row => HasText(Value(row, "AB")) && HasText(Value(row, "AD")))
                .OrderBy(RequestedAt)
                .ThenBy(row => row.RowNumber)
                .LastOrDefault();

            var combined = group.Skip(1).Aggregate(kept, MergeInto);
            int? addressFromRow = null;
            if (latestAddress is not null && latestAddress.RowNumber != kept.RowNumber)
            {
                combined = WithAddressFrom(combined, latestAddress);
                addressFromRow = latestAddress.RowNumber;
            }

            organisations.Add((combined, addressFromRow));
            foreach (var row in group.Skip(1))
            {
                merged.Add(new MergedOrganisation(
                    row.RowNumber,
                    First(row, "W", "J"),
                    kept.RowNumber,
                    row.RowNumber == addressFromRow,
                    Value(row, "AB"),
                    Value(row, "AD"),
                    CleanEmail(Value(row, "B")),
                    Value(row, "D")));
            }
        }

        return organisations;
    }

    private static DateTime RequestedAt(ClientRow row)
    {
        var text = Value(row, "A");
        if (DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            || DateTime.TryParse(text, CultureInfo.GetCultureInfo("en-IN"), DateTimeStyles.None, out at))
        {
            return at;
        }

        return DateTime.MinValue;
    }

    private static ClientRow WithAddressFrom(ClientRow target, ClientRow source)
    {
        var cells = new Dictionary<string, string>(target.Cells, StringComparer.OrdinalIgnoreCase);
        foreach (var column in AddressColumns.Append("A"))
        {
            var value = Value(source, column);
            if (value.Length > 0)
            {
                cells[column] = value;
            }
            else
            {
                cells.Remove(column);
            }
        }

        return target with { Cells = cells };
    }

    private static ClientRow MergeInto(ClientRow kept, ClientRow later)
    {
        var cells = new Dictionary<string, string>(kept.Cells, StringComparer.OrdinalIgnoreCase);

        var types = ReadTypes(Value(kept, "X")).Concat(ReadTypes(Value(later, "X")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (types.Count > 0)
        {
            cells["X"] = string.Join(", ", types);
        }

        if (LedgerRules.IsInvoiced(later.Cells))
        {
            cells["I"] = later.Cells["I"];
        }

        foreach (var (column, value) in later.Cells)
        {
            if (!cells.ContainsKey(column) && !AddressColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
            {
                cells[column] = value;
            }
        }

        return kept with { Cells = cells };
    }

    private static IEnumerable<string> ReadTypes(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool HasText(string value) => value.Any(char.IsLetterOrDigit);

    public static string NameKey(string name) => NonAlphanumeric.Replace(name.ToUpperInvariant(), string.Empty);

    private static ClientRow FillLocationFromAddress(ClientRow row, MappingCatalog catalog, Dictionary<int, string> notes)
    {
        (string Column, string Label)[] location = [("AB", "city"), ("AC", "state"), ("AD", "country")];
        var missing = location.Where(item => !HasText(Value(row, item.Column))).ToList();
        if (missing.Count == 0)
        {
            return row;
        }

        var cells = new Dictionary<string, string>(row.Cells, StringComparer.OrdinalIgnoreCase);
        foreach (var (column, _) in missing)
        {
            cells.Remove(column);
        }

        var knownCountry = ResolveCountry(Value(row, "AD"), catalog);
        var located = AddressLocator.Locate([Value(row, "Y"), Value(row, "Z"), Value(row, "AA")], catalog, knownCountry);
        var found = new List<string>();
        var notFound = new List<string>();
        foreach (var (column, label) in missing)
        {
            var value = column switch
            {
                "AB" => located.City,
                "AC" => located.State,
                _ => located.Country
            };

            if (!string.IsNullOrWhiteSpace(value))
            {
                cells[column] = value;
                found.Add($"{label} {value}");
            }
            else if (column != "AC")
            {
                notFound.Add(label);
            }
        }

        if (found.Count > 0 && !HasText(Value(row, "AE")) && !string.IsNullOrWhiteSpace(located.PostalCode))
        {
            cells["AE"] = located.PostalCode;
            found.Add($"postal code {located.PostalCode}");
        }

        var parts = new List<string>();
        if (found.Count > 0)
        {
            parts.Add(HoldReason.ReadFromAddress(found));
        }

        if (notFound.Count > 0)
        {
            parts.Add(HoldReason.NotInAddress(notFound));
        }

        if (parts.Count > 0)
        {
            notes[row.RowNumber] = string.Join(" ", parts);
        }

        return row with { Cells = cells };
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
