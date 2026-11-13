using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace LogisysOrgConverter.Conversion;

public sealed record GstCertificate(string? Gstin, string? Problem)
{
    public static GstCertificate Read(string gstin) => new(gstin, null);

    public static GstCertificate Failed(string problem) => new(null, problem);
}

public static class GstinReader
{
    private const string Characters = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private static readonly Regex Candidate = new("[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]", RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static GstCertificate FromFile(byte[] content, string? mimeType, string? pan)
    {
        if (mimeType is not null && mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return GstCertificate.Failed("the certificate is an image (scan or photo), and only PDF certificates can be read");
        }

        string text;
        try
        {
            text = PdfText(content);
        }
        catch (Exception)
        {
            return GstCertificate.Failed("the certificate is not a readable PDF");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return GstCertificate.Failed("the certificate PDF is a scanned image with no text in it");
        }

        return Find(text, pan) is { } gstin
            ? GstCertificate.Read(gstin)
            : GstCertificate.Failed("no valid GSTIN was found in the certificate");
    }

    public static string PdfText(byte[] content)
    {
        using var document = PdfDocument.Open(content);
        return string.Join(
            Environment.NewLine,
            document.GetPages().Select(page => string.Join(' ', page.GetWords().Select(word => word.Text))));
    }

    // A GST registration certificate names the GSTIN as its "Registration Number"; other
    // GSTINs (e.g. of the issuing officer's notes) are only used when that label is absent.
    public static string? Find(string text, string? pan)
    {
        var upper = text.ToUpperInvariant();
        var candidates = Candidates(upper).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var label = upper.IndexOf("REGISTRATION NUMBER", StringComparison.Ordinal);
        if (label >= 0 && candidates.FirstOrDefault(item => item.Index > label) is { Value: not null } labelled)
        {
            return labelled.Value;
        }

        var cleanPan = pan?.Trim().Replace(" ", string.Empty).ToUpperInvariant();
        if (!string.IsNullOrEmpty(cleanPan) && candidates.FirstOrDefault(item => item.Value.Substring(2, 10) == cleanPan) is { Value: not null } withPan)
        {
            return withPan.Value;
        }

        return candidates[0].Value;
    }

    public static bool HasValidCheckCharacter(string gstin)
    {
        if (gstin.Length != 15 || !Candidate.IsMatch(gstin))
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 14; i++)
        {
            var product = Characters.IndexOf(gstin[i]) * (i % 2 == 0 ? 1 : 2);
            sum += product / 36 + product % 36;
        }

        return Characters[(36 - sum % 36) % 36] == gstin[14];
    }

    private static IEnumerable<(int Index, string Value)> Candidates(string upper)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Candidate.Matches(upper))
        {
            if (HasValidCheckCharacter(match.Value) && seen.Add(match.Value))
            {
                yield return (match.Index, match.Value);
            }
        }

        // Some PDFs space out every character of the number.
        if (seen.Count == 0)
        {
            var compact = Whitespace.Replace(upper, string.Empty);
            foreach (Match match in Candidate.Matches(compact))
            {
                if (HasValidCheckCharacter(match.Value) && seen.Add(match.Value))
                {
                    yield return (ApproximateIndex(upper, compact, match.Index), match.Value);
                }
            }
        }
    }

    private static int ApproximateIndex(string original, string compact, int compactIndex)
    {
        var seen = 0;
        for (var i = 0; i < original.Length; i++)
        {
            if (char.IsWhiteSpace(original[i]))
            {
                continue;
            }

            if (seen == compactIndex)
            {
                return i;
            }

            seen++;
        }

        return original.Length;
    }
}
