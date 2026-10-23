using System.Text;

namespace LogisysOrgConverter.Notifications;

public sealed record CreatedOrganisation(int RowId, string Name, string? BranchName, string? Country, string RequesterEmail, string? RequesterName);

public sealed record EmailDraft(
    string RecipientEmail,
    string? RecipientName,
    string Subject,
    string Body,
    IReadOnlyList<CreatedOrganisation> Organisations);

public static class EmailComposer
{
    public static IReadOnlyList<EmailDraft> Compose(IEnumerable<CreatedOrganisation> organisations, EmailOptions options)
    {
        return organisations
            .GroupBy(item => item.RequesterEmail.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var name = items.Select(item => item.RequesterName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
                var single = items.Count == 1;
                var subject = single
                    ? $"Organisation created in Logi-Sys: {items[0].Name}"
                    : $"{items.Count} organisations created in Logi-Sys";

                var body = new StringBuilder();
                body.Append("Dear ").Append(string.IsNullOrWhiteSpace(name) ? "Sir/Madam" : name).Append(",\n\n");
                body.Append(single ? options.Intro : options.IntroPlural).Append("\n\n");
                foreach (var item in items)
                {
                    body.Append(single ? "Name: " : "- ").Append(item.Name).Append('\n');
                    if (!string.IsNullOrWhiteSpace(item.BranchName))
                    {
                        body.Append(single ? "Branch: " : "  Branch: ").Append(item.BranchName).Append('\n');
                    }

                    if (!string.IsNullOrWhiteSpace(item.Country))
                    {
                        body.Append(single ? "Country: " : "  Country: ").Append(item.Country).Append('\n');
                    }

                    if (!single)
                    {
                        body.Append('\n');
                    }
                }

                if (single)
                {
                    body.Append('\n');
                }

                body.Append(single ? options.Closing : options.ClosingPlural).Append("\n\n");
                body.Append(options.Signature.Replace("\\n", "\n"));

                return new EmailDraft(group.Key, name, subject, body.ToString(), items);
            })
            .OrderBy(draft => draft.RecipientEmail, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
