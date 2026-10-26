using System.Text;
using LogisysOrgConverter.Conversion;

namespace LogisysOrgConverter.Notifications;

public sealed record LedgerRequestOrganisation(
    int RowId,
    string Name,
    string? Country,
    string RequesterEmail,
    string? RequesterName,
    LedgerChecklist Checklist);

// Asks the requestor for what is still needed before the ledger can be linked:
// documents missing from the form, documents the form does not ask for, and approvals.
public static class LedgerRequestComposer
{
    public static bool HasRequest(LedgerChecklist checklist) =>
        checklist.Sections.Any(section => section.Items.Any(item =>
            item.State is LedgerItemStates.Missing or LedgerItemStates.Collect or LedgerItemStates.Approval));

    public static IReadOnlyList<EmailDraft> Compose(IEnumerable<LedgerRequestOrganisation> organisations, EmailOptions options)
    {
        return organisations
            .Where(item => HasRequest(item.Checklist))
            .GroupBy(item => item.RequesterEmail.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var name = items.Select(item => item.RequesterName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
                var subject = items.Count == 1
                    ? $"Documents needed for ledger link: {items[0].Name}"
                    : $"Documents needed for ledger link: {items.Count} organisations";

                var body = new StringBuilder();
                body.Append("Dear ").Append(string.IsNullOrWhiteSpace(name) ? "Sir/Madam" : name).Append(",\n\n");
                body.Append(items.Count == 1
                    ? "You asked for the organisation below to be invoiced. Before its ledger can be linked in Logi-Sys, we need the following.\n\n"
                    : "You asked for the organisations below to be invoiced. Before their ledgers can be linked in Logi-Sys, we need the following.\n\n");

                foreach (var item in items)
                {
                    body.Append(item.Name);
                    if (!string.IsNullOrWhiteSpace(item.Country))
                    {
                        body.Append(" (").Append(item.Country).Append(')');
                    }

                    body.Append('\n');
                    var all = item.Checklist.Sections.SelectMany(section => section.Items).ToList();
                    AppendList(body, "Documents", all
                        .Where(entry => entry.State is LedgerItemStates.Missing or LedgerItemStates.Collect)
                        .Select(entry => entry.State == LedgerItemStates.Missing && !string.IsNullOrWhiteSpace(entry.Detail)
                            ? $"{entry.Label} ({entry.Detail})"
                            : entry.Label));
                    AppendList(body, "Approvals", all
                        .Where(entry => entry.State == LedgerItemStates.Approval)
                        .Select(entry => entry.Label));
                    AppendList(body, "Please note", item.Checklist.Sections.SelectMany(section => section.Notes));
                    body.Append('\n');
                }

                body.Append("Please reply to this email with the documents and approvals.\n\n");
                body.Append(options.Signature.Replace("\\n", "\n"));

                var organisationsInDraft = items
                    .Select(item => new CreatedOrganisation(item.RowId, item.Name, null, item.Country, item.RequesterEmail, item.RequesterName))
                    .ToList();
                return new EmailDraft(group.Key, name, subject, body.ToString(), organisationsInDraft);
            })
            .OrderBy(draft => draft.RecipientEmail, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AppendList(StringBuilder body, string title, IEnumerable<string> lines)
    {
        var list = lines.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
        {
            return;
        }

        body.Append("  ").Append(title).Append(":\n");
        foreach (var line in list)
        {
            body.Append("  - ").Append(line).Append('\n');
        }
    }
}
