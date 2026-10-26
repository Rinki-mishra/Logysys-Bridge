namespace LogisysOrgConverter.Notifications;

public sealed class EmailOptions
{
    public const string Section = "Email";

    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = "Logi-Sys Organisation Master";

    public string Cc { get; set; } = string.Empty;

    public string Intro { get; set; } = "The following organisation you requested has been created in Logi-Sys.";

    public string IntroPlural { get; set; } = "The following organisations you requested have been created in Logi-Sys.";

    public string Closing { get; set; } = "You can now use it in Logi-Sys.";

    public string ClosingPlural { get; set; } = "You can now use them in Logi-Sys.";

    public string Signature { get; set; } = "Regards,\nOrganisation Master Team";

    // Overseas agents are created only after this person approves them.
    public string AgentApprover { get; set; } = "pankaj@tnpsfl.com";

    public string PickupFolder { get; set; } = "OutgoingMail";

    public SmtpOptions Smtp { get; set; } = new();

    public bool UsesSmtp => !string.IsNullOrWhiteSpace(Smtp.Host);
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public bool UseStartTls { get; set; } = true;

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}
