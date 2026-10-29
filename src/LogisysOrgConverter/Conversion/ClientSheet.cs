using System.Globalization;
using ClosedXML.Excel;

namespace LogisysOrgConverter.Conversion;

public sealed record ClientRow(int RowNumber, IReadOnlyDictionary<string, string> Cells);

public sealed class ClientSheetException : Exception
{
    public ClientSheetException(string message) : base(message)
    {
    }

    public ClientSheetException(string message, Exception inner) : base(message, inner)
    {
    }
}

public static class ClientSheetReader
{
    public static IReadOnlyList<ClientRow> Read(Stream stream)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        using var fixedStream = GoogleWorkbook.OpenableCopy(stream);
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(fixedStream);
        }
        catch (Exception ex)
        {
            var detail = string.IsNullOrWhiteSpace(ex.Message) ? string.Empty : " " + ex.Message.Split('\n')[0].Trim();
            throw new ClientSheetException("The file could not be opened as an Excel workbook." + detail, ex);
        }

        using (workbook)
        {
            var sheet = OrganisationSheet(workbook);

            var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
            var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 1;
            var rows = new List<ClientRow>();

            for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
            {
                var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var column = 1; column <= lastColumn; column++)
                {
                    var text = CellText(sheet.Cell(rowNumber, column));
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    cells[ColumnLetters.FromIndex(column)] = text;
                }

                if (cells.Count > 0)
                {
                    rows.Add(new ClientRow(rowNumber, cells));
                }
            }

            if (rows.Count == 0)
            {
                throw new ClientSheetException("The client sheet has no organisation rows.");
            }

            return rows;
        }
    }

    private static IXLWorksheet OrganisationSheet(XLWorkbook workbook)
    {
        IXLWorksheet? responses = null;
        IXLWorksheet? match = null;
        foreach (var sheet in workbook.Worksheets)
        {
            var header = CellText(sheet.Cell(1, ColumnLetters.Index("W")));
            if (!header.Contains("company", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = sheet.Name;
            if (name.Contains("Form Responses 1", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Form_Responses1", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Form Responses", StringComparison.OrdinalIgnoreCase))
            {
                return sheet;
            }

            if (name.Contains("response", StringComparison.OrdinalIgnoreCase))
            {
                responses ??= sheet;
            }

            match ??= sheet;
        }

        return responses ?? match ?? throw new ClientSheetException(
            "This workbook does not look like the client organisation sheet. Column W should be the company name.");
    }

    private static string CellText(IXLCell cell)
    {
        if (cell.IsEmpty())
        {
            return string.Empty;
        }

        return cell.DataType switch
        {
            XLDataType.Number => FormatNumber(cell.GetDouble()),
            XLDataType.DateTime => cell.GetDateTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            XLDataType.Boolean => cell.GetBoolean() ? "true" : "false",
            _ => cell.GetString().Trim()
        };
    }

    private static string FormatNumber(double value)
    {
        var rounded = Math.Round(value);
        if (Math.Abs(value - rounded) < 0.0000001)
        {
            return rounded.ToString("0", CultureInfo.InvariantCulture);
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }
}
