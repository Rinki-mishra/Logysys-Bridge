using System.Net;
using System.Text.RegularExpressions;

namespace LogisysOrgConverter.Notifications;

public static class FormSourceKinds
{
    public const string Sheet = "sheet";

    public const string Form = "form";
}

public sealed record FormNotice(
    string Kind,
    string SourceId,
    string Title,
    DateTime ReceivedAtUtc,
    string? RequestorEmail,
    string? OpenLink = null)
{
    public string FileName
    {
        get
        {
            var safe = new string(Title.Select(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '&' ? ch : ' ').ToArray());
            safe = string.Join(' ', safe.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (safe.Length > 80)
            {
                safe = safe[..80].Trim();
            }

            return (safe.Length == 0 ? "form-responses" : safe) + ".xlsx";
        }
    }

    public const string FormResponsesGid = "1513774273";

    public string OpenUrl
    {
        get
        {
            if (Kind == FormSourceKinds.Form)
            {
                return !string.IsNullOrWhiteSpace(OpenLink)
                    ? OpenLink
                    : "https://docs.google.com/forms/d/" + SourceId + "/edit#responses";
            }

            var link = !string.IsNullOrWhiteSpace(OpenLink)
                ? OpenLink
                : "https://docs.google.com/spreadsheets/d/" + SourceId + "/edit?usp=sharing";
            return Title.Contains("Creation Request Form", StringComparison.OrdinalIgnoreCase)
                ? FormNoticeParser.WithFormResponsesTab(link)
                : link;
        }
    }
}

public sealed class FormSheetException : Exception
{
    public FormSheetException(string message) : base(message)
    {
    }
}

public static partial class FormNoticeParser
{
    public static bool TryReadSubject(string? subject, out string kind, out string title)
    {
        kind = string.Empty;
        title = string.Empty;
        if (string.IsNullOrWhiteSpace(subject))
        {
            return false;
        }

        var edited = EditedSubject().Match(subject);
        if (edited.Success)
        {
            kind = FormSourceKinds.Sheet;
            title = edited.Groups["title"].Value.Trim();
            return title.Length > 0;
        }

        var responses = NewResponsesSubject().Match(subject);
        if (responses.Success)
        {
            kind = FormSourceKinds.Form;
            title = responses.Groups["title"].Value.Trim();
            return title.Length > 0;
        }

        return false;
    }

    public static FormNotice? Parse(string? subject, string? bodyPreview, string? html, DateTime receivedAtUtc)
    {
        if (!TryReadSubject(subject, out var kind, out var title))
        {
            return null;
        }

        var decoded = WebUtility.HtmlDecode(html ?? string.Empty);
        var sourceId = kind == FormSourceKinds.Sheet
            ? First(SpreadsheetId(), decoded)
            : First(FormId(), decoded);
        if (sourceId is null)
        {
            return null;
        }

        var requestor = Requestor().Match(bodyPreview ?? string.Empty);
        if (!requestor.Success)
        {
            requestor = Requestor().Match(WebUtility.HtmlDecode(html ?? string.Empty));
        }

        return new FormNotice(
            kind,
            sourceId,
            title,
            receivedAtUtc,
            requestor.Success ? requestor.Groups["email"].Value : null);
    }

    public static string? SpreadsheetUrlFrom(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var decoded = WebUtility.HtmlDecode(html).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);
        string? first = null;
        foreach (Match match in SpreadsheetUrl().Matches(decoded))
        {
            var url = match.Value.TrimEnd('"', '\'', '>');
            first ??= url;
            if (url.Contains("/edit", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
        }

        return first;
    }

    public static string? SpreadsheetIdFrom(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var decoded = WebUtility.HtmlDecode(html).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);
        return First(SpreadsheetId(), decoded);
    }

    public static string SheetTitleFrom(string? subject)
    {
        var match = QuotedTitle().Match(subject ?? string.Empty);
        return match.Success ? match.Groups["title"].Value.Trim() : "Organisation Creation Request Form (Responses)";
    }

    public static string WithFormResponsesTab(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        var hash = url.IndexOf('#');
        var withoutHash = hash >= 0 ? url[..hash] : url;
        var query = withoutHash.IndexOf('?');
        var path = query >= 0 ? withoutHash[..query] : withoutHash;
        var queryText = query >= 0 ? withoutHash[(query + 1)..] : string.Empty;
        var parts = queryText
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.StartsWith("gid=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        parts.Add("gid=" + FormNotice.FormResponsesGid);
        return path + "?" + string.Join('&', parts) + "#gid=" + FormNotice.FormResponsesGid;
    }

    private static string? First(Regex pattern, string text)
    {
        var match = pattern.Match(text);
        return match.Success ? match.Groups["id"].Value : null;
    }

    [GeneratedRegex("\"(?<title>[^\"]+)\" was edited recently", RegexOptions.IgnoreCase)]
    private static partial Regex EditedSubject();

    [GeneratedRegex(@"Your form, (?<title>.+), has new responses", RegexOptions.IgnoreCase)]
    private static partial Regex NewResponsesSubject();

    [GeneratedRegex(@"spreadsheets/d/(?<id>[a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SpreadsheetId();

    [GeneratedRegex(@"https://docs\.google\.com/spreadsheets/d/[a-zA-Z0-9_-]+[^\s""'<>]*", RegexOptions.IgnoreCase)]
    private static partial Regex SpreadsheetUrl();

    [GeneratedRegex("\"(?<title>[^\"]+)\"")]
    private static partial Regex QuotedTitle();

    [GeneratedRegex(@"forms/d/(?<id>[a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FormId();

    [GeneratedRegex(@"View Response:\s*(?<email>[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,})", RegexOptions.IgnoreCase)]
    private static partial Regex Requestor();
}
