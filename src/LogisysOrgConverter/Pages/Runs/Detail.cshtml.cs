using LogisysOrgConverter.Data;
using LogisysOrgConverter.GoogleDrive;
using LogisysOrgConverter.Logisys;
using LogisysOrgConverter.Notifications;
using FormSheetException = LogisysOrgConverter.Notifications.FormSheetException;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LogisysOrgConverter.Pages.Runs;

public class DetailModel : PageModel
{
    private readonly ConversionStore _store;
    private readonly NotificationStore _notifications;
    private readonly MailDelivery _delivery;
    private readonly OrganisationNoteUploader _notes;
    private readonly OrganisationCodMarker _cod;
    private readonly OrganisationLedgerLinker _ledgerLink;
    private readonly OrganisationImportUploader _import;
    private readonly SoftlinkOrganisationFinisher _finish;
    private readonly LogisysBrowserSession _softlink;

    public DetailModel(
        ConversionStore store,
        NotificationStore notifications,
        MailDelivery delivery,
        OrganisationNoteUploader notes,
        OrganisationCodMarker cod,
        OrganisationLedgerLinker ledgerLink,
        OrganisationImportUploader import,
        SoftlinkOrganisationFinisher finish,
        LogisysBrowserSession softlink)
    {
        _store = store;
        _notifications = notifications;
        _delivery = delivery;
        _notes = notes;
        _cod = cod;
        _ledgerLink = ledgerLink;
        _import = import;
        _finish = finish;
        _softlink = softlink;
    }

    public string SoftlinkUser => _softlink.UserName;

    public ConversionRun? Run { get; private set; }

    public IReadOnlyList<EmailDraft> Drafts { get; private set; } = [];

    public IReadOnlyList<EmailNotification> Sent { get; private set; } = [];

    public IReadOnlyList<EmailDraft> LedgerDrafts { get; private set; } = [];

    public IReadOnlyDictionary<int, string> PendingElsewhere { get; private set; } = new Dictionary<int, string>();

    public EmailDraft? ApprovalDraft { get; private set; }

    public string AgentApprover => _notifications.AgentApprover;

    public bool UsesSmtp => _delivery.UsesSmtp;

    public string PickupFolder => _delivery.PickupFolder;

    [TempData]
    public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        Run = await _store.GetRunAsync(id, cancellationToken);
        if (Run is null)
        {
            return NotFound();
        }

