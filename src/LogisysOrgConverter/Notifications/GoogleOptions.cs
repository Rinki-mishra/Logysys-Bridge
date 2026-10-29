namespace LogisysOrgConverter.Notifications;

public sealed class GoogleOptions
{
    public const string Section = "Google";

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string LoginHint { get; set; } = string.Empty;

    public string RedirectUri { get; set; } = string.Empty;

    public string SpreadsheetId { get; set; } = string.Empty;

    public string TokenFile { get; set; } = "google-token.json";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
