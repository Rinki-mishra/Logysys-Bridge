using System.Text.RegularExpressions;

namespace LogisysOrgConverter.Conversion;

public static class LedgerItemStates
{
    public const string Provided = "Provided";

    public const string Missing = "Missing";

    public const string Optional = "Optional";

    // Not asked in the client form, so someone has to collect or check it.
    public const string Collect = "Collect";

    public const string Approval = "Approval";
}

public sealed record LedgerItem(string Label, string State, string? Detail = null);

public sealed record LedgerSection(string Title, IReadOnlyList<LedgerItem> Items, IReadOnlyList<string> Notes);

public sealed record LedgerChecklist(string Category, IReadOnlyList<LedgerSection> Sections)
{
    public const string UnknownCountry = "Country unknown";

    public int MissingCount => Sections.Sum(section => section.Items.Count(item => item.State == LedgerItemStates.Missing));

    public int OpenCount => Sections.Sum(section => section.Items.Count(item => item.State is LedgerItemStates.Collect or LedgerItemStates.Approval));

    public string Summary => Sections.Count == 0
        ? Category == UnknownCountry
            ? "The country is not known, so the ledger rules cannot be chosen."
            : "No ledger rule for this organisation type."
        : MissingCount > 0
            ? $"{MissingCount} required {(MissingCount == 1 ? "document is" : "documents are")} missing."
            : OpenCount > 0
                ? "Documents in the form are complete; collect the rest and get the approvals."
                : "Ready for ledger link.";
}

// The "Document required during Ledger link - Logisys" checklist, applied to organisations
// the client wants invoiced (column I = Yes). Documents come from the KYC part of the form.
public static class LedgerRules
{
    private static readonly Regex Pan = new("^[A-Z]{5}[0-9]{4}[A-Z]$", RegexOptions.Compiled);

    private static readonly Regex Tan = new("^[A-Z]{4}[0-9]{5}[A-Z]$", RegexOptions.Compiled);

    private static readonly Regex Gstin = new("^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][0-9A-Z]Z[0-9A-Z]$", RegexOptions.Compiled);

    private const string AgentApproval = "Approval to create the organisation and mark the ledger: Shivangi (sea) / Aditya (air)";

    public static bool IsInvoiced(IReadOnlyDictionary<string, string> cells) =>
        cells.TryGetValue("I", out var value) && value.Trim().StartsWith("yes", StringComparison.OrdinalIgnoreCase);

    public static LedgerChecklist Build(IReadOnlyDictionary<string, string> cells, string? country)
    {
        var types = Types(Get(cells, "X"));
        var receivable = types.Contains("Shipper") || types.Contains("Consignee");
        var agent = types.Contains("Agent");
        var payable = types.Contains("Carrier") || types.Contains("Service");
        var sections = new List<LedgerSection>();

        if (string.IsNullOrWhiteSpace(country))
        {
            return new LedgerChecklist(LedgerChecklist.UnknownCountry, sections);
        }

        if (country == MappingCatalog.IndiaCountry)
        {
            sections.Add(IndiaMandatory(cells));
            return new LedgerChecklist("India", sections);
        }

        if (country == "Nepal")
        {
            sections.Add(new LedgerSection(
                "Nepal",
                [Kyc(cells), Number(cells, "PAN copy", "AM", "AN", required: true)],
                ["Mark the ledger as OVR RECEIVABLE."]));
            return new LedgerChecklist("Nepal", sections);
        }

        if (country == "United Arab Emirates")
        {
            if (agent)
            {
                sections.Add(OverseasAgent(cells));
            }

            if (receivable || payable)
            {
                var agreement = Has(cells, "M");
                sections.Add(new LedgerSection(
                    "Dubai – shipper, consignee, carrier, service",
                    [
                        Either(agreement, Number(cells, "VAT", "AO", null, required: true, alternative: "AV"), "agency agreement attached"),
                        Either(agreement, Number(cells, "Trade licence", "AM", null, required: true), "agency agreement attached"),
                        Document(cells, "Agency agreement", "M", required: false)
                    ],
                    ["VAT and trade licence, or the agency agreement."]));
            }

            return new LedgerChecklist("Dubai", sections);
        }

        if (receivable)
        {
            var items = new List<LedgerItem>
            {
                new("Approval for direct billing to the overseas customer, and credit approval", LedgerItemStates.Approval),
                new("Credit form and approval, if the customer is on credit", LedgerItemStates.Collect),
                new("Tax certificate and registration copy", LedgerItemStates.Collect,
                    Has(cells, "AO") || Has(cells, "AV") || Has(cells, "AT") ? "Tax number given in the form: " + FirstOf(cells, "AO", "AV", "AT") : null),
                new("Approval from JP sir along with the reporting manager", LedgerItemStates.Approval)
            };
            if (country == "United States of America")
            {
                items.Insert(3, new LedgerItem("W9 form", LedgerItemStates.Collect));
            }

            sections.Add(new LedgerSection("Overseas receivable – shipper, consignee", items, []));
        }

        if (agent)
        {
            sections.Add(OverseasAgent(cells));
        }

        if (payable)
        {
            var agreement = Has(cells, "M");
            sections.Add(new LedgerSection(
                "Overseas payable – carrier, service",
                [
                    new("Form 41 (was Form 10F), or agency agreement",
                        agreement ? LedgerItemStates.Provided : LedgerItemStates.Collect,
                        agreement ? "Agency agreement attached" : null),
                    new("TRC copy, or agency agreement",
                        agreement ? LedgerItemStates.Provided : LedgerItemStates.Collect,
                        agreement ? "Agency agreement attached" : null)
                ],
                []));
        }

        return new LedgerChecklist("Overseas", sections);
    }

