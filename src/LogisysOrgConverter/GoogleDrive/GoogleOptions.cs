namespace LogisysOrgConverter.GoogleDrive;

public sealed class GoogleOptions
{
    public const string Section = "Google";

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    // Pre-fills the account on Google's sign-in screen.
    public string LoginHint { get; set; } = string.Empty;

    // Leave blank to use this app's own address + /Google/Callback.
    public string RedirectUri { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
