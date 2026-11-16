using LogisysOrgConverter.Data;

namespace LogisysOrgConverter.Logisys;

public sealed record SoftlinkPipelineSummary(
    string Message,
    OrgImportSummary? Import,
    ImportLogSummary? IndiaCreated,
    ImportLogSummary? OverseasCreated,
    SoftlinkFinishSummary? Finish,
    LedgerLinkSummary? Ledger);

/// <summary>
/// Phase A orchestrator: runs the same Softlink steps as the run-page buttons, in order.
/// Does not change upload / Created / COD / Notes / G/L logic — only sequences existing calls.
/// </summary>
public sealed class SoftlinkPipeline
{
    private readonly OrganisationImportUploader _import;
    private readonly NotificationStore _notifications;
    private readonly SoftlinkOrganisationFinisher _finish;
    private readonly OrganisationLedgerLinker _ledger;

    public SoftlinkPipeline(
        OrganisationImportUploader import,
        NotificationStore notifications,
        SoftlinkOrganisationFinisher finish,
        OrganisationLedgerLinker ledger)
    {
        _import = import;
        _notifications = notifications;
        _finish = finish;
        _ledger = ledger;
    }

    /// <summary>
    /// Same order as manual Softlink work today:
    /// Upload Organisations → Mark all created (India, then Overseas, no error log)
    /// → Finish (COD + Notes) → Link India G/L.
    /// Emails are not sent here (same as not clicking Send emails).
    /// </summary>
    public async Task<SoftlinkPipelineSummary> RunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var import = await _import.UploadAsync(runId, cancellationToken);

        // Same as run page "Mark all created" with no Softlink error log (logText null).
        ImportLogSummary? indiaCreated = null;
        ImportLogSummary? overseasCreated = null;
        if (import.IndiaUploaded)
        {
            indiaCreated = await _notifications.ApplyImportResultAsync(
                runId, RowTemplates.India, logText: null, cancellationToken);
        }

        if (import.OverseasUploaded)
        {
            overseasCreated = await _notifications.ApplyImportResultAsync(
                runId, RowTemplates.Overseas, logText: null, cancellationToken);
        }

        var finish = await _finish.FinishAsync(runId, onlyName: null, cancellationToken);
        var ledger = await _ledger.LinkAsync(runId, onlyName: null, cancellationToken);

        var parts = new List<string> { import.Message };
        if (indiaCreated is not null)
        {
            parts.Add($"India Created: {indiaCreated.Created} created, {indiaCreated.Failed} failed.");
        }

        if (overseasCreated is not null)
        {
            parts.Add($"Overseas Created: {overseasCreated.Created} created, {overseasCreated.Failed} failed.");
        }

        parts.Add(finish.Message);
        parts.Add(ledger.Message);

        return new SoftlinkPipelineSummary(
            string.Join(" ", parts),
            import,
            indiaCreated,
            overseasCreated,
            finish,
            ledger);
    }
}
