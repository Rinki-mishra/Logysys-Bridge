using LogisysOrgConverter.Conversion;
using LogisysOrgConverter.Notifications;

namespace LogisysOrgConverter.Tests;

public class NotificationTests
{
    private const string SampleLog = """
        2026-10-23 16:44:59 ERR: Row=2, Org=CreateART,
        Violation of PRIMARY KEY constraint 'PK__Org_Cont__FA41CC147ED24B34'. Cannot insert duplicate key in object
        'dbo.Org_Contacts'. The duplicate key value is (1, 12, ACC).
        The statement has been terminated.

        2026-10-23 16:44:59 ERR: Row=4, Org=FULLY ORGANIC LLC,
        Violation of PRIMARY KEY constraint 'PK__Org_Cont__FA41CC147ED24B34'. Cannot insert duplicate key in object
        'dbo.Org_Contacts'. The duplicate key value is (1, 14, ACC).
        The statement has been terminated.
        """;

    [Fact]
    public void Parses_the_logisys_error_log()
    {
        var errors = ImportLogParser.Parse(SampleLog);

        Assert.Equal(2, errors.Count);
        Assert.Equal(2, errors[0].Row);
        Assert.Equal("CreateART", errors[0].OrganisationName);
        Assert.Contains("Org_Contacts", errors[0].Message);
        Assert.Contains("(1, 12, ACC)", errors[0].Message);
        Assert.Equal("FULLY ORGANIC LLC", errors[1].OrganisationName);
        Assert.DoesNotContain("FULLY ORGANIC", errors[0].Message);
    }

    [Fact]
    public void Keeps_commas_inside_organisation_names()
    {
        var errors = ImportLogParser.Parse("2026-10-23 10:00:00 ERR: Row=1, Org=Acme, Inc.,\nSome error.");

        Assert.Equal("Acme, Inc.", Assert.Single(errors).OrganisationName);
    }

    [Fact]
    public void Reads_requestor_from_columns_b_and_d()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Web_app_automation.xlsx"));
        var result = new OrganisationConverter().Convert(ClientSheetReader.Read(stream), MappingSeed.CreateCatalog());

        var terra = result.Overseas.Single(row => row.Name == "Terra Granite");
        Assert.Equal("ops231jpr.sea@sarlogisolutions.com", terra.RequesterEmail);
        Assert.Equal("Manoj Prajapat", terra.RequesterName);
    }

    [Fact]
    public void Groups_one_email_per_requestor()
    {
        var options = new EmailOptions { Signature = "Regards,\\nTeam" };
        var drafts = EmailComposer.Compose(
        [
            new CreatedOrganisation(1, "Terra Granite", "Houston", "United States of America", "ops@sar.com", "Manoj Prajapat"),
            new CreatedOrganisation(2, "Zenith Ltd", "Pune", "India", "OPS@sar.com", null),
            new CreatedOrganisation(3, "CreateART", "Eumundi", "Australia", "cs3@sar.com", "")
        ], options);

        Assert.Equal(2, drafts.Count);
        var grouped = drafts.Single(draft => draft.Organisations.Count == 2);
        Assert.Equal("2 organisations created in Logi-Sys", grouped.Subject);
        Assert.StartsWith("Dear Manoj Prajapat,", grouped.Body);
        Assert.Contains("- Terra Granite", grouped.Body);
        Assert.Contains("- Zenith Ltd", grouped.Body);
        Assert.EndsWith("Regards,\nTeam", grouped.Body);

        var single = drafts.Single(draft => draft.Organisations.Count == 1);
        Assert.Equal("Organisation created in Logi-Sys: CreateART", single.Subject);
        Assert.StartsWith("Dear Sir/Madam,", single.Body);
        Assert.Contains("Branch: Eumundi", single.Body);
    }
}
