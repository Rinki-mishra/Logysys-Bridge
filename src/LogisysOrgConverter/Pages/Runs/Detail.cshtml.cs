using LogisysOrgConverter.Data;
using LogisysOrgConverter.Notifications;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LogisysOrgConverter.Pages.Runs;

public class DetailModel : PageModel
{
    private readonly ConversionStore _store;
    private readonly NotificationStore _notifications;
    private readonly MailDelivery _delivery;

    public DetailModel(ConversionStore store, NotificationStore notifications, MailDelivery delivery)
    {
        _store = store;
        _notifications = notifications;
        _delivery = delivery;
    }

    public ConversionRun? Run { get; private set; }

    public IReadOnlyList<EmailDraft> Drafts { get; private set; } = [];

    public IReadOnlyList<EmailNotification> Sent { get; private set; } = [];

    public IReadOnlyDictionary<int, string> PendingElsewhere { get; private set; } = new Dictionary<int, string>();

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
        PendingElsewhere = await _store.GetPendingElsewhereAsync(id, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostImportResultAsync(Guid id, string template, IFormFile? log, bool markAll, CancellationToken cancellationToken)
    {
        if (template is not ("India" or "Overseas"))
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

    public async Task<IActionResult> OnGetFileAsync(Guid id, string which, CancellationToken cancellationToken)
    {
        var run = await _store.GetRunAsync(id, cancellationToken);
        if (run is null)
        {
            return NotFound();
        }

        var stamp = IndiaTime(run.CreatedAtUtc).ToString("yyyyMMdd-HHmm");
        var (bytes, name) = which.ToLowerInvariant() switch
        {
            "india" => (run.IndiaFile, $"OrgMaster-IN-{stamp}.xlsx"),
            "overseas" => (run.OverseasFile, $"OrgMaster-Overseas-{stamp}.xlsx"),
            "updates" => (run.UpdatesFile, $"OrgMaster-AddressUpdates-{stamp}.xlsx"),
            _ => (null, string.Empty)
        };
        if (bytes is null || bytes.Length == 0)
        {
            return NotFound();
        }

        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
    }

    public static DateTime IndiaTime(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), IndiaZone);

    private static readonly TimeZoneInfo IndiaZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
}
