using System.Text;
using LogisysOrgConverter.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LogisysOrgConverter.Logisys;

/// <summary>
/// Writes India/Overseas Excel files and a human-readable run status log to a local folder.
/// Does not change Softlink or conversion rules.
/// </summary>
public sealed class RunOutputWriter
{
    private readonly AppDbContext _db;
    private readonly AutomationOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<RunOutputWriter> _logger;

    public RunOutputWriter(
        AppDbContext db,
        IOptions<AutomationOptions> options,
        IWebHostEnvironment environment,
        ILogger<RunOutputWriter> logger)
    {
        _db = db;
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    public string RootFolder => Resolve(_options.OutputFolder);

    public async Task<string> WriteAfterConvertAsync(Guid runId, CancellationToken cancellationToken)
    {
        // Do not Include Rows — runs can have 10k+ rows and that reload blocked Softlink for minutes/GB.
        var run = await _db.Runs
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == runId, cancellationToken)
            ?? throw new InvalidOperationException($"Run {runId} was not found.");

        var folder = RunFolder(run);
        Directory.CreateDirectory(folder);

        var stamp = run.CreatedAtUtc.ToString("yyyyMMdd-HHmmss");
        if (run.IndiaFile is { Length: > 0 })
        {
            await File.WriteAllBytesAsync(
                Path.Combine(folder, $"OrgMaster-IN-{stamp}.xlsx"),
                run.IndiaFile,
                cancellationToken);
        }

        if (run.OverseasFile is { Length: > 0 })
        {
            await File.WriteAllBytesAsync(
                Path.Combine(folder, $"OrgMaster-Overseas-{stamp}.xlsx"),
                run.OverseasFile,
                cancellationToken);
        }

        if (run.UpdatesFile is { Length: > 0 })
        {
            await File.WriteAllBytesAsync(
                Path.Combine(folder, $"OrgMaster-AddressUpdates-{stamp}.xlsx"),
                run.UpdatesFile,
                cancellationToken);
        }

        // run-status.log is written only after Softlink pipeline finishes (or when Softlink is skipped),
        // so COD / Notes / G/L statuses are final — not the empty "after convert" snapshot.
        await File.WriteAllTextAsync(
            Path.Combine(folder, "README.txt"),
            $"Run folder for {run.SourceFileName}{Environment.NewLine}" +
            $"Run Id: {run.Id}{Environment.NewLine}" +
            $"India / Overseas Excel files are written after convert.{Environment.NewLine}" +
            $"run-status.log appears when Softlink upload / Finish / G/L is done (or skipped).{Environment.NewLine}",
            cancellationToken);

        _logger.LogInformation("Wrote convert Excel outputs for run {RunId} to {Folder}", runId, folder);
        return folder;
    }

    public async Task WriteAfterPipelineAsync(Guid runId, string? pipelineMessage, CancellationToken cancellationToken)
    {
        var run = await _db.Runs
            .Include(item => item.Rows)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == runId, cancellationToken);
        if (run is null)
        {
            return;
        }

