namespace LogisysOrgConverter.Notifications;

public sealed class GoogleVisitorOptions
{
    public const string Section = "GoogleVisitor";

    public string BrowserChannel { get; set; } = "msedge";

    public bool ShowBrowser { get; set; } = true;

    public int CodeWaitMinutes { get; set; } = 3;
}
