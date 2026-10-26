using System.Text;

namespace LogisysOrgConverter.Notifications;

public sealed record AgentForApproval(
    int RowId,
    string Name,
    string? City,
    string? Country,
    string? Details,
    string? RequesterEmail,
    string? RequesterName);

// One email to the approver listing every overseas agent of the run not yet sent for approval.
public static class AgentApprovalComposer
{
    public static EmailDraft? Compose(IReadOnlyList<AgentForApproval> agents, EmailOptions options)
    {
        if (agents.Count == 0 || string.IsNullOrWhiteSpace(options.AgentApprover))
        {
            return null;
        }

        var items = agents.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var subject = items.Count == 1
            ? $"Approval needed to create overseas agent: {items[0].Name}"
            : $"Approval needed to create {items.Count} overseas agents";

        var body = new StringBuilder();
        body.Append("Dear Sir,\n\n");
        body.Append(items.Count == 1
            ? "The overseas agent below has been requested. It will be created in Logi-Sys only after your approval.\n\n"
            : "The overseas agents below have been requested. They will be created in Logi-Sys only after your approval.\n\n");

        var number = 1;
        foreach (var item in items)
        {
            body.Append(number++).Append(". ").Append(item.Name);
            var place = string.Join(", ", new[] { item.City, item.Country }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (place.Length > 0)
            {
                body.Append(" (").Append(place).Append(')');
            }

            body.Append('\n');
            foreach (var line in (item.Details ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                body.Append("   ").Append(line).Append('\n');
            }

            body.Append('\n');
        }

        body.Append(items.Count == 1
            ? "Please reply to this email to approve or decline it.\n\n"
            : "Please reply to this email with the agents you approve and any you decline.\n\n");
        body.Append(options.Signature.Replace("\\n", "\n"));

        var organisations = items
            .Select(item => new CreatedOrganisation(item.RowId, item.Name, null, item.Country, options.AgentApprover.Trim(), null))
            .ToList();
        return new EmailDraft(options.AgentApprover.Trim(), null, subject, body.ToString(), organisations);
    }
}
