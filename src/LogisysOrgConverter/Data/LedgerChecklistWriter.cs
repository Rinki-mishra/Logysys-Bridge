using ClosedXML.Excel;
using LogisysOrgConverter.Conversion;

namespace LogisysOrgConverter.Data;

public static class LedgerChecklistWriter
{
    private static readonly string[] Headers =
    [
        "Organisation", "Client sheet row", "Requestor", "Requestor email", "Organisation status", "Ledger rules",
        "Ledger status", "Section", "Document or approval", "State", "Detail"
    ];

    public static byte[] Write(IEnumerable<ConversionRunRow> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Ledger link");
        for (var column = 0; column < Headers.Length; column++)
        {
            sheet.Cell(1, column + 1).Value = Headers[column];
        }

        sheet.Row(1).Style.Font.Bold = true;

        var line = 2;
        foreach (var row in rows)
        {
            var checklist = LedgerJson.Read(row.LedgerJson);
            if (checklist is null)
            {
                continue;
            }

            var ledgerStatus = row.LedgerLinkedAtUtc is null ? checklist.Summary : "Ledger linked";
            var items = checklist.Sections
                .SelectMany(section => section.Items.Select(item => (section.Title, item.Label, item.State, item.Detail))
                    .Concat(section.Notes.Select(note => (section.Title, "Note", string.Empty, (string?)note))))
                .DefaultIfEmpty((string.Empty, string.Empty, string.Empty, (string?)checklist.Summary));

            foreach (var (title, label, state, detail) in items)
            {
                string[] values =
                [
                    row.OrganisationName, row.SourceRowNumber.ToString(), row.RequesterName ?? string.Empty,
                    row.RequesterEmail ?? string.Empty,
                    row.Status == RowStatuses.Update ? "Already in Logi-Sys" : row.Status,
                    checklist.Category, ledgerStatus,
                    title, label, state, detail ?? string.Empty
                ];
                for (var column = 0; column < values.Length; column++)
                {
                    sheet.Cell(line, column + 1).SetValue(values[column]);
                }

                if (state == LedgerItemStates.Missing)
                {
                    sheet.Range(line, 1, line, Headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#FBE3DA");
                }

                line++;
            }
        }

        sheet.Columns().AdjustToContents(1, Math.Min(line, 300));
        sheet.SheetView.FreezeRows(1);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
