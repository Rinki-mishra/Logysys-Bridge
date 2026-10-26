using ClosedXML.Excel;
using LogisysOrgConverter.Conversion;

namespace LogisysOrgConverter.Tests;

public class ConversionTests
{
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Web_app_automation.xlsx");
    private static readonly string IndiaTemplate = Path.Combine(AppContext.BaseDirectory, "Templates", "OrgMaster-IN-Template.xlsx");
    private static readonly string OverseasTemplate = Path.Combine(AppContext.BaseDirectory, "Templates", "OrgMaster-Overseas-Template.xlsx");

    [Fact]
    public void Sample_client_sheet_splits_india_and_overseas_and_holds_bad_cities()
    {
        using var stream = File.OpenRead(Fixture);
        var result = new OrganisationConverter().Convert(ClientSheetReader.Read(stream), MappingSeed.CreateCatalog());

        Assert.Equal(
            ["Terra Granite", "CreateART", "SUSSMAN-AUTOMATIC CORPORATION", "FULLY ORGANIC LLC"],
            result.Overseas.Select(row => row.Name).ToArray());
        Assert.Equal(["LAVISH GLOBAL LOGISTICS INDIA PRIVATE LIMITED"], result.India.Select(row => row.Name).ToArray());
        Assert.Equal(["MAUSER USA LLC - MASON", "ZENAVIS LLC"], result.Held.Select(row => row.Name).ToArray());

        var terra = result.Overseas[0];
        Assert.Equal("Houston", terra.BranchName);
        Assert.Equal("Houston", terra.Cells["City"]);
        Assert.Equal("Texas", terra.Cells["State"]);
        Assert.Equal("United States of America", terra.Cells["Country"]);
        Assert.Equal("77092", terra.Cells["PostalCode"]);
        Assert.Equal("yes", terra.Cells["IsGlobal"]);
        Assert.Equal("yes", terra.Cells["ISConsignee"]);
        Assert.False(terra.Cells.ContainsKey("IsShipper"));
        Assert.False(terra.Cells.ContainsKey("IsAgent"));
        Assert.False(terra.Cells.ContainsKey("EMailID"));

        var createArt = result.Overseas[1];
        Assert.Equal("yes", createArt.Cells["IsAgent"]);
        Assert.Equal("Queensland (QLD)", createArt.Cells["State"]);
        Assert.Equal("Australia", createArt.Cells["Country"]);
        Assert.Equal("4562", createArt.Cells["PostalCode"]);
        Assert.Equal("georgina@createart.com.au", createArt.Cells["EMailID"]);
        Assert.False(createArt.Cells.ContainsKey("Notification Email"));

        Assert.Equal("NY", result.Overseas[2].Cells["State"]);
        Assert.Equal("FLORIDA", result.Overseas[3].Cells["State"]);
        Assert.Equal("operation1@fullyorganicllc.com", result.Overseas[3].Cells["EMailID"]);

        var lavish = result.India[0];
        Assert.Equal("Sikar", lavish.BranchName);
        Assert.Equal("Rajasthan", lavish.Cells["State"]);
        Assert.Equal("India", lavish.Cells["Country"]);
        Assert.Equal("332001", lavish.Cells["PostalCode"]);
        Assert.Equal("yes", lavish.Cells["IsServiceProvider"]);
        Assert.Equal("accts@lavishglobal.sg", lavish.Cells["EMailID"]);

        Assert.Contains("looks like a street", result.Held[0].Reason);
        Assert.Contains("both \"CARY\"", result.Held[1].Reason);
    }

