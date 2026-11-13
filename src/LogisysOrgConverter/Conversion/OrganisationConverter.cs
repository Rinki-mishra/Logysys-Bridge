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

    // Overseas agents are only created after approval, so they are kept out of the overseas file.
    public List<ReadyOrganisation> AwaitingApproval { get; } = [];
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

    // Postal codes stuck on the country cell, e.g. "CANADA J7C 5N5" or "USA 45040".
    private static readonly Regex[] TrailingPostalPatterns =
    [
        new(@"^(?<country>.+?)\s+(?<postal>[A-Z]\d[A-Z]\s?\d[A-Z]\d)$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^(?<country>.+?)\s+(?<postal>[A-Z]{1,2}\d[A-Z\d]?\s?\d[A-Z]{2})$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^(?<country>.+?)\s+(?<postal>\d{5}(?:-\d{4})?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^(?<country>.+?)\s+(?<postal>[1-9]\d{5})$", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    ];

    private static readonly Regex[] LeadingPostalPatterns =
    [
        new(@"^(?<postal>[A-Z]\d[A-Z]\s?\d[A-Z]\d)\s+(?<country>.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^(?<postal>\d{5}(?:-\d{4})?)\s+(?<country>.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^(?<postal>[1-9]\d{5})\s+(?<country>.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    ];

    // When the requested organisation is to be invoiced (column I = Yes), the form stores
    // its details in J..U and the official email in AI, leaving W..AG blank.
    private static readonly (string Main, string Invoiced)[] InvoicedSectionColumns =
    [
        ("W", "J"), ("X", "K"), ("Y", "N"), ("Z", "O"), ("AA", "P"), ("AB", "Q"),
        ("AC", "R"), ("AD", "S"), ("AE", "T"), ("AF", "U"), ("AG", "AI")
    ];

    public const string GstNumberColumn = "AO";

    public const string GstDocumentColumn = "AP";

    public const string PanColumn = "AM";

    private static readonly string[] AddressColumns = ["Y", "Z", "AA", "AB", "AC", "AD", "AE"];

    private static readonly Regex NonAlphanumeric = new("[^A-Z0-9]", RegexOptions.Compiled);

    public ConversionResult Convert(
        IReadOnlyList<ClientRow> rows,
        MappingCatalog catalog,
        IReadOnlyDictionary<string, GstCertificate>? gstCertificates = null)
    {
        var result = new ConversionResult();
        var locationNotes = new Dictionary<int, string>();
        var prepared = rows
            .Select(UseInvoicedSectionWhenMainIsBlank)
            .Select(row => FillLocationFromAddress(row, catalog, locationNotes))
            .ToList();
        var organisations = MergeDuplicates(prepared, result.Merged);

        foreach (var (organisation, addressFromRow) in organisations)
        {
            var row = organisation;
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
            else if (ResolveCountry(city, catalog) is { } cityCountry
                && canonical is not null
                && cityCountry.Equals(canonical, StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add(HoldReason.CityIsOwnCountry(city));
            }
            else if (ResolveCountry(city, catalog) is { } otherCountry && otherCountry != canonical)
            {
                reasons.Add(HoldReason.CityIsCountry(city));
            }
            else if (canonical is not null
                && city.Length > 0
                && state.Length > 0
                && city.Equals(state, StringComparison.OrdinalIgnoreCase)
                && !IsKnownState(catalog, canonical, state))
            {
                if (TryRepairDuplicatedCityState(row, catalog, canonical, ref city, ref state, out var repairNote))
                {
                    row = WithCityState(row, city, state);
                    notes.Add(repairNote);
                }
                else
                {
                    reasons.Add(HoldReason.CitySameAsState(state, canonical));
                }
            }

            var template = canonical == MappingCatalog.IndiaCountry
                ? MappingCatalog.IndiaTemplate
                : MappingCatalog.OverseasTemplate;
            if (template == MappingCatalog.IndiaTemplate)
            {
                row = UseGstinFromCertificate(row, gstCertificates, notes);
            }

            var note = notes.Count > 0 ? string.Join(" ", notes) : null;
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
            else if (ReadFlags(Value(row, "X")).Contains("Agent"))
            {
                result.AwaitingApproval.Add(ready with { Note = AgentApprovalDetails(row, note) });
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

    // City and state were both filled with the city name (or a garbled state). Prefer the state
    // from the address lines, then a known city→state map, then a close spelling of a state name.
    // If none of those work (common for Italy and other countries without a state list), keep the
    // city and clear the invalid state so the organisation is still created.
    private static bool TryRepairDuplicatedCityState(
        ClientRow row,
        MappingCatalog catalog,
        string country,
        ref string city,
        ref string state,
        out string note)
    {
        note = string.Empty;
        var shared = city;
        // Use address lines only — AB/AC are the duplicated bad values and must not pollute the parse.
        var located = AddressLocator.Locate(
            [Value(row, "Y"), Value(row, "Z"), Value(row, "AA")],
            catalog,
            country);

        // City/state were filled with the country name itself — only repair when address has a real city.
        if (ResolveCountry(shared, catalog) is { } sharedAsCountry
            && sharedAsCountry.Equals(country, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsBetterCity(located.City, state: null, shared, catalog))
            {
                return false;
            }

            city = located.City!;
            state = !string.IsNullOrWhiteSpace(located.State) && IsKnownState(catalog, country, located.State)
                ? located.State
                : string.Empty;
            note = HoldReason.CityWasCountryName(shared, city);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(located.State) && IsKnownState(catalog, country, located.State))
        {
            state = located.State;
            if (IsBetterCity(located.City, state, shared, catalog))
            {
                city = located.City!;
            }

            note = HoldReason.StateFilledFromAddress(state, shared);
            return true;
        }

        if (CityStates.TryGetState(country, shared, out var mappedState)
            && IsKnownState(catalog, country, mappedState))
        {
            state = mappedState;
            note = HoldReason.StateFilledFromCity(state, shared);
            return true;
        }

        if (ClosestStateName(catalog, country, shared) is { } corrected
            && IsKnownState(catalog, country, corrected))
        {
            state = corrected;
            if (IsBetterCity(located.City, state, shared, catalog))
            {
                city = located.City!;
            }

            note = HoldReason.StateFilledFromAddress(state, shared);
            return !city.Equals(state, StringComparison.OrdinalIgnoreCase)
                || IsKnownState(catalog, country, city);
        }

        if (IsBetterCity(located.City, state: null, shared, catalog))
        {
            city = located.City!;
        }
        else
        {
            city = shared;
        }

        state = string.Empty;
        note = HoldReason.ClearedDuplicatedState(shared, country);
        return HasText(city) && !LooksLikeStreet(city);
    }

    private static bool IsBetterCity(string? candidate, string? state, string shared, MappingCatalog catalog) =>
        !string.IsNullOrWhiteSpace(candidate)
        && !LooksLikeStreet(candidate)
        && ResolveCountry(candidate, catalog) is null
        && !candidate.Equals(shared, StringComparison.OrdinalIgnoreCase)
        && (state is null || !candidate.Equals(state, StringComparison.OrdinalIgnoreCase));

    private static string? ClosestStateName(MappingCatalog catalog, string country, string value)
    {
        var needle = value.Trim();
        if (needle.Length < 5)
        {
            return null;
        }

        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var known in catalog.KnownStates
            .Where(state => state.CountryCanonical.Equals(country, StringComparison.OrdinalIgnoreCase))
            .Select(state => state.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var distance = EditDistance(needle, known);
            var allowed = Math.Max(2, known.Length / 4);
            if (distance == 0 || distance > allowed || distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            best = known;
        }

        return best;
    }

    private static int EditDistance(string left, string right)
    {
        var a = left.ToUpperInvariant();
        var b = right.ToUpperInvariant();
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (var i = 0; i < a.Length; i++)
        {
            current[0] = i + 1;
            for (var j = 0; j < b.Length; j++)
            {
                var cost = a[i] == b[j] ? 0 : 1;
                current[j + 1] = Math.Min(
                    Math.Min(current[j] + 1, previous[j + 1] + 1),
                    previous[j] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static ClientRow WithCityState(ClientRow row, string city, string state)
    {
        var cells = new Dictionary<string, string>(row.Cells, StringComparer.OrdinalIgnoreCase)
        {
            ["AB"] = city,
            ["AC"] = state
        };
        return new ClientRow(row.RowNumber, cells);
    }

    private static string? ResolveCountry(string raw, MappingCatalog catalog)
    {
        if (raw.Length == 0)
        {
            return null;
        }

        var key = MappingSeed.NormalizeAlias(raw.Trim().TrimEnd('.', ',', ';'));
        return catalog.CountryAliases.TryGetValue(key, out var canonical) ? canonical : null;
    }

    // Country cells sometimes include the postal code ("CANADA J7C 5N5"). Split them apart.
    private static ClientRow SplitCountryAndPostal(
        ClientRow row,
        MappingCatalog catalog,
        Dictionary<int, string> notes)
    {
        var countryRaw = Value(row, "AD");
        if (countryRaw.Length == 0 || ResolveCountry(countryRaw, catalog) is not null)
        {
            return row;
        }

        if (!TrySplitCountryAndPostal(countryRaw, catalog, out var country, out var postal))
        {
            return row;
        }

        var cells = new Dictionary<string, string>(row.Cells, StringComparer.OrdinalIgnoreCase)
        {
            ["AD"] = country
        };
        var movedPostal = false;
        if (!string.IsNullOrWhiteSpace(postal) && !HasText(Value(row, "AE")))
        {
            cells["AE"] = postal;
            movedPostal = true;
        }

        var message = movedPostal
            ? HoldReason.PostalMovedFromCountry(postal!, ResolveCountry(country, catalog) ?? country)
            : $"Country read as {ResolveCountry(country, catalog) ?? country} from \"{countryRaw}\".";
        AppendLocationNote(notes, row.RowNumber, message);
        return new ClientRow(row.RowNumber, cells);
    }

    // Bad/blank country (ASIA, LAGOS, …): read city/state/country from the address, or from a
    // known city name that was typed into the country or city field.
    private static ClientRow RepairInvalidCountry(
        ClientRow row,
        MappingCatalog catalog,
        Dictionary<int, string> notes)
    {
        var countryRaw = Value(row, "AD");
        if (ResolveCountry(countryRaw, catalog) is not null)
        {
            return row;
        }

        var city = Value(row, "AB");
        var state = Value(row, "AC");
        var located = AddressLocator.Locate(LocationLines(row), catalog, null);
        if (!string.IsNullOrWhiteSpace(located.Country)
            && ResolveCountry(located.Country, catalog) is { } fromAddress)
        {
            return ApplyLocatedAddress(row, catalog, notes, located, fromAddress, countryRaw, preferAddressNote: true);
        }

        if (CityStates.TryResolvePlace(countryRaw, out var countryFromPlace, out var stateFromPlace))
        {
            var cells = new Dictionary<string, string>(row.Cells, StringComparer.OrdinalIgnoreCase)
            {
                ["AD"] = countryFromPlace,
                ["AB"] = HasText(city) && !city.Equals(countryRaw, StringComparison.OrdinalIgnoreCase)
                    && ResolveCountry(city, catalog) is null
                    && !LooksLikeStreet(city)
                        ? city
                        : countryRaw.Trim()
            };
            if (!HasText(state) && !string.IsNullOrWhiteSpace(stateFromPlace))
            {
                cells["AC"] = stateFromPlace;
            }

            AppendLocationNote(notes, row.RowNumber, HoldReason.CountryTakenFromPlaceName(countryFromPlace, countryRaw));
            return new ClientRow(row.RowNumber, cells);
        }

        if (ResolveCountry(city, catalog) is { } cityAsCountry)
        {
            var cells = new Dictionary<string, string>(row.Cells, StringComparer.OrdinalIgnoreCase)
            {
                ["AD"] = cityAsCountry
            };
            var withCountry = AddressLocator.Locate(LocationLines(row), catalog, cityAsCountry);
            if (!string.IsNullOrWhiteSpace(withCountry.City)
                && !LooksLikeStreet(withCountry.City)
                && ResolveCountry(withCountry.City, catalog) is null)
            {
                cells["AB"] = withCountry.City;
            }

            if (!HasText(state) && !string.IsNullOrWhiteSpace(withCountry.State))
            {
                cells["AC"] = withCountry.State;
            }

            if (!HasText(Value(row, "AE")) && !string.IsNullOrWhiteSpace(withCountry.PostalCode))
            {
                cells["AE"] = withCountry.PostalCode;
            }

            AppendLocationNote(notes, row.RowNumber, HoldReason.CountryTakenFromCity(cityAsCountry, countryRaw));
            return new ClientRow(row.RowNumber, cells);
        }

        if (CityStates.TryResolvePlace(city, out var countryFromCity, out var stateFromCity))
        {
            var cells = new Dictionary<string, string>(row.Cells, StringComparer.OrdinalIgnoreCase)
            {
                ["AD"] = countryFromCity
            };
            if (!HasText(state) && !string.IsNullOrWhiteSpace(stateFromCity))
            {
                cells["AC"] = stateFromCity;
            }

            AppendLocationNote(notes, row.RowNumber, HoldReason.CountryTakenFromCity(countryFromCity, countryRaw));
            return new ClientRow(row.RowNumber, cells);
        }

        return row;
    }

    private static ClientRow ApplyLocatedAddress(
        ClientRow row,
        MappingCatalog catalog,
        Dictionary<int, string> notes,
        LocatedAddress located,
        string country,
        string countryRaw,
        bool preferAddressNote)
    {
        var cells = new Dictionary<string, string>(row.Cells, StringComparer.OrdinalIgnoreCase)
        {
            ["AD"] = country
        };

        var city = Value(row, "AB");
        var state = Value(row, "AC");
        var nextCity = city;
        if (!string.IsNullOrWhiteSpace(located.City)
            && !LooksLikeStreet(located.City)
            && ResolveCountry(located.City, catalog) is null)
        {
            nextCity = located.City;
        }
        else if ((!HasText(city) || city.Equals(countryRaw, StringComparison.OrdinalIgnoreCase) || LooksLikeStreet(city))
            && CityStates.TryResolvePlace(countryRaw, out _, out _))
        {
            nextCity = countryRaw.Trim();
        }
        else if ((!HasText(city) || LooksLikeStreet(city) || ResolveCountry(city, catalog) is not null)
            && CityStates.TryResolvePlace(country, out _, out _)
            && country is "Singapore" or "Hong Kong")
        {
            nextCity = country;
        }

        if (HasText(nextCity))
        {
            cells["AB"] = nextCity;
        }

        var nextState = state;
        if (!string.IsNullOrWhiteSpace(located.State))
        {
            nextState = located.State;
        }
        else if (!HasText(state)
            && CityStates.TryGetState(country, nextCity, out var mappedState))
        {
            nextState = mappedState;
        }

        if (HasText(nextState))
        {
            cells["AC"] = nextState;
        }

        if (!HasText(Value(row, "AE")) && !string.IsNullOrWhiteSpace(located.PostalCode))
        {
            cells["AE"] = located.PostalCode;
        }

        AppendLocationNote(
            notes,
            row.RowNumber,
            preferAddressNote && HasText(nextCity)
                ? HoldReason.LocationRepairedFromAddress(nextCity, HasText(nextState) ? nextState : null, country)
                : HoldReason.CountryTakenFromAddress(country, countryRaw));
        return new ClientRow(row.RowNumber, cells);
    }

    private static ClientRow FillMissingStateFromCity(
        ClientRow row,
        MappingCatalog catalog,
        Dictionary<int, string> notes)
    {
        var country = ResolveCountry(Value(row, "AD"), catalog);
        var city = Value(row, "AB");
        var state = Value(row, "AC");
        if (country is null || !HasText(city) || HasText(state))
        {
            return row;
        }

        if (!CityStates.TryGetState(country, city, out var mappedState))
        {
            return row;
        }

        var cells = new Dictionary<string, string>(row.Cells, StringComparer.OrdinalIgnoreCase)
        {
            ["AC"] = mappedState
        };
        AppendLocationNote(notes, row.RowNumber, HoldReason.StateFilledFromCity(mappedState, city));
        return new ClientRow(row.RowNumber, cells);
    }

    private static void AppendLocationNote(Dictionary<int, string> notes, int rowNumber, string message)
    {
        notes[rowNumber] = notes.TryGetValue(rowNumber, out var existing)
            ? $"{existing} {message}"
            : message;
    }

    private static bool TrySplitCountryAndPostal(
        string raw,
        MappingCatalog catalog,
        out string country,
        out string? postal)
    {
        country = raw;
        postal = null;
        foreach (var pattern in TrailingPostalPatterns.Concat(LeadingPostalPatterns))
        {
            var match = pattern.Match(raw.Trim());
            if (!match.Success)
            {
                continue;
            }

            var countryPart = match.Groups["country"].Value.Trim().TrimEnd(',', '.', ';');
            if (ResolveCountry(countryPart, catalog) is null)
            {
                continue;
            }

            country = countryPart;
            postal = Regex.Replace(match.Groups["postal"].Value.Trim(), @"\s+", " ").ToUpperInvariant();
            return true;
        }

        return false;
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
        row = SplitCountryAndPostal(row, catalog, notes);
        row = RepairInvalidCountry(row, catalog, notes);
        row = ClearCountryNamedCityState(row, catalog, notes);
        row = FillMissingStateFromCity(row, catalog, notes);

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
        var located = AddressLocator.Locate(
            LocationLines(row),
            catalog,
            knownCountry);
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
            AppendLocationNote(notes, row.RowNumber, string.Join(" ", parts));
        }

        return row with { Cells = cells };
    }

    private static IEnumerable<string> LocationLines(ClientRow row) =>
        new[] { Value(row, "Y"), Value(row, "Z"), Value(row, "AA"), Value(row, "AB"), Value(row, "AC"), Value(row, "AD") }
            .Where(HasText);

    // When City (and often State) was typed as the country name, clear them so address fill can run.
    private static ClientRow ClearCountryNamedCityState(
        ClientRow row,
        MappingCatalog catalog,
        Dictionary<int, string> notes)
    {
        var country = ResolveCountry(Value(row, "AD"), catalog);
        var city = Value(row, "AB");
        if (country is null || !HasText(city))
        {
            return row;
        }

        if (ResolveCountry(city, catalog) is not { } cityAsCountry
            || !cityAsCountry.Equals(country, StringComparison.OrdinalIgnoreCase))
        {
            return row;
        }

        var cells = new Dictionary<string, string>(row.Cells, StringComparer.OrdinalIgnoreCase);
        cells.Remove("AB");
        var state = Value(row, "AC");
        if (!HasText(state)
            || state.Equals(city, StringComparison.OrdinalIgnoreCase)
            || (ResolveCountry(state, catalog) is { } stateAsCountry
                && stateAsCountry.Equals(country, StringComparison.OrdinalIgnoreCase)))
        {
            cells.Remove("AC");
        }

        var located = AddressLocator.Locate(
            [Value(row, "Y"), Value(row, "Z"), Value(row, "AA")],
            catalog,
            country);
        if (string.IsNullOrWhiteSpace(located.City)
            || LooksLikeStreet(located.City)
            || ResolveCountry(located.City, catalog) is not null)
        {
            // No real city in the address — leave the row as-is so conversion can Hold it.
            return row;
        }

        cells["AB"] = located.City;
        if ((!cells.TryGetValue("AC", out var nextState) || !HasText(nextState))
            && !string.IsNullOrWhiteSpace(located.State)
            && IsKnownState(catalog, country, located.State))
        {
            cells["AC"] = located.State;
        }

        if (!HasText(Value(row, "AE")) && !string.IsNullOrWhiteSpace(located.PostalCode))
        {
            cells["AE"] = located.PostalCode;
        }

        AppendLocationNote(notes, row.RowNumber, HoldReason.CityWasCountryName(city, located.City));
        return new ClientRow(row.RowNumber, cells);
    }

    // What the approver needs to decide, one fact per line.
    private static string AgentApprovalDetails(ClientRow row, string? note)
    {
        var lines = new List<string>
        {
            $"Type: {Value(row, "X")}",
            $"Address: {string.Join(", ", new[] { "Y", "Z", "AA", "AB", "AC", "AE", "AD" }.Select(column => Value(row, column)).Where(HasText))}"
        };
        AddLine(lines, "Contact email", Value(row, "AG"));
        AddLine(lines, "Telephone", Value(row, "AF"));
        AddLine(lines, "Network / event", Value(row, "L"));
        AddLine(lines, "WCA ID", Value(row, "BC"));
        AddLine(lines, "Agency agreement", Value(row, "M"));
        AddLine(lines, "Sales person", Value(row, "V"));
        AddLine(lines, "Requested by", string.Join(" ", new[] { Value(row, "D"), Value(row, "B") }.Where(HasText)));
        AddLine(lines, "Note", note ?? string.Empty);
        return string.Join("\n", lines);

        static void AddLine(List<string> lines, string label, string value)
        {
            if (HasText(value))
            {
                lines.Add($"{label}: {value}");
            }
        }
    }

    private static ClientRow UseGstinFromCertificate(
        ClientRow row,
        IReadOnlyDictionary<string, GstCertificate>? certificates,
        List<string> notes)
    {
        var link = Value(row, GstDocumentColumn);
        if (certificates is null || link.Length == 0 || !certificates.TryGetValue(link, out var certificate))
        {
            return row;
        }

        var typed = CleanRegistration(Value(row, GstNumberColumn));
        if (certificate.Gstin is null)
        {
            notes.Add(HoldReason.GstCertificateNotRead(certificate.Problem ?? "it could not be read", typed));
            return row;
        }

        if (typed == certificate.Gstin)
        {
            notes.Add(HoldReason.GstinMatchesCertificate(certificate.Gstin));
            return row;
        }

        notes.Add(typed.Length == 0
            ? HoldReason.GstinFromCertificate(certificate.Gstin)
            : HoldReason.GstinCorrectedFromCertificate(typed, certificate.Gstin));
        var cells = new Dictionary<string, string>(row.Cells, StringComparer.OrdinalIgnoreCase)
        {
            [GstNumberColumn] = certificate.Gstin
        };
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
