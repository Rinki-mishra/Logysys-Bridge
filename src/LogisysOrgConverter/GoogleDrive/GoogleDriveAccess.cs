using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogisysOrgConverter.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LogisysOrgConverter.GoogleDrive;

public sealed class GoogleDriveException : Exception
{
    public GoogleDriveException(string message) : base(message)
    {
    }
}

public sealed record DriveFile(string Name, string MimeType, byte[] Content);

public sealed class GoogleDriveAccess
{
    public const string HttpClientName = "google";

    private const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    private const string UserInfoEndpoint = "https://openidconnect.googleapis.com/v1/userinfo";
    private const string DriveFilesEndpoint = "https://www.googleapis.com/drive/v3/files/";
    private const string Scopes = "openid email https://www.googleapis.com/auth/drive.readonly";
    private const long MaxFileBytes = 20 * 1024 * 1024;

    private static readonly Regex[] FileIdPatterns =
    [
        new(@"[?&]id=([A-Za-z0-9_-]{10,})", RegexOptions.Compiled),
        new(@"/d/([A-Za-z0-9_-]{10,})", RegexOptions.Compiled)
    ];

    private readonly GoogleOptions _options;
    private readonly IHttpClientFactory _httpClients;
    private readonly IDataProtector _protector;
    private readonly IServiceScopeFactory _scopes;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTime _accessTokenExpiresUtc;

    public GoogleDriveAccess(
        IOptions<GoogleOptions> options,
        IHttpClientFactory httpClients,
        IDataProtectionProvider dataProtection,
        IServiceScopeFactory scopes)
    {
        _options = options.Value;
        _httpClients = httpClients;
        _protector = dataProtection.CreateProtector("GoogleDrive.RefreshToken");
        _scopes = scopes;
    }

    public bool IsConfigured => _options.IsConfigured;

    public string LoginHint => _options.LoginHint;

    public string RedirectUri(HttpRequest request) =>
        string.IsNullOrWhiteSpace(_options.RedirectUri)
            ? $"{request.Scheme}://{request.Host}/Google/Callback"
            : _options.RedirectUri.Trim();

    public static string? FileId(string link)
    {
        foreach (var pattern in FileIdPatterns)
        {
            var match = pattern.Match(link);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    public static (string Verifier, string Challenge) CreatePkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public string AuthorizeUrl(string redirectUri, string state, string codeChallenge)
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = _options.ClientId.Trim(),
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = Scopes,
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };
        if (!string.IsNullOrWhiteSpace(_options.LoginHint))
        {
            query["login_hint"] = _options.LoginHint.Trim();
        }

        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(AuthorizeEndpoint, query);
    }

    public async Task<string> CompleteSignInAsync(string code, string redirectUri, string verifier, CancellationToken cancellationToken)
    {
        using var token = await PostTokenAsync(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = _options.ClientId.Trim(),
            ["client_secret"] = _options.ClientSecret.Trim(),
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = verifier
        }, cancellationToken);

        var root = token.RootElement;
        if (!root.TryGetProperty("refresh_token", out var refresh) || string.IsNullOrEmpty(refresh.GetString()))
        {
            throw new GoogleDriveException("Google did not return a long-lived sign-in. Remove this app's access at myaccount.google.com/permissions and connect again.");
        }

        var accessToken = root.GetProperty("access_token").GetString()!;
        var email = await GetEmailAsync(accessToken, cancellationToken);

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.GoogleConnections.RemoveRange(await db.GoogleConnections.ToListAsync(cancellationToken));
        db.GoogleConnections.Add(new GoogleConnection
        {
            Email = email,
            ProtectedRefreshToken = _protector.Protect(refresh.GetString()!),
            ConnectedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            _accessToken = accessToken;
            _accessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(ExpiresIn(root));
        }
        finally
        {
            _tokenLock.Release();
        }

        return email;
    }

