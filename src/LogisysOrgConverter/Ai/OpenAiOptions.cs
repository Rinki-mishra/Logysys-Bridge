namespace LogisysOrgConverter.Ai;

public sealed class OpenAiOptions
{
    public const string Section = "OpenAi";

    /// <summary>Keep this in appsettings.Local.json only - that file is gitignored.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "gpt-4o-mini";

    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>Off unless a key is present, so a missing key is never a pipeline failure.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cap per run so one bad batch cannot run up a bill.</summary>
    public int MaxRowsPerRun { get; set; } = 25;

    public int TimeoutSeconds { get; set; } = 45;

    /// <summary>Phase 4 will gate auto-apply on this. Phase 3 reports it and applies nothing.</summary>
    public double AutoApplyConfidence { get; set; } = 0.95;

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(ApiKey);
}
