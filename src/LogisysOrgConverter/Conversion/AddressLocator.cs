using System.Text.RegularExpressions;

namespace LogisysOrgConverter.Conversion;

public sealed record LocatedAddress(string? City, string? State, string? Country, string? PostalCode);

// Finds city, state, country and postal code inside free-text address lines, for rows where the
// client left those columns blank or typed "." in them. It only answers when the text is clear;
// anything doubtful is left blank so the row is held rather than guessed.
public static class AddressLocator
{
    private static readonly Regex IndianPin = new(@"(?<![0-9])([1-9][0-9]{2})\s?([0-9]{3})(?![0-9])", RegexOptions.Compiled);

    private static readonly Regex UsZip = new(@"\b([A-Z]{2})\s+([0-9]{5})(?:-[0-9]{4})?\b", RegexOptions.Compiled);

    private static readonly Regex LeadingPostal = new(@"(?:^|\s)([0-9]{4,6})$", RegexOptions.Compiled);

    private static readonly string[] NoisePrefixes =
        ["VAT", "TEL", "PHONE", "FAX", "MOB", "KIND ATTN", "ATTN", "GST", "PAN", "EMAIL", "E-MAIL", "CONTACT"];

    private static readonly HashSet<string> NotCityWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ROAD", "RD", "STREET", "ST", "AVENUE", "AVE", "LANE", "LN", "DRIVE", "DR", "BOULEVARD", "BLVD",
        "HIGHWAY", "HWY", "FLOOR", "SUITE", "STE", "PLOT", "NEAR", "OPP", "BUILDING", "BLDG", "COMPLEX",
        "SOCIETY", "ESTATE", "AREA", "SECTOR", "UNIT", "SHOP", "BLOCK", "NO", "PVT", "LTD", "LIMITED",
        "DIVISION", "VIA", "VIALE", "BULEVAR", "BUL", "PO", "BOX"
    };

    // First two digits of an Indian PIN code that belong to exactly one state.
    private static readonly Dictionary<string, string> PinStates = new()
    {
        ["11"] = "Delhi", ["12"] = "Haryana", ["13"] = "Haryana", ["14"] = "Punjab", ["15"] = "Punjab",
        ["16"] = "Chandigarh", ["17"] = "Himachal Pradesh", ["30"] = "Rajasthan", ["31"] = "Rajasthan",
        ["32"] = "Rajasthan", ["33"] = "Rajasthan", ["34"] = "Rajasthan", ["36"] = "Gujarat", ["37"] = "Gujarat",
        ["38"] = "Gujarat", ["39"] = "Gujarat", ["40"] = "Maharashtra", ["41"] = "Maharashtra", ["42"] = "Maharashtra",
        ["43"] = "Maharashtra", ["44"] = "Maharashtra", ["45"] = "Madhya Pradesh", ["46"] = "Madhya Pradesh",
        ["47"] = "Madhya Pradesh", ["48"] = "Madhya Pradesh", ["49"] = "Chhattisgarh", ["50"] = "Telangana",
        ["56"] = "Karnataka", ["57"] = "Karnataka", ["58"] = "Karnataka", ["59"] = "Karnataka", ["60"] = "Tamil Nadu",
        ["61"] = "Tamil Nadu", ["62"] = "Tamil Nadu", ["63"] = "Tamil Nadu", ["64"] = "Tamil Nadu", ["67"] = "Kerala",
        ["68"] = "Kerala", ["69"] = "Kerala", ["70"] = "West Bengal", ["71"] = "West Bengal", ["72"] = "West Bengal",
        ["73"] = "West Bengal", ["74"] = "West Bengal", ["75"] = "Odisha", ["76"] = "Odisha", ["77"] = "Odisha",
        ["78"] = "Assam", ["80"] = "Bihar", ["81"] = "Bihar", ["82"] = "Jharkhand", ["83"] = "Jharkhand",
        ["84"] = "Bihar", ["85"] = "Bihar"
    };

    public static LocatedAddress Locate(IEnumerable<string> lines, MappingCatalog catalog, string? knownCountry = null)
    {
        var segments = lines
            .SelectMany(line => line.Split([',', ';', '\n']))
            .Select(Clean)
            .Where(segment => segment.Any(char.IsLetterOrDigit) && !IsNoise(segment))
            .ToList();
        if (segments.Count == 0)
        {
            return new LocatedAddress(null, null, null, null);
        }

        var stateNames = catalog.KnownStates.Select(state => state.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var country = FindLast(segments, catalog.CountryAliases
            .Where(alias => alias.Key.Length >= 3 && !stateNames.Contains(alias.Key))
            .Where(alias => knownCountry is null || alias.Value.Equals(knownCountry, StringComparison.OrdinalIgnoreCase))
            .Select(alias => (Text: alias.Key, Value: alias.Value)));

        var countryName = knownCountry ?? country?.Value;
        var states = catalog.KnownStates
            .Where(state => countryName is null
                ? state.CountryCanonical == MappingCatalog.IndiaCountry
                : state.CountryCanonical.Equals(countryName, StringComparison.OrdinalIgnoreCase))
            .Where(state => state.Name.Length >= 4)
            .Select(state => (Text: state.Name, Value: state.Name))
            .ToList();
        var state = FindLast(segments, states);

        (int Segment, string Text, string Value)? zip = null;
        for (var index = segments.Count - 1; index >= 0 && zip is null; index--)
        {
            foreach (Match match in UsZip.Matches(segments[index].ToUpperInvariant()))
            {
                var abbreviation = match.Groups[1].Value;
                var usState = catalog.KnownStates.FirstOrDefault(known =>
                    known.CountryCanonical == "United States of America"
                    && string.Equals(known.Abbreviation, abbreviation, StringComparison.OrdinalIgnoreCase));
                if (usState is not null && (countryName is null || countryName == usState.CountryCanonical))
                {
                    zip = (index, match.Value, match.Groups[2].Value);
                    countryName ??= usState.CountryCanonical;
                    state ??= (index, abbreviation, usState.Name);
                }
            }
        }

        (int Segment, string Text, string Value)? pin = null;
        if (zip is null && (countryName is null || countryName == MappingCatalog.IndiaCountry))
        {
            for (var index = segments.Count - 1; index >= 0 && pin is null; index--)
            {
                var match = IndianPin.Match(segments[index]);
                if (match.Success)
                {
                    pin = (index, match.Value, match.Groups[1].Value + match.Groups[2].Value);
                }
            }
        }

        if (countryName is null && (state is not null || pin is not null))
        {
            countryName = MappingCatalog.IndiaCountry;
        }

        if (state is null && pin is not null && PinStates.TryGetValue(pin.Value.Value[..2], out var pinState))
        {
            state = (pin.Value.Segment, string.Empty, pinState);
        }

        var anchor = new[] { country?.Segment, state is { Text.Length: > 0 } ? state.Value.Segment : null, zip?.Segment, pin?.Segment }
            .Where(index => index is not null)
            .Select(index => index!.Value)
            .DefaultIfEmpty(knownCountry is null ? -1 : segments.Count - 1)
            .Max();

        string? city = null;
        string? postal = zip?.Value ?? pin?.Value;
        var remove = new[] { country?.Text, state?.Text, zip?.Text, pin?.Text }
            .Where(text => !string.IsNullOrEmpty(text))
            .Select(text => text!)
            .ToList();
        for (var index = anchor; index >= 0 && index >= anchor - 3; index--)
        {
            var candidate = segments[index];
            foreach (var text in remove)
            {
                candidate = RemoveWord(candidate, text);
            }

            candidate = Clean(candidate);
            var lastNumber = Regex.Match(candidate, @"^(.*[0-9][^ ]*)\s+(.+)$");
            if (lastNumber.Success)
            {
                var before = LeadingPostal.Match(lastNumber.Groups[1].Value);
                if (before.Success && postal is null)
                {
                    postal = before.Groups[1].Value;
                }

                candidate = Clean(lastNumber.Groups[2].Value);
            }
            else if (candidate.Any(char.IsDigit))
            {
                candidate = string.Empty;
            }

            if (!candidate.Any(char.IsLetter))
            {
                continue;
            }

            var words = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= 3 && !words.Any(word => NotCityWords.Contains(word.Trim('.', '(', ')'))))
            {
                city = candidate;
            }

            break;
        }

        return new LocatedAddress(city, state?.Value, countryName, postal);
    }

    private static (int Segment, string Text, string Value)? FindLast(
        List<string> segments,
        IEnumerable<(string Text, string Value)> options)
    {
        var list = options.OrderByDescending(option => option.Text.Length).ToList();
        for (var index = segments.Count - 1; index >= 0; index--)
        {
            var upper = Collapse(segments[index]).ToUpperInvariant();
            var matches = list
                .Select(option => (option, At: WordIndex(upper, option.Text.ToUpperInvariant())))
                .Where(item => item.At >= 0)
                .OrderByDescending(item => item.At + item.option.Text.Length)
                .ThenByDescending(item => item.option.Text.Length)
                .ToList();
            if (matches.Count > 0)
            {
                return (index, matches[0].option.Text, matches[0].option.Value);
            }
        }

        return null;
    }

    private static int WordIndex(string text, string word)
    {
        var start = 0;
        while (true)
        {
            var at = text.IndexOf(word, start, StringComparison.Ordinal);
            if (at < 0)
            {
                return -1;
            }

            var end = at + word.Length;
            var leftOk = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            var rightOk = end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (leftOk && rightOk)
            {
                var later = WordIndex(text[end..], word);
                return later >= 0 ? end + later : at;
            }

            start = at + 1;
        }
    }

    private static string RemoveWord(string text, string word)
    {
        var collapsed = Collapse(text);
        var at = WordIndex(collapsed.ToUpperInvariant(), Collapse(word).ToUpperInvariant());
        return at < 0 ? collapsed : collapsed.Remove(at, Collapse(word).Length).Insert(at, " ");
    }

    private static bool IsNoise(string segment)
    {
        if (segment.Contains('@'))
        {
            return true;
        }

        var upper = segment.ToUpperInvariant();
        return NoisePrefixes.Any(prefix => upper.StartsWith(prefix, StringComparison.Ordinal)
            && (upper.Length == prefix.Length || !char.IsLetter(upper[prefix.Length])));
    }

    private static string Collapse(string text) =>
        string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string Clean(string text) =>
        Collapse(text.Trim().Trim('.', ',', '-', '–', ':', ';', '(', ')', '"', '\'', '„', ' ').Trim());
}
