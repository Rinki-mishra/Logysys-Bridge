using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LogisysOrgConverter.Notifications;

public sealed class GoogleSheets
{
    public const string HttpClientName = "google";

    private const string SheetMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly HttpClient _http;
    private readonly GoogleBrowserSession _browser;
    private readonly GoogleOptions _options;
    private readonly string _tokenPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GoogleSheets(
        IHttpClientFactory httpClientFactory,
        IOptions<GoogleOptions> options,
        IWebHostEnvironment environment,
        GoogleBrowserSession browser)
    {
        _http = httpClientFactory.CreateClient(HttpClientName);
        _browser = browser;
        _options = options.Value;
        _tokenPath = Path.IsPathRooted(_options.TokenFile)
            ? _options.TokenFile
            : Path.Combine(environment.ContentRootPath, _options.TokenFile);
    }

    public bool IsConfigured => _options.IsConfigured;

    public string SpreadsheetId => _options.SpreadsheetId.Trim();

    public bool IsConnected => ReadToken() is { RefreshToken.Length: > 0 };

    public bool IsReady => IsConnected || _browser.HasSignedIn;

    public string RedirectUri(HttpRequest request) =>
        string.IsNullOrWhiteSpace(_options.RedirectUri)
            ? $"{request.Scheme}://{request.Host}/Google/Callback"
            : _options.RedirectUri.Trim();

    public string BuildAuthorizeUrl(string redirectUri, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId.Trim(),
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "https://www.googleapis.com/auth/drive.readonly https://www.googleapis.com/auth/forms.body.readonly",
            ["access_type"] = "offline",
            ["prompt"] = "consent select_account",
            ["state"] = state
        };
        if (!string.IsNullOrWhiteSpace(_options.LoginHint))
        {
            query["login_hint"] = _options.LoginHint.Trim();
        }

        return "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join(
            '&',
            query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    }

    public async Task ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            throw new FormSheetException("Google sign-in is not configured.");
        }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = _options.ClientId.Trim(),
            ["client_secret"] = _options.ClientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code"
        });
        using var response = await _http.PostAsync("https://oauth2.googleapis.com/token", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new FormSheetException("Google sign-in failed. " + FormMailbox.ApiMessage(body, response.ReasonPhrase));
        }

        var stored = ReadToken();
        using var document = JsonDocument.Parse(body);
        var refresh = document.RootElement.TryGetProperty("refresh_token", out var refreshToken)
            ? refreshToken.GetString()
            : null;
        refresh = string.IsNullOrWhiteSpace(refresh) ? stored?.RefreshToken : refresh;
        if (string.IsNullOrWhiteSpace(refresh))
        {
            throw new FormSheetException("Google did not return a refresh token. Remove this app under Google account permissions and connect again.");
        }

        Save(document.RootElement, refresh);
    }

    public async Task<byte[]> DownloadAsync(string kind, string sourceId, CancellationToken cancellationToken, string? openUrl = null)
    {
        if (!IsConnected)
        {
            return await _browser.DownloadAsync(kind, sourceId, cancellationToken, openUrl);
        }

        var sheetId = kind == FormSourceKinds.Form
            ? await LinkedSheetIdAsync(sourceId, cancellationToken)
            : sourceId;
        var url = "https://www.googleapis.com/drive/v3/files/"
            + Uri.EscapeDataString(sheetId)
            + "/export?mimeType="
            + Uri.EscapeDataString(SheetMime);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(cancellationToken));
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new FormSheetException("The response sheet could not be downloaded. " + FormMailbox.ApiMessage(body, response.ReasonPhrase));
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length < 4 || bytes[0] != 0x50 || bytes[1] != 0x4B)
        {
            throw new FormSheetException("Google did not return an Excel file for that response sheet.");
        }

        return bytes;
    }

    private async Task<string> LinkedSheetIdAsync(string formId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://forms.googleapis.com/v1/forms/" + Uri.EscapeDataString(formId));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(cancellationToken));
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new FormSheetException("The form's response sheet could not be opened. " + FormMailbox.ApiMessage(body, response.ReasonPhrase));
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("linkedSheetId", out var linked) || linked.GetString() is not { Length: > 0 } sheetId)
        {
            throw new FormSheetException("This form has no linked response spreadsheet.");
        }

        return sheetId;
    }

    private async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var stored = ReadToken() ?? throw new FormSheetException("Connect Google before reading the response sheet.");
            if (!string.IsNullOrWhiteSpace(stored.AccessToken) && stored.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return stored.AccessToken;
            }

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId.Trim(),
                ["client_secret"] = _options.ClientSecret,
                ["refresh_token"] = stored.RefreshToken,
                ["grant_type"] = "refresh_token"
            });
            using var response = await _http.PostAsync("https://oauth2.googleapis.com/token", content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new FormSheetException("Google sign-in expired. Connect again. " + FormMailbox.ApiMessage(body, response.ReasonPhrase));
            }

            using var document = JsonDocument.Parse(body);
            Save(document.RootElement, stored.RefreshToken);
            return document.RootElement.GetProperty("access_token").GetString()
                ?? throw new FormSheetException("Google did not return an access token.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Save(JsonElement token, string refreshToken)
    {
        var seconds = token.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 3600;
        var stored = new StoredGoogleToken
        {
            RefreshToken = refreshToken,
            AccessToken = token.GetProperty("access_token").GetString() ?? string.Empty,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds)
        };
        File.WriteAllText(_tokenPath, JsonSerializer.Serialize(stored, JsonOptions));
    }

    private StoredGoogleToken? ReadToken()
    {
        if (!File.Exists(_tokenPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredGoogleToken>(File.ReadAllText(_tokenPath), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class StoredGoogleToken
    {
        public string RefreshToken { get; set; } = string.Empty;

        public string AccessToken { get; set; } = string.Empty;

        public DateTimeOffset ExpiresAt { get; set; }
    }
}