    public async Task<GoogleConnection?> GetConnectionAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.GoogleConnections.AsNoTracking().OrderByDescending(item => item.ConnectedAtUtc).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken);
        if (connection is not null && TryUnprotect(connection.ProtectedRefreshToken) is { } refresh)
        {
            try
            {
                var client = _httpClients.CreateClient(HttpClientName);
                await client.PostAsync(RevokeEndpoint, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refresh }), cancellationToken);
            }
            catch (HttpRequestException)
            {
            }
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.GoogleConnections.RemoveRange(await db.GoogleConnections.ToListAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);

        await _tokenLock.WaitAsync(cancellationToken);
        _accessToken = null;
        _tokenLock.Release();
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new GoogleDriveException("Google sign-in is not set up yet; see the Google Drive page");
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is not null && DateTime.UtcNow < _accessTokenExpiresUtc.AddMinutes(-2))
            {
                return _accessToken;
            }

            var connection = await GetConnectionAsync(cancellationToken)
                ?? throw new GoogleDriveException("Google Drive is not connected; see the Google Drive page");
            var refresh = TryUnprotect(connection.ProtectedRefreshToken)
                ?? throw new GoogleDriveException("the saved Google sign-in can no longer be read; connect Google Drive again");

            using var token = await PostTokenAsync(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId.Trim(),
                ["client_secret"] = _options.ClientSecret.Trim(),
                ["refresh_token"] = refresh,
                ["grant_type"] = "refresh_token"
            }, cancellationToken);

            _accessToken = token.RootElement.GetProperty("access_token").GetString();
            _accessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(ExpiresIn(token.RootElement));
            return _accessToken!;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public async Task<DriveFile> DownloadAsync(string fileId, CancellationToken cancellationToken)
    {
        var accessToken = await GetAccessTokenAsync(cancellationToken);
        var client = _httpClients.CreateClient(HttpClientName);
        var escaped = Uri.EscapeDataString(fileId);

        using var metaRequest = new HttpRequestMessage(HttpMethod.Get, $"{DriveFilesEndpoint}{escaped}?fields=name,mimeType,size&supportsAllDrives=true");
        metaRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var metaResponse = await client.SendAsync(metaRequest, cancellationToken);
        EnsureDriveSuccess(metaResponse.StatusCode);
        using var meta = JsonDocument.Parse(await metaResponse.Content.ReadAsStringAsync(cancellationToken));
        var name = meta.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? fileId : fileId;
        var mimeType = meta.RootElement.TryGetProperty("mimeType", out var m) ? m.GetString() ?? string.Empty : string.Empty;
        if (meta.RootElement.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), out var size) && size > MaxFileBytes)
        {
            throw new GoogleDriveException("the certificate file is larger than 20 MB");
        }

        if (mimeType.StartsWith("application/vnd.google-apps", StringComparison.Ordinal))
        {
            throw new GoogleDriveException("the link points to a Google Docs file, not an uploaded certificate");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{DriveFilesEndpoint}{escaped}?alt=media&supportsAllDrives=true");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, cancellationToken);
        EnsureDriveSuccess(response.StatusCode);
        return new DriveFile(name, mimeType, await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    private static void EnsureDriveSuccess(HttpStatusCode status)
    {
        switch (status)
        {
            case HttpStatusCode.OK:
                return;
            case HttpStatusCode.NotFound:
                throw new GoogleDriveException("the file was not found, or the connected Google account cannot see it");
            case HttpStatusCode.Forbidden:
                throw new GoogleDriveException("the connected Google account has no access to the file");
            case HttpStatusCode.Unauthorized:
                throw new GoogleDriveException("Google rejected the sign-in; connect Google Drive again");
            default:
                throw new GoogleDriveException($"Google Drive answered {(int)status}");
        }
    }

    private async Task<JsonDocument> PostTokenAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        var client = _httpClients.CreateClient(HttpClientName);
        using var response = await client.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return JsonDocument.Parse(body);
        }

        var error = string.Empty;
        try
        {
            using var json = JsonDocument.Parse(body);
            error = json.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException)
        {
        }

        throw new GoogleDriveException(error switch
        {
            "invalid_grant" => "the Google sign-in has expired or was removed; connect Google Drive again",
            "invalid_client" or "unauthorized_client" => "Google rejected the client ID or secret in appsettings.Local.json",
            _ => $"Google sign-in failed ({(error.Length > 0 ? error : ((int)response.StatusCode).ToString())})"
        });
    }

    private async Task<string> GetEmailAsync(string accessToken, CancellationToken cancellationToken)
    {
        var client = _httpClients.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return "unknown account";
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.TryGetProperty("email", out var email) ? email.GetString() ?? "unknown account" : "unknown account";
    }

    private string? TryUnprotect(string value)
    {
        try
        {
            return _protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static int ExpiresIn(JsonElement root) =>
        root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds) ? seconds : 3600;

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