    [Fact]
    public void Writer_keeps_template_headers_and_drops_sample_rows()
    {
        using var stream = File.OpenRead(Fixture);
        var result = new OrganisationConverter().Convert(ClientSheetReader.Read(stream), MappingSeed.CreateCatalog());
        var writer = new TemplateWorkbookWriter();

        var indiaHeaders = Headers(IndiaTemplate);
        var overseasHeaders = Headers(OverseasTemplate);
        var india = Open(writer.Write(IndiaTemplate, result.India));
        var overseas = Open(writer.Write(OverseasTemplate, result.Overseas));

        Assert.Equal(indiaHeaders, Headers(india));
        Assert.Equal(overseasHeaders, Headers(overseas));
        Assert.Equal("LAVISH GLOBAL LOGISTICS INDIA PRIVATE LIMITED", Cell(india, 2, "Name"));
        Assert.True(string.IsNullOrEmpty(Cell(india, 3, "Name")));
        Assert.DoesNotContain("devesh", string.Join(' ', Names(india)), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Terra Granite", Cell(overseas, 2, "Name"));
        Assert.Equal("Houston", Cell(overseas, 2, "BranchName"));
        Assert.Equal("yes", Cell(overseas, 2, "ISConsignee"));
        Assert.Equal(string.Empty, Cell(overseas, 2, "EMailID"));
        Assert.Equal("georgina@createart.com.au", Cell(overseas, 3, "EMailID"));
        Assert.Equal(string.Empty, Cell(overseas, 3, "Notification Email"));
        Assert.Equal(string.Empty, Cell(india, 2, "Notification Email"));
        Assert.Equal("FULLY ORGANIC LLC", Cell(overseas, 5, "Name"));
        Assert.Equal(4, Names(overseas).Count(name => name.Length > 0));
    }

    [Fact]
    public void Holds_unknown_country_duplicate_name_and_bad_gstin()
    {
        using var stream = BuildSheet(
            Row(("W", "Goa Minerals"), ("X", "Consignee"), ("AB", "Goa"), ("AC", "Goa"), ("AD", "India")),
            Row(("W", "Bad Tax"), ("X", "Services"), ("AB", "Sikar"), ("AC", "Rajasthan"), ("AD", "INDIA"), ("AO", "ABC")),
            Row(("W", "Wrong State Tax"), ("X", "Services"), ("AB", "Sikar"), ("AC", "Rajasthan"), ("AD", "India"), ("AM", "ABCDE1234F"), ("AO", "27ABCDE1234F1Z5")),
            Row(("W", "Good Tax"), ("X", "Services"), ("AB", "Sikar"), ("AC", "Rajasthan"), ("AD", "India"), ("AM", "ABCDE1234F"), ("AO", "08ABCDE1234F1Z5")),
            Row(("W", "Goa Minerals"), ("X", "Shipper"), ("AB", "Mumbai"), ("AC", "Maharashtra"), ("AD", "India")),
            Row(("W", "Nowhere Ltd"), ("X", "Consignee"), ("AB", "Nowhere"), ("AC", "Nowhere"), ("AD", "Narnia")),
            Row(("W", "Street Co"), ("X", "Consignee"), ("AB", "CASTLE DRIVE"), ("AC", "Texas"), ("AD", "USA")));

        var result = new OrganisationConverter().Convert(ClientSheetReader.Read(stream), MappingSeed.CreateCatalog());

        Assert.Equal(["Goa Minerals", "Good Tax"], result.India.Select(row => row.Name).ToArray());
        Assert.Equal("08ABCDE1234F1Z5", result.India[1].Cells["REG-GSTIN"]);
        Assert.Equal("ABCDE1234F", result.India[1].Cells["REG-PAN NO"]);
        Assert.Contains(result.Held, row => row.Name == "Bad Tax" && row.Reason.Contains("GSTIN"));
        Assert.Contains(result.Held, row => row.Name == "Wrong State Tax" && row.Reason.Contains("does not match"));
        Assert.Contains(result.Held, row => row.Name == "Goa Minerals" && row.Reason.Contains("already used on row 2"));
        Assert.Contains(result.Held, row => row.Name == "Nowhere Ltd" && row.Reason.Contains("Narnia"));
        Assert.Contains(result.Held, row => row.Name == "Street Co" && row.Reason.Contains("street"));
        Assert.Empty(result.Overseas);
    }

    [Fact]
    public void Invoiced_organisations_are_read_from_the_j_to_u_section()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Book1_organization.xlsx"));
        var result = new OrganisationConverter().Convert(ClientSheetReader.Read(stream), MappingSeed.CreateCatalog());

        Assert.Equal(
            [
                "ESTEEM INDUSTRIES PVT.LTD.", "S D BAKHAI & CO", "S. D. Bakhai & co", "VISNUKUMAR TRADERS PVT LTD",
                "PETROCHEM CORPORATION", "PLASTECH", "NEO POLY INDUSTRIES PRIVATE LIMITED",
                "NAVRATAN SPECIALTY CHEMICALS LLP", "Mantram TechnoFab Pvt. Ltd.",
                "METACHEM MANUFACTURING COMPANY PRIVATE LIMITED", "PRET MODE"
            ],
            result.India.Select(row => row.Name).ToArray());
        Assert.Equal(6, result.Overseas.Count);

        var metachem = result.India.Single(row => row.Name.StartsWith("METACHEM"));
        Assert.Equal("PUNE", metachem.BranchName);
        Assert.Equal("147/1 & 2, SHEMBEKAR INDUSTRIAL AREA", metachem.Cells["BranchAddress1"]);
        Assert.Equal("CHINCHWAD ,PUNE ,", metachem.Cells["BranchAddress2"]);
        Assert.Equal("MAHARASHTRA, 411019", metachem.Cells["BranchAddress3"]);
        Assert.Equal("MAHARASHTRA", metachem.Cells["State"]);
        Assert.Equal("India", metachem.Cells["Country"]);
        Assert.Equal("411019", metachem.Cells["PostalCode"]);
        Assert.Equal("export@metachem.in", metachem.Cells["EMailID"]);
        Assert.Equal("NOMINATION SALES", metachem.Cells["Sales Person"]);
        Assert.Equal("yes", metachem.Cells["IsShipper"]);
        Assert.Equal("yes", metachem.Cells["IsServiceProvider"]);
        Assert.Equal("27AAACC7064K1ZH", metachem.Cells["REG-GSTIN"]);
        Assert.Equal("AAACC7064K", metachem.Cells["REG-PAN NO"]);
        Assert.Equal("riya@tnpsfl.com", metachem.RequesterEmail);

        Assert.Equal("MUMBAI", result.Held.Single(row => row.Name == "LILARAM GOBINDRAM").City);
        Assert.Contains(result.Held, row => row.Name == "LILARAM GOBINDRAM" && row.Reason.Contains("GSTIN"));
        Assert.Contains(result.Held, row => row.Name == "JTL INDUSTRIES LTD" && row.Reason.Contains("PAN"));
        Assert.Contains(result.Held, row => row.Name == "SEROJET FREIGHT (I) PRIVATE LIMITED" && row.Reason.Contains("GUJARAT"));
        Assert.Contains(result.Held, row => row.Name == "TRIDENT LIMITED");
    }

