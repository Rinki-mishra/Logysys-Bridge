namespace LogisysOrgConverter.Logisys;

public sealed class AutomationOptions
{
    public const string Section = "Automation";

    // Master switch for background Softlink automation triggers.
    public bool Enabled { get; set; }

    // Poll the mailbox Spreadsheet folder (same as “Load spreadsheet from mail”).
    public bool PollMailbox { get; set; } = true;

    // Minutes between mailbox polls when PollMailbox is true.
    public int MailPollMinutes { get; set; } = 5;

    // Optional: also watch a local inbox folder for .xlsx drops (off by default).
    public bool WatchInboxFolder { get; set; }

    // Relative to content root, or an absolute path.
    public string InboxFolder { get; set; } = "App_Data/Inbox";

    public string ProcessedFolder { get; set; } = "App_Data/Processed";

    public string FailedFolder { get; set; } = "App_Data/Failed";

    // India/Overseas Excel copies + run-status.log (absolute path recommended).
    public string OutputFolder { get; set; } = @"C:\Logisys-bridge inbox";
}
