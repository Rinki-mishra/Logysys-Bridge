using ClosedXML.Excel;

namespace LogisysOrgConverter.Conversion;

// Reads organisation names from an export of Logi-Sys: the column headed Name (or a similar
// heading) in the first sheet of an .xlsx, or in a .csv. Without such a heading, column A is used.
public static class OrganisationListReader
{
    private static readonly string[] NameHeadings =
    [
        "name", "organisation name", "organization name", "org name", "orgname",
        "organisation", "organization", "company name", "party name"
    ];

    public static IReadOnlyList<string> Read(Stream stream, string fileName)
    {
        var rows = fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            ? ReadCsv(stream)
            : ReadWorkbook(stream);

        var headerRow = -1;
        var nameColumn = 0;
        for (var index = 0; index < Math.Min(rows.Count, 10) && headerRow < 0; index++)
        {
            for (var column = 0; column < rows[index].Count; column++)
            {
                if (NameHeadings.Contains(rows[index][column].Trim().ToLowerInvariant()))
                {
                    headerRow = index;
                    nameColumn = column;
                    break;
                }
            }
        }

        return rows
            .Skip(headerRow + 1)
            .Select(row => nameColumn < row.Count ? row[nameColumn].Trim() : string.Empty)
            .Where(name => OrganisationConverter.NameKey(name).Length > 0)
            .ToList();
    }

    private static List<List<string>> ReadWorkbook(Stream stream)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(stream);
        }
        catch (Exception ex)
        {
            throw new ClientSheetException("The file could not be opened. Upload an .xlsx or .csv file.", ex);
        }

        using (workbook)
        {
            var sheet = workbook.Worksheets.First();
            var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
            var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
            var rows = new List<List<string>>();
            for (var row = 1; row <= lastRow; row++)
            {
                var values = new List<string>();
                for (var column = 1; column <= lastColumn; column++)
                {
                    values.Add(sheet.Cell(row, column).GetFormattedString());
                }

                rows.Add(values);
            }

            return rows;
        }
    }

    private static List<List<string>> ReadCsv(Stream stream)
    {
        using var reader = new StreamReader(stream);
        var rows = new List<List<string>>();
        while (reader.ReadLine() is { } line)
        {
            var values = new List<string>();
            var current = new System.Text.StringBuilder();
            var quoted = false;
            for (var index = 0; index < line.Length; index++)
            {
                var character = line[index];
                if (quoted)
                {
                    if (character == '"' && index + 1 < line.Length && line[index + 1] == '"')
                    {
                        current.Append('"');
                        index++;
                    }
                    else if (character == '"')
                    {
                        quoted = false;
                    }
                    else
                    {
                        current.Append(character);
                    }
                }
                else if (character == '"')
                {
                    quoted = true;
                }
                else if (character == ',')
                {
                    values.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(character);
                }
            }

            values.Add(current.ToString());
            rows.Add(values);
        }

        return rows;
    }
}
