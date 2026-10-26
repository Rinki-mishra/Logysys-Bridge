using ClosedXML.Excel;

namespace LogisysOrgConverter.Conversion;

public static class AddressUpdateWriter
{
    private static readonly string[] Headers =
    [
        "Name", "BranchName", "BranchAddress1", "BranchAddress2", "BranchAddress3", "City", "State", "Country",
        "PostalCode", "Telephone", "Requested at", "Requestor", "Requestor email", "Client sheet row", "Already in Logi-Sys"
    ];

    public static byte[] Write(IEnumerable<AddressUpdate> updates)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Address updates");
        for (var column = 0; column < Headers.Length; column++)
        {
            sheet.Cell(1, column + 1).Value = Headers[column];
        }

        sheet.Row(1).Style.Font.Bold = true;

        var row = 2;
        foreach (var update in updates.Where(item => item.HasAddress))
        {
            string[] values =
            [
                update.Name, update.City, update.BranchAddress1, update.BranchAddress2, update.BranchAddress3,
                update.City, update.State, update.Country, update.PostalCode, update.Telephone,
                update.RequestedAt?.ToString("dd-MMM-yyyy HH:mm") ?? string.Empty,
                update.RequesterName ?? string.Empty, update.RequesterEmail ?? string.Empty,
                (update.AddressFromRow ?? update.SourceRowNumber).ToString(), update.ExistsIn
            ];

            for (var column = 0; column < values.Length; column++)
            {
                sheet.Cell(row, column + 1).SetValue(values[column]);
            }

            row++;
        }

        sheet.Columns().AdjustToContents(1, Math.Min(row, 200));
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
