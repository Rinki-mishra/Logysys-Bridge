using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace LogisysOrgConverter.GoogleDrive;

public sealed class MicrosoftOptions
{
    public const string Section = "Microsoft";

    public string TenantId { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    // The Outlook mailbox Google sends the visitor code to.
    public string Mailbox { get; set; } = string.Empty;

    // Optional regex with one group for the code, if Google's email ever changes format.
    public string CodePattern { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(Mailbox);
}

// Reads the one-time code Google emails to the visitor mailbox, through Microsoft Graph
// with the app's own (application) permission Mail.Read.
public sealed class OutlookCodeReader
{
    public const string HttpClientName = "microsoft";

    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex LabelledCode = new(@"(?:code|PIN)\b[^0-9A-Z]{0,60}(?:G-)?([0-9]{4,8})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AnyCode = new(@"(?<![0-9])(?:G-)?([0-9]{6,8})(?![0-9])", RegexOptions.Compiled);

    private readonly MicrosoftOptions _options;
    private readonly IHttpClientFactory _httpClients;
    private readonly ILogger<OutlookCodeReader> _logger;
    private string? _token;
    private DateTime _tokenExpiresUtc;

    public OutlookCodeReader(IOptions<MicrosoftOptions> options, IHttpClientFactory httpClients, ILogger<OutlookCodeReader> logger)
    {
        _options = options.Value;
        _httpClients = httpClients;
        _logger = logger;
    }

    public bool IsConfigured => _options.IsConfigured;

    public string Mailbox => _options.Mailbox.Trim();

    public async Task CheckAccessAsync(CancellationToken cancellationToken)
    {
        await GetMessagesAsync(DateTime.UtcNow.AddDays(-1), 1, cancellationToken);
    }

    public async Task<string> WaitForCodeAsync(DateTime requestedUtc, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var since = requestedUtc.AddSeconds(-30);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            foreach (var message in await GetMessagesAsync(since, 10, cancellationToken))
            {
                if (ExtractCode(message.From, message.Subject, message.Text) is { } code)
                {
                    _logger.LogInformation("Google visitor code found in {Mailbox} (email received {Received:u}).", Mailbox, message.ReceivedUtc);
                    return code;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new GoogleDriveException($"no code from Google arrived in {Mailbox} within {timeout.TotalMinutes:0} minutes (check the Junk folder rules)");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    public string? ExtractCode(string from, string subject, string text)
    {
        var fromGoogle = from.EndsWith("google.com", StringComparison.OrdinalIgnoreCase);
        var looksLikeCode = Regex.IsMatch(subject + " " + text, @"code|PIN|verif", RegexOptions.IgnoreCase);
        if (!fromGoogle || !looksLikeCode)
        {
            return null;
        }

        var all = subject + "\n" + text;
        if (!string.IsNullOrWhiteSpace(_options.CodePattern))
        {
            var custom = Regex.Match(all, _options.CodePattern);
            return custom.Success ? custom.Groups[custom.Groups.Count > 1 ? 1 : 0].Value : null;
        }

        var labelled = LabelledCode.Match(all);
        if (labelled.Success)
        {
            return labelled.Groups[1].Value;
        }

        var any = AnyCode.Match(all);
        return any.Success ? any.Groups[1].Value : null;
    }

    private async Task<List<(string From, string Subject, string Text, DateTime ReceivedUtc)>> GetMessagesAsync(
        DateTime sinceUtc,
        int top,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new GoogleDriveException("the Microsoft section of appsettings.Local.json is not filled in");
        }

        var token = await GetTokenAsync(cancellationToken);
        var client = _httpClients.CreateClient(HttpClientName);
        var url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(Mailbox)}/messages"
            + $"?$filter=receivedDateTime ge {sinceUtc:yyyy-MM-ddTHH:mm:ssZ}"
            + "&$orderby=receivedDateTime desc"
            + $"&$top={top}"
            + "&$select=subject,from,receivedDateTime,body";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new GoogleDriveException($"Microsoft 365 refused to read {Mailbox}; the app needs the Mail.Read application permission with admin consent");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new GoogleDriveException($"Microsoft 365 has no mailbox {Mailbox}");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new GoogleDriveException($"Microsoft 365 answered {(int)response.StatusCode} when reading {Mailbox}");
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var messages = new List<(string, string, string, DateTime)>();
        foreach (var item in json.RootElement.GetProperty("value").EnumerateArray())
        {
            var from = item.TryGetProperty("from", out var f) && f.TryGetProperty("emailAddress", out var address)
                ? address.GetProperty("address").GetString() ?? string.Empty
                : string.Empty;
            var subject = item.TryGetProperty("subject", out var s) ? s.GetString() ?? string.Empty : string.Empty;
            var body = item.TryGetProperty("body", out var b) ? b.GetProperty("content").GetString() ?? string.Empty : string.Empty;
            var received = item.TryGetProperty("receivedDateTime", out var r) ? r.GetDateTime().ToUniversalTime() : DateTime.UtcNow;
            messages.Add((from, subject, WebUtility.HtmlDecode(Tags.Replace(body, " ")), received));
        }

        return messages;
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is not null && DateTime.UtcNow < _tokenExpiresUtc.AddMinutes(-2))
        {
            return _token;
        }

        var client = _httpClients.CreateClient(HttpClientName);
        using var response = await client.PostAsync(
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(_options.TenantId.Trim())}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId.Trim(),
                ["client_secret"] = _options.ClientSecret.Trim(),
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials"
            }),
            cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!response.IsSuccessStatusCode || !json.RootElement.TryGetProperty("access_token", out var token))
        {
            var error = json.RootElement.TryGetProperty("error", out var e) ? e.GetString() : ((int)response.StatusCode).ToString();
            throw new GoogleDriveException($"Microsoft sign-in for the app failed ({error}); check TenantId, ClientId and ClientSecret");
        }

        _token = token.GetString();
        _tokenExpiresUtc = DateTime.UtcNow.AddSeconds(json.RootElement.TryGetProperty("expires_in", out var x) && x.TryGetInt32(out var seconds) ? seconds : 3600);
        return _token!;
    }
}
