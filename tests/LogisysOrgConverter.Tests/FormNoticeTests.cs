using LogisysOrgConverter.Conversion;
using LogisysOrgConverter.Notifications;

namespace LogisysOrgConverter.Tests;

public class FormNoticeTests
{
    [Fact]
    public void Reads_a_new_response_notice()
    {
        const string html = """
            <a href="https://docs.google.com/forms/d/1fbgsQ81_0QP1HWsI5hYgoFmjXWWfYAp7Zsz-VDF0LFU/edit?usp=mail_response_notification#responses">Summary</a>
            """;

        var notice = FormNoticeParser.Parse(
            "Your form, Organization Creation Request Form - Logi-sys, has new responses.",
            "View Response: cs11nom.mum@sartransport.com VIEW SUMMARY",
            html,
            new DateTime(2026, 10, 29, 5, 14, 55, DateTimeKind.Utc));

        Assert.NotNull(notice);
        Assert.Equal(FormSourceKinds.Form, notice.Kind);
        Assert.Equal("1fbgsQ81_0QP1HWsI5hYgoFmjXWWfYAp7Zsz-VDF0LFU", notice.SourceId);
        Assert.Equal("Organization Creation Request Form - Logi-sys", notice.Title);
        Assert.Equal("cs11nom.mum@sartransport.com", notice.RequestorEmail);
    }

    [Fact]
    public void Reads_a_forwarded_spreadsheet_notice()
    {
        const string html = """
            <a href="https://docs.google.com/spreadsheets/d/1p2wM1GNb-idsjqaXw7E7QCbDxyFhPUkbjHuzMf11xPY/edit?ouid=104">Open</a>
            """;

        var notice = FormNoticeParser.Parse(
            "FW: \"Organization Creation Request Form - SAR Canada & SAR USA (Responses)\" was edited recently",
            null,
            html,
            new DateTime(2026, 10, 28, 21, 35, 23, DateTimeKind.Utc));

        Assert.NotNull(notice);
        Assert.Equal(FormSourceKinds.Sheet, notice.Kind);
        Assert.Equal("1p2wM1GNb-idsjqaXw7E7QCbDxyFhPUkbjHuzMf11xPY", notice.SourceId);
        Assert.Equal("Organization Creation Request Form - SAR Canada & SAR USA (Responses)", notice.Title);
        Assert.Null(notice.RequestorEmail);
        Assert.EndsWith(".xlsx", notice.FileName);
    }

    [Fact]
    public void Reads_the_spreadsheet_link_from_a_share_mail()
    {
        const string html = """
            <a href="https://www.google.com/url?q=https://docs.google.com/spreadsheets/d/1abcDef_GH-ijkLMNopqrst/edit?usp=sharing">
            Organisation Creation Request Form (Responses)
            </a>
            """;

        Assert.Equal("1abcDef_GH-ijkLMNopqrst", FormNoticeParser.SpreadsheetIdFrom(html));
        Assert.Equal(
            "Organisation Creation Request Form (Responses)",
            FormNoticeParser.SheetTitleFrom("Spreadsheet shared with you: \"Organisation Creation Request Form (Responses)\""));
    }

    [Fact]
    public void Opens_the_Form_Responses_1_tab()
    {
        var url = FormNoticeParser.WithFormResponsesTab(
            "https://docs.google.com/spreadsheets/d/1abcDef_GH-ijkLMNopqrst/edit?usp=sharing#gid=18");

        Assert.Contains("gid=" + FormNotice.FormResponsesGid, url);
        Assert.EndsWith("#gid=" + FormNotice.FormResponsesGid, url);

        var notice = new FormNotice(
            FormSourceKinds.Sheet,
            "1abcDef_GH-ijkLMNopqrst",
            "Organistion Creation Request Form (Responses)",
            DateTime.UtcNow,
            null,
            "https://docs.google.com/spreadsheets/d/1abcDef_GH-ijkLMNopqrst/edit?pli=1");
        Assert.Contains("gid=" + FormNotice.FormResponsesGid, notice.OpenUrl);
    }

    [Fact]
    public void Reads_the_visitor_code_and_ignores_the_street_number()
    {
        const string body = """
            Google Verification Code
            This email address was recently used to start a visitor session.
            You can use this code to verify that this email belongs to you.
            482913
            Google Inc., 1600 Amphitheatre Parkway
            """;

        Assert.Equal("482913", FormMailbox.ReadVisitorCode(body));
    }

    [Fact]
    public void Ignores_mail_that_is_not_a_form_notice()
    {
        var notice = FormNoticeParser.Parse(
            "Organization Creation Request Successful: ANCHAL INDIA PISTONS & RINGS",
            null,
            "<a href=\"https://docs.google.com/spreadsheets/d/abcdefghijklmnopqrstuvwxyz12/edit\">x</a>",
            DateTime.UtcNow);

        Assert.Null(notice);
    }

    [Fact]
    public void Response_sheet_export_is_the_client_workbook()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Web_app_automation.xlsx"));
        var rows = ClientSheetReader.Read(stream);

        Assert.NotEmpty(rows);
    }
}