    // For India only these three are mandatory, for every organisation type; each counts as
    // received only when its document is attached. Every other document is ignored.
    private static LedgerSection IndiaMandatory(IReadOnlyDictionary<string, string> cells) =>
        new(
            "India – PAN, TAN and GST",
            [
                MandatoryDocument(cells, "PAN", "AM", "AN", Pan),
                MandatoryDocument(cells, "TAN", "AU", "BF", Tan),
                MandatoryDocument(cells, "GST", "AO", "AP", Gstin)
            ],
            []);

    private static LedgerItem MandatoryDocument(
        IReadOnlyDictionary<string, string> cells,
        string label,
        string numberColumn,
        string documentColumn,
        Regex pattern)
    {
        var raw = Get(cells, numberColumn);
        var number = raw.Replace(" ", string.Empty).ToUpperInvariant();
        var numberOk = pattern.IsMatch(number);

        if (Has(cells, documentColumn))
        {
            return new LedgerItem($"{label} document", LedgerItemStates.Provided, numberOk ? number : "Attached");
        }

        return new LedgerItem($"{label} document", LedgerItemStates.Missing, numberOk
            ? $"{label} number {number} given, but the document is not attached"
            : "not attached");
    }

    private static LedgerSection OverseasAgent(IReadOnlyDictionary<string, string> cells) =>
        new(
            "Overseas agent",
            [
                new(AgentApproval, LedgerItemStates.Approval),
                Number(cells, "WCA ID", "BC", null, required: true),
                Document(cells, "Agency agreement", "M", required: true)
            ],
            []);

    private static LedgerItem Kyc(IReadOnlyDictionary<string, string> cells) =>
        Document(cells, "KYC in SAR format on the company letterhead", "AL", required: true);

    private static LedgerItem Document(IReadOnlyDictionary<string, string> cells, string label, string column, bool required) =>
        Has(cells, column)
            ? new LedgerItem(label, LedgerItemStates.Provided, "Attached")
            : new LedgerItem(label, required ? LedgerItemStates.Missing : LedgerItemStates.Optional);

    private static LedgerItem Number(
        IReadOnlyDictionary<string, string> cells,
        string label,
        string numberColumn,
        string? documentColumn,
        bool required,
        Regex? pattern = null,
        string? alternative = null)
    {
        var raw = Get(cells, numberColumn);
        if (raw.Length == 0 && alternative is not null)
        {
            raw = Get(cells, alternative);
        }

        var number = raw.Replace(" ", string.Empty).ToUpperInvariant();
        var numberOk = number.Any(char.IsDigit) && (pattern is null || pattern.IsMatch(number));
        var documentOk = documentColumn is not null && Has(cells, documentColumn);

        if (numberOk)
        {
            return new LedgerItem(label, LedgerItemStates.Provided,
                documentColumn is null || documentOk ? number : $"{number} (document not attached)");
        }

        if (documentOk)
        {
            return new LedgerItem(label, LedgerItemStates.Provided,
                raw.Length > 0 ? $"Document attached; number \"{raw}\" is not valid" : "Document attached; number not given");
        }

        var detail = raw.Length > 0 ? $"\"{raw}\" is not a valid number and no document is attached" : null;
        return new LedgerItem(label, required ? LedgerItemStates.Missing : LedgerItemStates.Optional, detail);
    }

    private static LedgerItem Either(bool alternativeOk, LedgerItem item, string alternativeText) =>
        item.State == LedgerItemStates.Missing && alternativeOk
            ? item with { State = LedgerItemStates.Provided, Detail = alternativeText }
            : item;

    private static HashSet<string> Types(string raw)
    {
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var text = token.ToLowerInvariant();
            if (text.Contains("shipping line") || text.Contains("airline") || text.Contains("carrier"))
            {
                types.Add("Carrier");
            }
            else if (text.Contains("transport") || text.Contains("commission"))
            {
                types.Add("Transport");
            }
            else if (text.Contains("shipper"))
            {
                types.Add("Shipper");
            }
            else if (text.Contains("consignee"))
            {
                types.Add("Consignee");
            }
            else if (text.Contains("agent"))
            {
                types.Add("Agent");
            }
            else if (text.Contains("service"))
            {
                types.Add("Service");
            }
        }

        return types;
    }

    private static string FirstOf(IReadOnlyDictionary<string, string> cells, params string[] columns) =>
        columns.Select(column => Get(cells, column)).FirstOrDefault(value => value.Length > 0) ?? string.Empty;

    private static bool Has(IReadOnlyDictionary<string, string> cells, string column) =>
        Get(cells, column).Any(char.IsLetterOrDigit);

    private static string Get(IReadOnlyDictionary<string, string> cells, string column) =>
        cells.TryGetValue(column, out var value) ? value.Trim() : string.Empty;
}
