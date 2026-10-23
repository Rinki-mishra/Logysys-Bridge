using System.Text;
using System.Text.RegularExpressions;

namespace LogisysOrgConverter.Notifications;

public sealed record ImportLogError(int Row, string OrganisationName, string Message);

public static class ImportLogParser
{
    private static readonly Regex ErrorLine = new(
        @"ERR:\s*Row\s*=\s*(?<row>\d+)\s*,\s*Org\s*=\s*(?<org>.*?)\s*,?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EntryStart = new(@"^\s*\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}", RegexOptions.Compiled);

    public static IReadOnlyList<ImportLogError> Parse(string text)
    {
        var errors = new List<ImportLogError>();
        var lines = text.Replace("\r\n", "\n").Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var match = ErrorLine.Match(lines[index]);
            if (!match.Success)
            {
                continue;
            }

            var message = new StringBuilder();
            var next = index + 1;
            while (next < lines.Length
                && !string.IsNullOrWhiteSpace(lines[next])
                && !EntryStart.IsMatch(lines[next]))
            {
                if (message.Length > 0)
                {
                    message.Append(' ');
                }

                message.Append(lines[next].Trim());
                next++;
            }

            errors.Add(new ImportLogError(
                int.Parse(match.Groups["row"].Value),
                match.Groups["org"].Value.Trim(),
                message.ToString()));
            index = next - 1;
        }

        return errors;
    }
}