        var folder = RunFolder(run);
        Directory.CreateDirectory(folder);
        var logPath = Path.Combine(folder, "run-status.log");
        await File.WriteAllTextAsync(
            logPath,
            BuildLog(run, stage: "After Softlink pipeline", pipelineMessage),
            cancellationToken);
        _logger.LogInformation("Updated run status log for {RunId} in {Folder}", runId, folder);
    }

    private string RunFolder(ConversionRun run)
    {
        var stamp = run.CreatedAtUtc.ToString("yyyyMMdd-HHmmss");
        var safe = Sanitize(Path.GetFileNameWithoutExtension(run.SourceFileName));
        return Path.Combine(RootFolder, $"{stamp}-{safe}-{run.Id.ToString("N")[..8]}");
    }

    private string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Path.GetFullPath(Path.Combine(_environment.ContentRootPath, "Runs"));
        }

        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, path));
    }

    private static string BuildLog(ConversionRun run, string stage, string? pipelineMessage = null)
    {
        var rows = run.Rows.OrderBy(row => row.SourceRowNumber).ToList();
        var india = rows.Where(row => row.Template == RowTemplates.India).ToList();
        var overseas = rows.Where(row => row.Template == RowTemplates.Overseas).ToList();
        var agents = rows.Where(row => row.Template == RowTemplates.OverseasAgent).ToList();
        var held = rows.Where(row => row.Status is RowStatuses.Held or "Held").ToList();
        var updates = rows.Where(row => row.Status == RowStatuses.Update).ToList();
        var merged = rows.Where(row => row.Status == RowStatuses.Merged).ToList();
        var created = rows.Where(row => row.ImportStatus == ImportStatuses.Created).ToList();
        var failedImport = rows.Where(row => row.ImportStatus == ImportStatuses.Failed).ToList();
        var readyEmail = created
            .Where(row => row.EmailedAtUtc is null && !string.IsNullOrWhiteSpace(row.RequesterEmail))
            .ToList();
        var approvals = agents.Where(row => row.Status == RowStatuses.Approval).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Logysys-Bridge run status");
        sb.AppendLine($"Stage: {stage}");
        sb.AppendLine($"Written: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local)");
        sb.AppendLine($"Run Id: {run.Id}");
        sb.AppendLine($"Source: {run.SourceFileName}");
        sb.AppendLine($"Converted UTC: {run.CreatedAtUtc:u}");
        sb.AppendLine();
        sb.AppendLine("=== COUNTS FROM SPREADSHEET / CONVERT ===");
        sb.AppendLine($"India organisations (ready to create): {run.IndiaCount}");
        sb.AppendLine($"Overseas organisations (ready to create): {run.OverseasCount}");
        sb.AppendLine($"Held: {run.HeldCount}");
        sb.AppendLine($"Address updates (already in Logi-Sys): {updates.Count}");
        sb.AppendLine($"Merged duplicates: {merged.Count}");
        sb.AppendLine($"Overseas agents awaiting approval: {approvals.Count}");
        sb.AppendLine();
        sb.AppendLine("=== SOFTLINK CREATE STATUS ===");
        sb.AppendLine($"Marked Created: {created.Count}");
        sb.AppendLine($"Marked Failed: {failedImport.Count}");
        sb.AppendLine($"Not yet marked: {rows.Count(row => row.Status == RowStatuses.Ready && row.ImportStatus is null)}");
        sb.AppendLine();
        sb.AppendLine("=== CASH ON DELIVERY ===");
        sb.AppendLine($"Done: {created.Count(row => row.CodMarkStatus == "Done")}");
        sb.AppendLine($"Skipped (credit/unlimited): {created.Count(row => row.CodMarkStatus == "Skipped")}");
        sb.AppendLine($"Failed: {created.Count(row => row.CodMarkStatus == "Failed")}");
        sb.AppendLine($"Pending: {created.Count(row => row.CodMarkStatus is not ("Done" or "Skipped" or "Failed"))}");
        sb.AppendLine();
        sb.AppendLine("=== NOTES ATTACHMENTS ===");
        var withDocs = created.Where(row => !string.IsNullOrWhiteSpace(row.DocumentsJson) && row.DocumentsJson != "[]").ToList();
        sb.AppendLine($"Created orgs with form documents: {withDocs.Count}");
        sb.AppendLine($"Attached: {withDocs.Count(row => row.NotesUploadStatus == "Attached")}");
        sb.AppendLine($"Failed: {withDocs.Count(row => row.NotesUploadStatus == "Failed")}");
        sb.AppendLine($"Pending: {withDocs.Count(row => row.NotesUploadStatus is not ("Attached" or "Failed"))}");
        sb.AppendLine();
        sb.AppendLine("=== INDIA LEDGER G/L ===");
        var indiaLedger = created.Where(row =>
            row.Template == RowTemplates.India && row.LedgerJson is not null).ToList();
        sb.AppendLine($"India invoiced (Created): {indiaLedger.Count}");
        sb.AppendLine($"G/L Done: {indiaLedger.Count(row => row.LedgerLinkStatus == "Done" || row.LedgerLinkedAtUtc is not null)}");
        sb.AppendLine($"G/L Failed: {indiaLedger.Count(row => row.LedgerLinkStatus == "Failed")}");
        sb.AppendLine($"G/L Pending: {indiaLedger.Count(row => row.LedgerLinkedAtUtc is null && row.LedgerLinkStatus is not ("Done" or "Skipped" or "Failed"))}");
        sb.AppendLine();
        sb.AppendLine("=== APPROVALS NEEDED (overseas agents) ===");
        if (approvals.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (var row in approvals)
            {
                sb.AppendLine($"- {row.OrganisationName} | {row.Country} | {row.Reason}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("=== READY TO SEND REQUESTOR EMAIL ===");
        sb.AppendLine("(Created, not yet emailed, has requester email)");
        if (readyEmail.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (var row in readyEmail)
            {
                sb.AppendLine($"- {row.OrganisationName} | {row.Country} | {row.RequesterEmail}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("=== INDIA DETAIL ===");
        AppendOrgLines(sb, india);
        sb.AppendLine();
        sb.AppendLine("=== OVERSEAS DETAIL ===");
        AppendOrgLines(sb, overseas);
        sb.AppendLine();
        sb.AppendLine("=== HELD ===");
        if (held.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (var row in held)
            {
                sb.AppendLine($"- Row {row.SourceRowNumber}: {row.OrganisationName} | {row.Reason}");
            }
        }

        if (!string.IsNullOrWhiteSpace(pipelineMessage))
        {
            sb.AppendLine();
            sb.AppendLine("=== PIPELINE MESSAGE ===");
            sb.AppendLine(pipelineMessage);
        }

        return sb.ToString();
    }

    private static void AppendOrgLines(StringBuilder sb, List<ConversionRunRow> rows)
    {
        if (rows.Count == 0)
        {
            sb.AppendLine("(none)");
            return;
        }

        foreach (var row in rows)
        {
            sb.AppendLine(
                $"- {row.OrganisationName} | {row.Country} | status={row.Status} | import={row.ImportStatus ?? "-"} | COD={row.CodMarkStatus ?? "-"} | Notes={row.NotesUploadStatus ?? "-"} | G/L={row.LedgerLinkStatus ?? (row.LedgerLinkedAtUtc is null ? "-" : "Done")} | emailed={(row.EmailedAtUtc is null ? "no" : "yes")}");
            if (!string.IsNullOrWhiteSpace(row.CodMarkMessage))
            {
                sb.AppendLine($"    COD note: {row.CodMarkMessage}");
            }

            if (!string.IsNullOrWhiteSpace(row.NotesUploadMessage))
            {
                sb.AppendLine($"    Notes note: {row.NotesUploadMessage}");
            }

            if (!string.IsNullOrWhiteSpace(row.LedgerLinkMessage))
            {
                sb.AppendLine($"    G/L note: {row.LedgerLinkMessage}");
            }
        }
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').Take(40).ToArray();
        var text = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(text) ? "run" : text;
    }
}
