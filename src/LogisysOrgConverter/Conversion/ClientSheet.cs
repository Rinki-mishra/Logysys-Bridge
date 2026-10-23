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
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(stream);
        }
        catch (Exception ex)
        {
            throw new ClientSheetException("The file could not be opened as an Excel workbook.", ex);
        }

        using (workbook)
        {
            var sheet = workbook.Worksheets.FirstOrDefault()
                ?? throw new ClientSheetException("The workbook has no sheets.");

            var nameHeader = CellText(sheet.Cell(1, ColumnLetters.Index("W")));
            if (!nameHeader.Contains("company", StringComparison.OrdinalIgnoreCase))
            {
                throw new ClientSheetException(
                    "This workbook does not look like the client organisation sheet. Column W should be the company name.");
            }

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