    private static Dictionary<string, string> Row(params (string Column, string Value)[] cells) =>
        cells.ToDictionary(cell => cell.Column, cell => cell.Value, StringComparer.OrdinalIgnoreCase);

    private static MemoryStream BuildSheet(params Dictionary<string, string>[] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Sheet1");
        sheet.Cell(1, ColumnLetters.Index("W")).Value = "name of the company ";
        var rowNumber = 2;
        foreach (var row in rows)
        {
            foreach (var (column, value) in row)
            {
                sheet.Cell(rowNumber, ColumnLetters.Index(column)).Value = value;
            }

            rowNumber++;
        }

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    private static IReadOnlyList<string> Headers(string path)
    {
        using var workbook = new XLWorkbook(path);
        return Headers(workbook);
    }

    private static IReadOnlyList<string> Headers(XLWorkbook workbook)
    {
        var sheet = workbook.Worksheet(TemplateWorkbookWriter.SheetName);
        var last = sheet.LastColumnUsed()?.ColumnNumber() ?? 1;
        return Enumerable.Range(1, last).Select(column => sheet.Cell(1, column).GetString()).ToList();
    }

    private static string Cell(XLWorkbook workbook, int row, string header)
    {
        var sheet = workbook.Worksheet(TemplateWorkbookWriter.SheetName);
        var last = sheet.LastColumnUsed()?.ColumnNumber() ?? 1;
        for (var column = 1; column <= last; column++)
        {
            if (sheet.Cell(1, column).GetString() == header)
            {
                return sheet.Cell(row, column).GetString();
            }
        }

        throw new InvalidOperationException(header);
    }

    private static IEnumerable<string> Names(XLWorkbook workbook)
    {
        var sheet = workbook.Worksheet(TemplateWorkbookWriter.SheetName);
        var last = sheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var row = 2; row <= last; row++)
        {
            yield return Cell(workbook, row, "Name");
        }
    }
}