        Drafts = await _notifications.GetDraftsAsync(id, cancellationToken);
        Sent = await _notifications.GetNotificationsAsync(id, cancellationToken);
        LedgerDrafts = await _notifications.GetLedgerDraftsAsync(id, cancellationToken);
        ApprovalDraft = await _notifications.GetAgentApprovalDraftAsync(id, cancellationToken);
        PendingElsewhere = await _store.GetPendingElsewhereAsync(id, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostImportResultAsync(Guid id, string template, IFormFile? log, bool markAll, CancellationToken cancellationToken)
    {
        if (template is not (RowTemplates.India or RowTemplates.Overseas or RowTemplates.OverseasAgent))
        {
            return BadRequest();
        }

        if (!markAll && (log is null || log.Length == 0))
        {
            Message = $"Choose the Logi-Sys error log for the {template} file, or click Mark all created if the import had no errors.";
            return RedirectToPage(new { id });
        }

        string? text = null;
        if (!markAll && log is not null && log.Length > 0)
        {
            using var reader = new StreamReader(log.OpenReadStream());
            text = await reader.ReadToEndAsync(cancellationToken);
        }

        var summary = await _notifications.ApplyImportResultAsync(id, template, text, cancellationToken);
        Message = $"{template} file: {summary.Created} created, {summary.Failed} failed.";
        if (summary.UnmatchedNames.Count > 0)
        {
            Message += $" Not found in this file: {string.Join(", ", summary.UnmatchedNames)}.";
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostMarkAsync(Guid id, int rowId, string status, CancellationToken cancellationToken)
    {
        var changed = await _notifications.SetImportStatusAsync(id, rowId, status, cancellationToken);
        Message = changed ? "Saved." : "That row could not be changed.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSendAsync(Guid id, CancellationToken cancellationToken)
    {
        var summary = await _notifications.SendAsync(id, cancellationToken);
        var verb = summary.Delivery == "SMTP" ? "sent" : "saved to the outgoing mail folder";
        Message = summary.Failed == 0
            ? $"{summary.Sent} email(s) {verb}."
            : $"{summary.Sent} email(s) {verb}. {summary.Failed} failed; see the email history below.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSendLedgerAsync(Guid id, CancellationToken cancellationToken)
    {
        var summary = await _notifications.SendLedgerRequestsAsync(id, cancellationToken);
        var verb = summary.Delivery == "SMTP" ? "sent" : "saved to the outgoing mail folder";
        Message = summary.Failed == 0
            ? $"{summary.Sent} document request email(s) {verb}."
            : $"{summary.Sent} document request email(s) {verb}. {summary.Failed} failed; see the email history below.";
        return RedirectToPage(null, null, new { id }, "ledger");
    }

    public async Task<IActionResult> OnPostSendApprovalAsync(Guid id, CancellationToken cancellationToken)
    {
        var summary = await _notifications.SendAgentApprovalAsync(id, cancellationToken);
        var verb = summary.Delivery == "SMTP" ? $"sent to {AgentApprover}" : "saved to the outgoing mail folder";
        Message = summary.Failed == 0
            ? summary.Sent == 0 ? "No agents are waiting to be sent for approval." : $"Approval request {verb}."
            : "The approval request could not be sent; see the email history below.";
        return RedirectToPage(null, null, new { id }, "agents");
    }

    public async Task<IActionResult> OnPostAgentAsync(Guid id, int rowId, bool approved, CancellationToken cancellationToken)
    {
        var changed = await _store.DecideAgentAsync(id, rowId, approved, cancellationToken);
        Message = changed
            ? approved ? "Marked approved. The agent is in the approved agents file." : "Marked not approved. The agent will not be created."
            : "That agent could not be changed (it may already be imported).";
        return RedirectToPage(null, null, new { id }, "agents");
    }

    public async Task<IActionResult> OnPostLedgerAsync(Guid id, int rowId, bool linked, CancellationToken cancellationToken)
    {
        var changed = await _store.SetLedgerLinkedAsync(id, rowId, linked, cancellationToken);
        Message = changed ? (linked ? "Marked as ledger linked." : "Ledger link cleared.") : "That row could not be changed.";
        return RedirectToPage(null, null, new { id }, "ledger");
    }

    // Softlink India only: Customer G/L → TPY Receivable / Vendor G/L → TPY Payable.
    [RequestTimeout(900_000)]
    public async Task<IActionResult> OnPostLinkIndiaLedgerAsync(Guid id, string? orgName, CancellationToken cancellationToken)
    {
        try
        {
            Message = (await _ledgerLink.LinkAsync(id, orgName, cancellationToken)).Message;
        }
        catch (LogisysException ex)
        {
            Message = ex.Message;
        }
        catch (Microsoft.Playwright.PlaywrightException ex)
        {
            Message = ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase)
                ? "The Softlink Edge window was closed before India G/L link finished. Sign in again, leave Edge open, then retry."
                : "Softlink browser error: " + ex.Message.Split('\n')[0];
        }

        return RedirectToPage(null, null, new { id }, "ledger");
    }

    public Task<IActionResult> OnPostSoftlinkSignInAsync(Guid id, string? returnTo, CancellationToken cancellationToken)
    {
        try
        {
            Message = _softlink.OpenSignInWindow();
        }
        catch (LogisysException ex)
        {
            Message = ex.Message;
        }

        var fragment = returnTo switch
        {
            "softlink-import" => "softlink-import",
            "notes" => "notes",
            "ledger" => "ledger",
            _ => "softlink-finish"
        };
        return Task.FromResult<IActionResult>(RedirectToPage(null, null, new { id }, fragment));
    }

    // Softlink Organizations → ⋮ → Upload Organizations (India, then overseas).
    [RequestTimeout(900_000)]
    public async Task<IActionResult> OnPostUploadOrganisationsAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            Message = (await _import.UploadAsync(id, cancellationToken)).Message;
        }
        catch (LogisysException ex)
        {
            Message = ex.Message;
        }
        catch (Microsoft.Playwright.PlaywrightException ex)
        {
            Message = ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase)
                ? "The Softlink Edge window was closed before Upload Organizations finished. Sign in again, leave Edge open, then upload again."
                : "Softlink browser error: " + ex.Message.Split('\n')[0];
        }

        return RedirectToPage(null, null, new { id }, "softlink-import");
    }

    // Softlink session + one note per Drive file can take several minutes.
    [RequestTimeout(900_000)]
    public async Task<IActionResult> OnPostAttachNotesAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var summary = await _notes.AttachAsync(id, cancellationToken);
            Message = summary.Message;
        }
        catch (LogisysException ex)
        {
            Message = ex.Message;
        }
        catch (GoogleDriveException ex)
        {
            Message = ex.Message;
        }
        catch (FormSheetException ex)
        {
            Message = ex.Message;
        }
        catch (Microsoft.Playwright.PlaywrightException ex)
        {
            Message = ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase)
                ? "The Softlink Edge window was closed before Attach finished. Sign in again, close Edge, then Attach."
                : "Softlink browser error: " + ex.Message.Split('\n')[0];
        }

        return RedirectToPage(null, null, new { id }, "notes");
    }

    [RequestTimeout(900_000)]
    public async Task<IActionResult> OnPostMarkCashOnDeliveryAsync(Guid id, string? orgName, CancellationToken cancellationToken)
    {
        try
        {
            Message = (await _cod.MarkAsync(id, orgName, cancellationToken)).Message;
        }
        catch (LogisysException ex)
        {
            Message = ex.Message;
        }
        catch (Microsoft.Playwright.PlaywrightException ex)
        {
            Message = ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase)
                ? "The Softlink Edge window was closed before Cash On Delivery finished. Sign in again, leave Edge open, then retry."
                : "Softlink browser error: " + ex.Message.Split('\n')[0];
        }

        return RedirectToPage(null, null, new { id }, "softlink-finish");
    }

    // COD + Notes for Created orgs only (after Softlink Excel import).
    [RequestTimeout(900_000)]
    public async Task<IActionResult> OnPostFinishSoftlinkAsync(Guid id, string? orgName, CancellationToken cancellationToken)
    {
        try
        {
            Message = (await _finish.FinishAsync(id, orgName, cancellationToken)).Message;
        }
        catch (LogisysException ex)
        {
            Message = ex.Message;
        }
        catch (GoogleDriveException ex)
        {
            Message = ex.Message;
        }
        catch (FormSheetException ex)
        {
            Message = ex.Message;
        }
        catch (Microsoft.Playwright.PlaywrightException ex)
        {
            Message = ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase)
                ? "The Softlink Edge window was closed before Finish completed. Sign in again, leave Edge open, then Finish again."
                : "Softlink browser error: " + ex.Message.Split('\n')[0];
        }

        return RedirectToPage(null, null, new { id }, "softlink-finish");
    }

    public async Task<IActionResult> OnGetLedgerFileAsync(Guid id, CancellationToken cancellationToken)
    {
        var run = await _store.GetRunAsync(id, cancellationToken);
        if (run is null || !run.Rows.Any(row => row.LedgerJson is not null))
        {
            return NotFound();
        }

        var stamp = IndiaTime(run.CreatedAtUtc).ToString("yyyyMMdd-HHmm");
        return File(
            LedgerChecklistWriter.Write(run.Rows.Where(row => row.LedgerJson is not null)),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"LedgerLink-Checklist-{stamp}.xlsx");
    }

    public async Task<IActionResult> OnGetFileAsync(Guid id, string which, CancellationToken cancellationToken)
    {
        which = (which ?? string.Empty).ToLowerInvariant();
        if (which == "agents")
        {
            var agentsHeader = await _store.GetExportFileAsync(id, "_", cancellationToken);
            if (agentsHeader is null)
            {
                return NotFound();
            }

            var agents = await _store.GetApprovedAgentsFileAsync(id, cancellationToken);
            if (agents is null || agents.Length == 0)
            {
                return NotFound();
            }

            var stampAgents = IndiaTime(agentsHeader.Value.CreatedAtUtc).ToString("yyyyMMdd-HHmm");
            return File(agents, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"OrgMaster-Overseas-ApprovedAgents-{stampAgents}.xlsx");
        }

        var export = await _store.GetExportFileAsync(id, which, cancellationToken);
        if (export is null)
        {
            return NotFound();
        }

        var (createdAtUtc, bytes) = export.Value;
        if (bytes is null || bytes.Length == 0)
        {
            return NotFound();
        }

        var stamp = IndiaTime(createdAtUtc).ToString("yyyyMMdd-HHmm");
        var name = which switch
        {
            "india" => $"OrgMaster-IN-{stamp}.xlsx",
            "overseas" => $"OrgMaster-Overseas-{stamp}.xlsx",
            "updates" => $"OrgMaster-AddressUpdates-{stamp}.xlsx",
            _ => string.Empty
        };
        if (name.Length == 0)
        {
            return NotFound();
        }

        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
    }

    public static DateTime IndiaTime(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), IndiaZone);

    private static readonly TimeZoneInfo IndiaZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
}
