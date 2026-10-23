using LogisysOrgConverter.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LogisysOrgConverter.Data;

public sealed record ImportLogSummary(int Created, int Failed, IReadOnlyList<string> UnmatchedNames);

public sealed record SendSummary(int Sent, int Failed, string Delivery);

public sealed class NotificationStore
{
    private readonly AppDbContext _db;
    private readonly MailDelivery _delivery;
    private readonly EmailOptions _options;

    public NotificationStore(AppDbContext db, MailDelivery delivery, IOptions<EmailOptions> options)
    {
        _db = db;
        _delivery = delivery;
        _options = options.Value;
    }

    public async Task<ImportLogSummary> ApplyImportResultAsync(
        Guid runId,
        string template,
        string? logText,
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.RunRows
            .Where(row => row.RunId == runId && row.Status == "Ready" && row.Template == template)
            .ToListAsync(cancellationToken);
        var errors = string.IsNullOrWhiteSpace(logText) ? [] : ImportLogParser.Parse(logText);
        var byName = errors
            .GroupBy(error => error.OrganisationName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => string.Join(" ", group.Select(error => error.Message)), StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var created = 0;
        var failed = 0;

        foreach (var row in rows)
        {
            if (byName.TryGetValue(row.OrganisationName.Trim(), out var message) && row.EmailedAtUtc is null)
            {
                row.ImportStatus = ImportStatuses.Failed;
                row.ImportMessage = message;
                failed++;
            }
            else
            {
                row.ImportStatus = ImportStatuses.Created;
                row.ImportMessage = null;
                created++;
            }

            row.ImportUpdatedAtUtc = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
        var known = rows.Select(row => row.OrganisationName.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unmatched = byName.Keys.Where(name => !known.Contains(name)).OrderBy(name => name).ToList();
        return new ImportLogSummary(created, failed, unmatched);
    }

    public async Task<bool> SetImportStatusAsync(Guid runId, int rowId, string status, CancellationToken cancellationToken = default)
    {
        if (status is not (ImportStatuses.Created or ImportStatuses.Failed))
        {
            return false;
        }

        var row = await _db.RunRows.FirstOrDefaultAsync(
            item => item.Id == rowId && item.RunId == runId && item.Status == "Ready",
            cancellationToken);
        if (row is null || (row.EmailedAtUtc is not null && status == ImportStatuses.Failed))
        {
            return false;
        }

        row.ImportStatus = status;
        row.ImportMessage = status == ImportStatuses.Created ? null : row.ImportMessage ?? "Marked as not created.";
        row.ImportUpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<EmailDraft>> GetDraftsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.RunRows.AsNoTracking()
            .Where(row => row.RunId == runId
                && row.ImportStatus == ImportStatuses.Created
                && row.EmailedAtUtc == null
                && row.RequesterEmail != null)
            .ToListAsync(cancellationToken);

        return EmailComposer.Compose(
            rows.Select(row => new CreatedOrganisation(row.Id, row.OrganisationName, row.BranchName, row.Country, row.RequesterEmail!, row.RequesterName)),
            _options);
    }

    public Task<List<EmailNotification>> GetNotificationsAsync(Guid runId, CancellationToken cancellationToken = default) =>
        _db.EmailNotifications.AsNoTracking()
            .Where(item => item.RunId == runId)
            .OrderByDescending(item => item.CreatedAtUtc)
            .ToListAsync(cancellationToken);

    public async Task<SendSummary> SendAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var drafts = await GetDraftsAsync(runId, cancellationToken);
        var sent = 0;
        var failed = 0;
        var delivery = _delivery.UsesSmtp ? "SMTP" : "File";

        foreach (var draft in drafts)
        {
            var result = await _delivery.SendAsync(draft, cancellationToken);
            delivery = result.Delivery;
            var now = DateTime.UtcNow;
            _db.EmailNotifications.Add(new EmailNotification
            {
                RunId = runId,
                RecipientEmail = draft.RecipientEmail,
                RecipientName = draft.RecipientName,
                Organisations = string.Join("; ", draft.Organisations.Select(item => item.Name)),
                Subject = draft.Subject,
                Body = draft.Body,
                Delivery = result.Delivery,
                Status = result.Succeeded ? "Sent" : "Failed",
                Error = result.Error,
                CreatedAtUtc = now
            });

            if (result.Succeeded)
            {
                var ids = draft.Organisations.Select(item => item.RowId).ToList();
                var rows = await _db.RunRows.Where(row => ids.Contains(row.Id)).ToListAsync(cancellationToken);
                foreach (var row in rows)
                {
                    row.EmailedAtUtc = now;
                }

                sent++;
            }
            else
            {
                failed++;
            }

            await _db.SaveChangesAsync(cancellationToken);
        }

        return new SendSummary(sent, failed, delivery);
    }
}
