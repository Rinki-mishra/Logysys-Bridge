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
            ["Terra Granite", "SUSSMAN-AUTOMATIC CORPORATION", "FULLY ORGANIC LLC"],
            result.Overseas.Select(row => row.Name).ToArray());
        Assert.Equal(["CreateART"], result.AwaitingApproval.Select(row => row.Name).ToArray());
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

        var createArt = result.AwaitingApproval[0];
        Assert.Equal("yes", createArt.Cells["IsAgent"]);
        Assert.Equal("Queensland (QLD)", createArt.Cells["State"]);
        Assert.Equal("Australia", createArt.Cells["Country"]);
        Assert.Equal("4562", createArt.Cells["PostalCode"]);
        Assert.Equal("georgina@createart.com.au", createArt.Cells["EMailID"]);
        Assert.False(createArt.Cells.ContainsKey("Notification Email"));

        Assert.Equal("NY", result.Overseas[1].Cells["State"]);
        Assert.Equal("FLORIDA", result.Overseas[2].Cells["State"]);
        Assert.Equal("operation1@fullyorganicllc.com", result.Overseas[2].Cells["EMailID"]);

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
        Assert.Equal(string.Empty, Cell(overseas, 3, "Notification Email"));
        Assert.Equal(string.Empty, Cell(india, 2, "Notification Email"));
        Assert.Equal("FULLY ORGANIC LLC", Cell(overseas, 4, "Name"));
        Assert.Equal(3, Names(overseas).Count(name => name.Length > 0));

        var agents = Open(writer.Write(OverseasTemplate, result.AwaitingApproval));
        Assert.Equal("CreateART", Cell(agents, 2, "Name"));
        Assert.Equal("georgina@createart.com.au", Cell(agents, 2, "EMailID"));
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
        var goa = result.India[0];
        Assert.Equal(2, goa.SourceRowNumber);
        Assert.Equal("Mumbai", goa.City);
        Assert.Equal("Maharashtra", goa.Cells["State"]);
        Assert.Equal("yes", goa.Cells["ISConsignee"]);
        Assert.Equal("yes", goa.Cells["IsShipper"]);
        Assert.Contains("row 6", goa.Note);
        var merged = Assert.Single(result.Merged);
        Assert.Equal((6, 2, true), (merged.SourceRowNumber, merged.MergedIntoRowNumber, merged.AddressUsed));
        Assert.DoesNotContain(result.Held, row => row.Name == "Goa Minerals");
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
                "ESTEEM INDUSTRIES PVT.LTD.", "S D BAKHAI & CO", "TRIDENT LIMITED", "SUN NUTRAFOODS",
                "VARDHMAN TEXTILES LIMITED", "VISNUKUMAR TRADERS PVT LTD",
                "PETROCHEM CORPORATION", "PLASTECH", "NEO POLY INDUSTRIES PRIVATE LIMITED",
                "NAVRATAN SPECIALTY CHEMICALS LLP", "Mantram TechnoFab Pvt. Ltd.",
                "METACHEM MANUFACTURING COMPANY PRIVATE LIMITED", "PRET MODE"
            ],
            result.India.Select(row => row.Name).ToArray());
        Assert.Equal(10, result.Overseas.Count);
        var blueShipping = Assert.Single(result.AwaitingApproval);
        Assert.Equal("SARL BLUE SHIPPING SERVICES", blueShipping.Name);
        Assert.Contains("Type: Overseas Agent", blueShipping.Note);

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

        var bakhai = result.India.Single(row => row.Name == "S D BAKHAI & CO");
        Assert.Equal(5, bakhai.SourceRowNumber);
        Assert.Equal("201 „ SHARDA CHAMBERS.. 15.", bakhai.Cells["BranchAddress1"]);
        Assert.Equal("NEW MARINE LINES, CHURCHCATE", bakhai.Cells["BranchAddress2"]);
        Assert.Equal("Mumbai Suburban, Maharashtra, 400020", bakhai.Cells["BranchAddress3"]);
        Assert.Equal("MUMBAI", bakhai.BranchName);
        Assert.Equal("27AABFS6109H1Z9", bakhai.Cells["REG-GSTIN"]);
        Assert.Equal("sdbakhaico@gmail.com", bakhai.Cells["EMailID"]);
        Assert.Equal("riya@tnpsfl.com", bakhai.RequesterEmail);
        var merged = Assert.Single(result.Merged);
        Assert.Equal((6, 5, "S. D. Bakhai & co"), (merged.SourceRowNumber, merged.MergedIntoRowNumber, merged.Name));

        Assert.Equal("MUMBAI", result.Held.Single(row => row.Name == "LILARAM GOBINDRAM").City);
        Assert.Contains(result.Held, row => row.Name == "LILARAM GOBINDRAM" && row.Reason.Contains("GSTIN"));
        Assert.Contains(result.Held, row => row.Name == "JTL INDUSTRIES LTD" && row.Reason.Contains("PAN"));
        Assert.Contains(result.Held, row => row.Name == "SEROJET FREIGHT (I) PRIVATE LIMITED" && row.Reason.Contains("GUJARAT"));
        Assert.Contains(result.Held, row => row.Name == "STROLIT D.O.O." && row.Reason.Contains("another country"));

        var vardhman = result.India.Single(row => row.Name == "VARDHMAN TEXTILES LIMITED");
        Assert.Equal(("LUDHIANA", "Punjab", "India", "141010"), (vardhman.City, vardhman.Cells["State"], vardhman.Cells["Country"], vardhman.Cells["PostalCode"]));
        Assert.Contains("Read from the address", vardhman.Note);
        var sun = result.India.Single(row => row.Name == "SUN NUTRAFOODS");
        Assert.Equal(("MANDIDEEP", "Madhya Pradesh", "462046"), (sun.City, sun.Cells["State"], sun.Cells["PostalCode"]));
        var trident = result.India.Single(row => row.Name == "TRIDENT LIMITED");
        Assert.Equal(("SEHORE", "Madhya Pradesh"), (trident.City, trident.Cells["State"]));
        var milsped = result.Overseas.Single(row => row.Name == "MILSPED");
        Assert.Equal(("BELGRADE", "Serbia", "11070"), (milsped.City, milsped.Cells["Country"], milsped.Cells["PostalCode"]));
        Assert.False(milsped.Cells.ContainsKey("State"));
        Assert.Equal(("ROMA", "Italy"), (result.Overseas.Single(row => row.Name.StartsWith("SAVINO")).City, result.Overseas.Single(row => row.Name.StartsWith("SAVINO")).Country));
        Assert.Equal("FROSINONE", result.Overseas.Single(row => row.Name.StartsWith("KLOPMAN")).City);
        Assert.Equal("Algeria", result.AwaitingApproval.Single(row => row.Name.StartsWith("SARL BLUE")).Country);
    }

    [Fact]
    public void Missing_location_is_read_from_the_address_and_left_blank_when_unclear()
    {
        using var stream = BuildSheet(
            Row(("W", "Zip Co"), ("X", "Consignee"), ("Y", "11742, 187th Street,"), ("Z", "Artesia, CA 90701, USA"), ("AB", "."), ("AC", "."), ("AD", ".")),
            Row(("W", "Known Country"), ("X", "Consignee"), ("Y", "Hauptstrasse 5"), ("Z", "8001 Zurich"), ("AD", "Switzerland")),
            Row(("W", "Vague Ltd"), ("X", "Shipper"), ("Y", "Near the old market"), ("AB", ","), ("AD", ",")));

        var result = new OrganisationConverter().Convert(ClientSheetReader.Read(stream), MappingSeed.CreateCatalog());

        var zip = result.Overseas.Single(row => row.Name == "Zip Co");
        Assert.Equal(("Artesia", "California", "United States of America", "90701"), (zip.City, zip.Cells["State"], zip.Country, zip.Cells["PostalCode"]));
        var known = result.Overseas.Single(row => row.Name == "Known Country");
        Assert.Equal(("Zurich", "8001"), (known.City, known.Cells["PostalCode"]));
        var vague = Assert.Single(result.Held);
        Assert.Equal("Vague Ltd", vague.Name);
        Assert.Contains("could not be found in the address", vague.Reason);
    }

    [Fact]
    public void Repeated_organisation_takes_the_address_of_the_latest_request_by_timestamp()
    {
        using var stream = BuildSheet(
            Row(("A", "26/10/2026 09:00:00"), ("W", "Acme Exports"), ("X", "Shipper"), ("Y", "New Plot 9"), ("AB", "Pune"), ("AC", "Maharashtra"), ("AD", "India")),
            Row(("A", "20/10/2026 09:00:00"), ("W", "ACME EXPORTS."), ("X", "Consignee"), ("Y", "Old Plot 1"), ("AB", "Nashik"), ("AC", "Maharashtra"), ("AD", "India")),
            Row(("A", "27/10/2026 09:00:00"), ("W", "Acme  Exports"), ("X", "Shipper"), ("AB", "."), ("AD", ".")));

        var result = new OrganisationConverter().Convert(ClientSheetReader.Read(stream), MappingSeed.CreateCatalog());

        var acme = Assert.Single(result.India);
        Assert.Equal(2, acme.SourceRowNumber);
        Assert.Equal("Pune", acme.City);
        Assert.Equal("New Plot 9", acme.Cells["BranchAddress1"]);
        Assert.Equal("yes", acme.Cells["ISConsignee"]);
        Assert.Null(acme.Note);
        Assert.Equal([3, 4], result.Merged.Select(row => row.SourceRowNumber).ToArray());
        Assert.All(result.Merged, row => Assert.False(row.AddressUsed));
        Assert.Empty(result.Held);
    }

    [Fact]
    public void Organisation_already_in_logisys_becomes_an_address_update_from_the_latest_request()
    {
        using var stream = BuildSheet(
            Row(("A", "20/10/2026 09:00:00"), ("W", "S D Bakhai and Co"), ("X", "Shipper"), ("Y", "Old Plot 1"), ("AB", "Thane"), ("AC", "Maharashtra"), ("AD", "India")),
            Row(("A", "21/10/2026 09:00:00"), ("W", "New Org"), ("X", "Shipper"), ("AB", "Pune"), ("AC", "Maharashtra"), ("AD", "India")),
            Row(("A", "26/10/2026 09:00:00"), ("W", "S.D. BAKHAI AND CO."), ("X", "Shipper"), ("Y", "201 Sharda Chambers"), ("AB", "Mumbai"), ("AC", "Maharashtra"), ("AD", "INDIA"), ("AE", "400020")),
            Row(("W", "Ghost Ltd"), ("X", "Consignee"), ("AB", "."), ("AD", ".")));
        var seed = MappingSeed.CreateCatalog();
        var catalog = new MappingCatalog
        {
            FieldMaps = seed.FieldMaps,
            CountryAliases = seed.CountryAliases,
            KnownStates = seed.KnownStates,
            ExistingOrganisations = new Dictionary<string, string>
            {
                [OrganisationConverter.NameKey("S D BAKHAI AND CO")] = "on the Logi-Sys organisation list",
                [OrganisationConverter.NameKey("GHOST LTD")] = "on the Logi-Sys organisation list"
            }
        };

        var result = new OrganisationConverter().Convert(ClientSheetReader.Read(stream), catalog);

        Assert.Equal(["New Org"], result.India.Select(row => row.Name).ToArray());
        Assert.Empty(result.Overseas);
        Assert.Empty(result.Held);
        var bakhai = result.Updates.Single(row => row.SourceRowNumber == 2);
        Assert.True(bakhai.HasAddress);
        Assert.Equal(("201 Sharda Chambers", "Mumbai", "India", "400020", 4), (bakhai.BranchAddress1, bakhai.City, bakhai.Country, bakhai.PostalCode, bakhai.AddressFromRow));
        Assert.Equal(new DateTime(2026, 10, 26, 9, 0, 0), bakhai.RequestedAt);
        Assert.False(result.Updates.Single(row => row.Name == "Ghost Ltd").HasAddress);
        Assert.Equal(4, Assert.Single(result.Merged).SourceRowNumber);

        using var updates = Open(AddressUpdateWriter.Write(result.Updates));
        var sheet = updates.Worksheet(1);
        Assert.Equal("S D Bakhai and Co", sheet.Cell(2, 1).GetString());
        Assert.Equal("Mumbai", sheet.Cell(2, 2).GetString());
        Assert.Equal("201 Sharda Chambers", sheet.Cell(2, 3).GetString());
        Assert.True(sheet.Cell(3, 1).IsEmpty());
    }

    [Fact]
    public void Organisation_list_reads_the_name_column()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Orgs");
        sheet.Cell(1, 1).Value = "Code";
        sheet.Cell(1, 2).Value = "Name";
        sheet.Cell(2, 1).Value = "C1";
        sheet.Cell(2, 2).Value = "Terra Granite";
        sheet.Cell(3, 2).Value = " ";
        sheet.Cell(4, 2).Value = "CreateART";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        Assert.Equal(["Terra Granite", "CreateART"], OrganisationListReader.Read(stream, "orgs.xlsx"));
        using var csv = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Org Name,City\n\"Acme, Inc\",Pune\nZed Ltd,Goa\n"));
        Assert.Equal(["Acme, Inc", "Zed Ltd"], OrganisationListReader.Read(csv, "orgs.csv"));
    }

    [Fact]
    public void Invoiced_organisations_get_the_ledger_link_checklist()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Book1_organization.xlsx"));
        var result = new OrganisationConverter().Convert(ClientSheetReader.Read(stream), MappingSeed.CreateCatalog());

        Assert.All(result.India.Concat(result.Overseas).Where(row => row.SourceRowNumber is 2 or 3 or 20), row => Assert.Null(row.Ledger));

        var metachem = result.India.Single(row => row.Name.StartsWith("METACHEM")).Ledger!;
        Assert.Equal("India", metachem.Category);
        var section = Assert.Single(metachem.Sections);
        Assert.Equal("India – PAN, TAN and GST", section.Title);
        Assert.Equal(["PAN document", "TAN document", "GST document"], section.Items.Select(item => item.Label).ToArray());
        Assert.Equal(0, metachem.MissingCount);
        Assert.Equal("Ready for ledger link.", metachem.Summary);

        var neoPoly = result.India.Single(row => row.Name.StartsWith("NEO POLY")).Ledger!;
        Assert.Equal(LedgerItemStates.Missing, neoPoly.Sections[0].Items.Single(item => item.Label == "TAN document").State);
        Assert.Equal("3 required documents are missing.", neoPoly.Summary);

        var pret = result.India.Single(row => row.Name.StartsWith("PRET MODE")).Ledger!;
        Assert.Equal(["TAN document"], pret.Sections[0].Items.Where(item => item.State == LedgerItemStates.Missing).Select(item => item.Label).ToArray());

        Assert.Equal(LedgerChecklist.UnknownCountry, result.Held.Single(row => row.Name.StartsWith("SEROJET")).Ledger!.Category);
    }

    [Fact]
    public void Ledger_rules_follow_country_and_organisation_type()
    {
        Dictionary<string, string> Cells(params (string, string)[] values) =>
            values.ToDictionary(value => value.Item1, value => value.Item2, StringComparer.OrdinalIgnoreCase);

        var usCustomer = LedgerRules.Build(Cells(("I", "Yes"), ("X", "Consignee")), "United States of America");
        Assert.Contains(usCustomer.Sections[0].Items, item => item.Label == "W9 form");
        Assert.Contains(usCustomer.Sections[0].Items, item => item.State == LedgerItemStates.Approval && item.Label.Contains("JP sir"));

        var agent = LedgerRules.Build(Cells(("I", "Yes"), ("X", "Overseas Agent"), ("M", "https://drive/agreement")), "Germany");
        var agentItems = Assert.Single(agent.Sections).Items;
        Assert.Contains(agentItems, item => item.Label.Contains("Shivangi") && item.State == LedgerItemStates.Approval);
        Assert.Equal(LedgerItemStates.Missing, agentItems.Single(item => item.Label == "WCA ID").State);
        Assert.Equal(LedgerItemStates.Provided, agentItems.Single(item => item.Label == "Agency agreement").State);

        var carrier = LedgerRules.Build(Cells(("I", "Yes"), ("X", "Shipping Line")), "Singapore");
        Assert.Equal("Overseas payable – carrier, service", Assert.Single(carrier.Sections).Title);

        var dubai = LedgerRules.Build(Cells(("I", "Yes"), ("X", "Shipper"), ("AM", "TL-12345"), ("AO", "100234567800003")), "United Arab Emirates");
        Assert.Equal(0, dubai.MissingCount);

        var nepal = LedgerRules.Build(Cells(("I", "Yes"), ("X", "Consignee")), "Nepal");
        Assert.Contains("OVR RECEIVABLE", Assert.Single(nepal.Sections).Notes[0]);
        Assert.Equal(2, nepal.MissingCount);
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
