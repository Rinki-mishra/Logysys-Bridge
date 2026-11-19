namespace LogisysOrgConverter.Logisys;

public sealed class LogisysOptions
{
    public const string Section = "Logisys";

    // Softlink tenant, e.g. https://awgmen.mylogisys.com
    public string BaseUrl { get; set; } = "https://awgmen.mylogisys.com";

    // Softlink User Id / Password (keep in appsettings.Local.json only). Security Code is still typed in Edge.
    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    // "msedge" uses the installed Edge browser (recommended).
    public string BrowserChannel { get; set; } = "msedge";

    // Signed-in runs start Edge with --headless=new (no window).
    // The Security Code still opens a visible Edge. False keeps the maximized window.
    public bool Headless { get; set; }

    // Softlink login needs a security code; a visible Edge stays open so you can sign in.
    public int LoginWaitMinutes { get; set; } = 5;

    // Softlink Add Note attachment limit shown in the dialog.
    public int MaxAttachmentBytes { get; set; } = 2 * 1024 * 1024;
}
