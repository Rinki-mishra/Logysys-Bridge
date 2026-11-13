using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace LogisysOrgConverter.Notifications;

public sealed record MailboxListing(IReadOnlyList<FormNotice> Notices, string? Error);

public sealed partial class FormMailbox
{
    public const string HttpClientName = "graph";

    private static readonly string[] Searches = ["\"has new responses\"", "\"was edited recently\""];

    private readonly HttpClient _http;
    private readonly MailboxOptions _options;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpires;
    private string? _spreadsheetFolderId;

    public FormMailbox(IHttpClientFactory httpClientFactory, IOptions<MailboxOptions> options)
    {
        _http = httpClientFactory.CreateClient(HttpClientName);
        _options = options.Value;
    }

    public string Address => _options.Mailbox.Trim();

    public bool IsConfigured => _options.IsConfigured;

    public async Task<MailboxListing> ListAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            return new MailboxListing([], "Mailbox reading is not configured.");
        }

        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var grouped = new Dictionary<string, List<MailMessage>>(StringComparer.OrdinalIgnoreCase);
            foreach (var search in Searches)
            {
                foreach (var message in await SearchAsync(search, cancellationToken))
                {
                    if (!seen.Add(message.Id) || !FormNoticeParser.TryReadSubject(message.Subject, out var kind, out var title))
                    {
                        continue;
                    }

                    if (!title.Contains("Organization Creation", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var key = kind + "\n" + title;
                    if (!grouped.TryGetValue(key, out var list))
                    {
                        list = [];
                        grouped[key] = list;
                    }

                    list.Add(message);
                }
            }

            var notices = new List<FormNotice>();
            foreach (var group in grouped.Values)
            {
                foreach (var message in group.OrderByDescending(item => item.ReceivedAtUtc).Take(3))
                {
                    var html = await ReadBodyAsync(message.Id, cancellationToken);
                    var notice = FormNoticeParser.Parse(message.Subject, message.BodyPreview, html, message.ReceivedAtUtc);
                    if (notice is null)
                    {
                        continue;
                    }

                    notices.Add(notice);
                    break;
                }
            }

            return new MailboxListing(
                notices.OrderByDescending(item => item.ReceivedAtUtc).ToList(),
                null);
        }
        catch (FormSheetException ex)
        {
            return new MailboxListing([], ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new MailboxListing([], "The mailbox did not answer in time.");
        }
    }

    public static string? ReadVisitorCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var plain = Regex.Replace(WebUtility.HtmlDecode(text), "<[^>]+>", " ");
        var match = VisitorCode().Match(plain);
        return match.Success ? match.Groups[1].Value : null;
    }

    public async Task<string?> RecentSignInCodeAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            return null;
        }

        try
        {
            foreach (var message in await ListRecentAsync(cancellationToken))
            {
                if (message.ReceivedAtUtc < since.UtcDateTime.AddSeconds(-15))
                {
                    continue;
                }

                if (message.Subject?.Contains("Google Verification Code", StringComparison.OrdinalIgnoreCase) != true)
                {
                    continue;
                }

                if (message.From?.Contains("google.com", StringComparison.OrdinalIgnoreCase) == false)
                {
                    continue;
                }

                var code = ReadVisitorCode(message.BodyPreview);
                if (code is null)
                {
                    code = ReadVisitorCode(await ReadBodyAsync(message.Id, cancellationToken));
                }

                if (code is not null)
                {
                    return code;
                }
            }
        }
        catch (FormSheetException)
        {
            return null;
        }

        return null;
    }

    public async Task<FormNotice> FindSharedSheetAsync(CancellationToken cancellationToken)
    {
        var folderId = await FindFolderIdAsync("Spreadsheet", cancellationToken)
            ?? throw new FormSheetException("The Spreadsheet folder was not found in the mailbox.");
        var url = string.Concat(
            "https://graph.microsoft.com/v1.0/users/",
            Uri.EscapeDataString(_options.Mailbox.Trim()),
            "/mailFolders/",
            Uri.EscapeDataString(folderId),
            "/messages?$top=40&$orderby=receivedDateTime desc&$select=id,subject,receivedDateTime,body");
        using var document = await SendAsync(HttpMethod.Get, url, null, cancellationToken);
        if (!document.RootElement.TryGetProperty("value", out var value))
        {
            throw new FormSheetException("The Spreadsheet folder has no mail.");
        }

        FormNotice? exact = null;
        FormNotice? named = null;
        FormNotice? fallback = null;
        foreach (var item in value.EnumerateArray())
        {
            var subject = item.TryGetProperty("subject", out var subjectNode) ? subjectNode.GetString() : null;
            var html = string.Empty;
            if (item.TryGetProperty("body", out var body) && body.TryGetProperty("content", out var content))
            {
                html = content.GetString() ?? string.Empty;
            }

            var sourceId = FormNoticeParser.SpreadsheetIdFrom(html);
            if (sourceId is null)
            {
                continue;
            }

            var received = item.TryGetProperty("receivedDateTime", out var when)
                ? when.GetDateTimeOffset().UtcDateTime
                : DateTime.UtcNow;
            var title = FormNoticeParser.SheetTitleFrom(subject);
            var openUrl = FormNoticeParser.SpreadsheetUrlFrom(html);
            if (string.IsNullOrWhiteSpace(openUrl) || !openUrl.Contains("/edit", StringComparison.OrdinalIgnoreCase))
            {
                openUrl = "https://docs.google.com/spreadsheets/d/" + sourceId + "/edit?usp=sharing";
            }

            if (IsOrganisationResponses(title) || IsOrganisationResponses(subject))
            {
                openUrl = FormNoticeParser.WithFormResponsesTab(openUrl);
            }

            var notice = new FormNotice(FormSourceKinds.Sheet, sourceId, title, received, null, openUrl);
            if (IsOrganisationResponses(title) || IsOrganisationResponses(subject))
            {
                exact ??= notice;
            }
            else if (subject?.Contains("Creation Request Form", StringComparison.OrdinalIgnoreCase) == true
                && subject.Contains("Responses", StringComparison.OrdinalIgnoreCase))
            {
                named ??= notice;
            }
            else
            {
                fallback ??= notice;
            }
        }

        return exact ?? named ?? fallback ?? throw new FormSheetException("The Spreadsheet folder has no link to the response sheet.");
    }

    private static bool IsOrganisationResponses(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Contains("Creation Request Form", StringComparison.OrdinalIgnoreCase)
        && text.Contains("Responses", StringComparison.OrdinalIgnoreCase)
        && (text.Contains("Organis", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Organiz", StringComparison.OrdinalIgnoreCase));

    private async Task<string?> FindFolderIdAsync(string name, CancellationToken cancellationToken)
    {
        if (string.Equals(name, "Spreadsheet", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_spreadsheetFolderId))
        {
            return _spreadsheetFolderId;
        }

        // Prefer a direct name filter so Create/Load does not walk the whole mailbox tree.
        var filtered = string.Concat(
            "https://graph.microsoft.com/v1.0/users/",
            Uri.EscapeDataString(_options.Mailbox.Trim()),
            "/mailFolders?$filter=",
            Uri.EscapeDataString($"displayName eq '{name.Replace("'", "''", StringComparison.Ordinal)}'"),
            "&$top=10");
        try
        {
            using var matched = await SendAsync(HttpMethod.Get, filtered, null, cancellationToken);
            if (matched.RootElement.TryGetProperty("value", out var hits))
            {
                foreach (var folder in hits.EnumerateArray())
                {
                    var id = folder.GetProperty("id").GetString();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        if (string.Equals(name, "Spreadsheet", StringComparison.OrdinalIgnoreCase))
                        {
                            _spreadsheetFolderId = id;
                        }

                        return id;
                    }
                }
            }
        }
        catch (FormSheetException)
        {
            // Some tenants reject $filter on mailFolders; fall back to a shallow walk.
        }

        var url = string.Concat(
            "https://graph.microsoft.com/v1.0/users/",
            Uri.EscapeDataString(_options.Mailbox.Trim()),
            "/mailFolders?$top=50");
        return await FindFolderIdAsync(url, name, cancellationToken);
    }

    private async Task<string?> FindFolderIdAsync(string url, string name, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, url, null, cancellationToken);
        if (!document.RootElement.TryGetProperty("value", out var value))
        {
            return null;
        }

        foreach (var folder in value.EnumerateArray())
        {
            var displayName = folder.TryGetProperty("displayName", out var display) ? display.GetString() : null;
            var id = folder.GetProperty("id").GetString();
            if (string.Equals(displayName, name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(id))
            {
                if (string.Equals(name, "Spreadsheet", StringComparison.OrdinalIgnoreCase))
                {
                    _spreadsheetFolderId = id;
                }

                return id;
            }

            var children = folder.TryGetProperty("childFolderCount", out var count) ? count.GetInt32() : 0;
            if (children > 0 && !string.IsNullOrWhiteSpace(id))
            {
                var childUrl = string.Concat(
                    "https://graph.microsoft.com/v1.0/users/",
                    Uri.EscapeDataString(_options.Mailbox.Trim()),
                    "/mailFolders/",
                    Uri.EscapeDataString(id),
                    "/childFolders?$top=50");
                var child = await FindFolderIdAsync(childUrl, name, cancellationToken);
                if (child is not null)
                {
                    return child;
                }
            }
        }

        if (document.RootElement.TryGetProperty("@odata.nextLink", out var next) && next.GetString() is { Length: > 0 } nextUrl)
        {
            return await FindFolderIdAsync(nextUrl, name, cancellationToken);
        }

        return null;
    }

    private async Task<IReadOnlyList<MailMessage>> ListRecentAsync(CancellationToken cancellationToken)
    {
        var url = string.Concat(
            "https://graph.microsoft.com/v1.0/users/",
            Uri.EscapeDataString(_options.Mailbox.Trim()),
            "/messages?$top=15&$orderby=receivedDateTime desc&$select=id,subject,from,receivedDateTime,bodyPreview");
        return await ReadMessagesAsync(url, cancellationToken);
    }

    private async Task<IReadOnlyList<MailMessage>> SearchAsync(string search, CancellationToken cancellationToken)
    {
        var url = string.Concat(
            "https://graph.microsoft.com/v1.0/users/",
            Uri.EscapeDataString(_options.Mailbox.Trim()),
            "/messages?$search=",
            Uri.EscapeDataString(search),
            "&$top=25&$select=id,subject,from,receivedDateTime,bodyPreview");
        return await ReadMessagesAsync(url, cancellationToken);
    }

    private async Task<IReadOnlyList<MailMessage>> ReadMessagesAsync(string url, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, url, null, cancellationToken);
        if (!document.RootElement.TryGetProperty("value", out var value))
        {
            return [];
        }

        var messages = new List<MailMessage>();
        foreach (var item in value.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var received = item.TryGetProperty("receivedDateTime", out var when)
                ? when.GetDateTimeOffset().UtcDateTime
                : DateTime.UtcNow;
            string? from = null;
            if (item.TryGetProperty("from", out var fromNode)
                && fromNode.TryGetProperty("emailAddress", out var email)
                && email.TryGetProperty("address", out var address))
            {
                from = address.GetString();
            }

            messages.Add(new MailMessage(
                id,
                item.TryGetProperty("subject", out var subject) ? subject.GetString() : null,
                item.TryGetProperty("bodyPreview", out var preview) ? preview.GetString() : null,
                received,
                from));
        }

        return messages;
    }

    private async Task<string> ReadBodyAsync(string messageId, CancellationToken cancellationToken)
    {
        var url = string.Concat(
            "https://graph.microsoft.com/v1.0/users/",
            Uri.EscapeDataString(_options.Mailbox.Trim()),
            "/messages/",
            Uri.EscapeDataString(messageId),
            "?$select=body");
        using var document = await SendAsync(HttpMethod.Get, url, null, cancellationToken);
        if (!document.RootElement.TryGetProperty("body", out var body)
            || !body.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        return content.GetString() ?? string.Empty;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken cancellationToken)
    {
        var token = await AccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new FormSheetException("The mailbox could not be read. " + ApiMessage(body, response.ReasonPhrase));
        }

        return JsonDocument.Parse(body);
    }

    private async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is not null && _tokenExpires > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return _token;
        }

        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && _tokenExpires > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return _token;
            }

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId.Trim(),
                ["client_secret"] = _options.ClientSecret,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials"
            });
            using var response = await _http.PostAsync(
                "https://login.microsoftonline.com/" + Uri.EscapeDataString(_options.TenantId.Trim()) + "/oauth2/v2.0/token",
                content,
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new FormSheetException("Microsoft sign-in for the mailbox failed. " + ApiMessage(body, response.ReasonPhrase));
            }

            using var document = JsonDocument.Parse(body);
            _token = document.RootElement.GetProperty("access_token").GetString()
                ?? throw new FormSheetException("Microsoft did not return a mailbox token.");
            var seconds = document.RootElement.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 3600;
            _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(seconds);
            return _token;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    internal static string ApiMessage(string body, string? reason)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object)
                {
                    if (error.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } text)
                    {
                        return text;
                    }

                    if (error.TryGetProperty("error_description", out var description) && description.GetString() is { Length: > 0 } detail)
                    {
                        return FirstLine(detail);
                    }
                }

                if (error.ValueKind == JsonValueKind.String && error.GetString() is { Length: > 0 } code)
                {
                    if (root.TryGetProperty("error_description", out var description) && description.GetString() is { Length: > 0 } detail)
                    {
                        return FirstLine(detail);
                    }

                    return code;
                }
            }
        }
        catch (JsonException)
        {
        }

        return string.IsNullOrWhiteSpace(reason) ? "The request failed." : reason;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 280 ? line[..280] : line;
    }

    [GeneratedRegex(@"belongs to you\.?\s*(\d{6})", RegexOptions.IgnoreCase)]
    private static partial Regex VisitorCode();

    private sealed record MailMessage(string Id, string? Subject, string? BodyPreview, DateTime ReceivedAtUtc, string? From);
}
