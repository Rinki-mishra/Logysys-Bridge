using ClosedXML.Excel;

namespace LogisysOrgConverter.Conversion;

public sealed class TemplateWorkbookWriter
{
    public const string SheetName = "ORGANISATION MASTER";

    public byte[] Write(string templatePath, IReadOnlyList<ReadyOrganisation> rows)
    {
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException("The Logi-Sys template file is missing.", templatePath);
        }

        using var workbook = new XLWorkbook(templatePath);
        var sheet = workbook.Worksheets.FirstOrDefault(item => item.Name == SheetName)
            ?? throw new InvalidOperationException($"The template is missing the {SheetName} sheet.");

        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 1;
        for (var column = 1; column <= lastColumn; column++)
        {
            var header = sheet.Cell(1, column).GetString();
            if (header.Length > 0)
            {
                columns[header] = column;
            }
        }

        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var row = lastRow; row >= 2; row--)
        {
            sheet.Row(row).Delete();
        }

        var excelRow = 2;
        foreach (var organisation in rows)
        {
            foreach (var (header, value) in organisation.Cells)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (!columns.TryGetValue(header, out var column))
                {
                    throw new InvalidOperationException($"The template has no column \"{header}\".");
                }

                var cell = sheet.Cell(excelRow, column);
                cell.Style.NumberFormat.Format = "@";
                cell.Value = value;
            }

            excelRow++;
        }

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }
}
