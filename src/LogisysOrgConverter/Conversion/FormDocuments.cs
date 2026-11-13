using System.Text.Json;
using System.Text.RegularExpressions;

namespace LogisysOrgConverter.Conversion;

public sealed record FormDocument(string Label, string Column, string Link);

// Drive uploads on the organisation creation form (any column that holds a Google Drive link).
public static class FormDocuments
{
    private static readonly Regex DriveLink = new(
        @"https?://(?:drive|docs)\.google\.com/\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["M"] = "Agency agreement",
        ["AL"] = "KYC",
        ["AN"] = "PAN",
        ["AP"] = "GST certificate",
        ["BF"] = "TAN",
        ["AQ"] = "IE code document",
        ["AV"] = "Tax registration",
        ["AT"] = "Tax certificate"
    };

    public static IReadOnlyList<FormDocument> Collect(IReadOnlyDictionary<string, string> cells)
    {
        var list = new List<FormDocument>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, raw) in cells)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            foreach (Match match in DriveLink.Matches(raw))
            {
                var link = match.Value.TrimEnd('.', ',', ')', ']');
                if (!seen.Add(link))
                {
                    continue;
                }

                var label = Labels.TryGetValue(column, out var named) ? named : $"Document {column}";
                list.Add(new FormDocument(label, column.ToUpperInvariant(), link));
            }
        }

        return list;
    }

    public static string? Write(IReadOnlyList<FormDocument> documents) =>
        documents.Count == 0 ? null : JsonSerializer.Serialize(documents);

    public static IReadOnlyList<FormDocument> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<FormDocument>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
