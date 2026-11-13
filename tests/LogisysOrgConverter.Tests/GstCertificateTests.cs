using LogisysOrgConverter.Conversion;
using LogisysOrgConverter.GoogleDrive;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace LogisysOrgConverter.Tests;

public class GstCertificateTests
{
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Book1_organization.xlsx");
    private const string LilaramLink = "https://drive.google.com/open?id=1NhPv2hEzO11gYvrgHz32J1zjqa2kJmth";

    [Theory]
    [InlineData("27AABFS6109H1Z9", true)]
    [InlineData("27AAAFL1370N1Z7", true)]
    [InlineData("27AABFS6109H1Z8", false)]
    [InlineData("27AAAF1370N1Z7", false)]
    public void Check_character_rejects_mistyped_gstins(string gstin, bool valid) =>
        Assert.Equal(valid, GstinReader.HasValidCheckCharacter(gstin));

    [Fact]
    public void Registration_number_is_read_from_a_gst_certificate_pdf()
    {
        var pdf = CertificatePdf(
            "Government of India",
            "Form GST REG-06",
            "Registration Certificate",
            "Registration Number : 27AABFS6109H1Z9",
            "1. Legal Name S D BAKHAI & CO");

        var result = GstinReader.FromFile(pdf, "application/pdf", "AABFS6109H");

        Assert.Equal("27AABFS6109H1Z9", result.Gstin);
        Assert.Null(result.Problem);
    }

    [Fact]
    public void Labelled_registration_number_wins_over_other_gstins_in_the_text()
    {
        var text = "Issued with reference to 33AABCV1856H1ZF. Registration Number: 27AABFS6109H1Z9 Legal name ...";

        Assert.Equal("27AABFS6109H1Z9", GstinReader.Find(text, null));
    }

    [Fact]
    public void Number_with_a_wrong_check_character_is_not_taken()
    {
        Assert.Null(GstinReader.Find("Registration Number : 27AABFS6109H1Z8", null));
    }

    [Fact]
    public void Spaced_out_characters_are_still_read()
    {
        Assert.Equal("27AAAFL1370N1Z7", GstinReader.Find("Registration Number 2 7 A A A F L 1 3 7 0 N 1 Z 7", null));
    }

    [Fact]
    public void Images_and_text_less_pdfs_are_reported_not_guessed()
    {
        Assert.NotNull(GstinReader.FromFile([1, 2, 3], "image/jpeg", null).Problem);
        Assert.NotNull(GstinReader.FromFile(CertificatePdf(), "application/pdf", null).Problem);
        Assert.NotNull(GstinReader.FromFile([1, 2, 3], "application/pdf", null).Problem);
    }

    [Theory]
    [InlineData("https://drive.google.com/open?id=1VJONQtgWnrQigEEt5Wz8K8EWcH70oevT", "1VJONQtgWnrQigEEt5Wz8K8EWcH70oevT")]
    [InlineData("https://drive.google.com/file/d/1VJONQtgWnrQigEEt5Wz8K8EWcH70oevT/view?usp=sharing", "1VJONQtgWnrQigEEt5Wz8K8EWcH70oevT")]
    [InlineData("attached", null)]
    public void Drive_file_id_is_taken_from_the_link(string link, string? id) =>
        Assert.Equal(id, GoogleDriveAccess.FileId(link));

    [Fact]
    public void Certificate_gstin_replaces_a_mistyped_one_in_reg_gstin()
    {
        var without = Convert(null);
        Assert.Contains(without.Held, row => row.Name == "LILARAM GOBINDRAM" && row.Reason.Contains("27AAAF1370N1Z7"));

        var result = Convert(new Dictionary<string, GstCertificate> { [LilaramLink] = GstCertificate.Read("27AAAFL1370N1Z7") });

        var lilaram = Assert.Single(result.India, row => row.Name == "LILARAM GOBINDRAM");
        Assert.Equal("27AAAFL1370N1Z7", lilaram.Cells["REG-GSTIN"]);
        Assert.Contains("taken from the GST certificate", lilaram.Note);
        Assert.Contains("27AAAF1370N1Z7", lilaram.Note);
    }

    [Fact]
    public void Unread_certificate_keeps_the_typed_gstin_and_says_why()
    {
        var result = Convert(new Dictionary<string, GstCertificate>
        {
            [LilaramLink] = GstCertificate.Failed("Google Drive is not connected")
        });

        var lilaram = Assert.Single(result.Held, row => row.Name == "LILARAM GOBINDRAM");
        Assert.Contains("GST certificate not read (Google Drive is not connected)", lilaram.Reason);
    }

    [Theory]
    [InlineData("no-reply@accounts.google.com", "Your verification code", "Your Google verification code is 482913. It expires in 10 minutes.", "482913")]
    [InlineData("drive-shares-dm-noreply@google.com", "Verify your email", "Use this PIN to access the file: 73104862", "73104862")]
    [InlineData("no-reply@accounts.google.com", "G-552019 is your Google verification code", "", "552019")]
    [InlineData("alerts@bank.com", "Your OTP code", "Code 123456", null)]
    [InlineData("no-reply@accounts.google.com", "Security alert", "New sign-in on Windows", null)]
    public void Google_code_is_taken_only_from_google_emails(string from, string subject, string body, string? code)
    {
        var reader = new OutlookCodeReader(
            Microsoft.Extensions.Options.Options.Create(new MicrosoftOptions()),
            null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OutlookCodeReader>.Instance);

        Assert.Equal(code, reader.ExtractCode(from, subject, body));
    }

    private static ConversionResult Convert(IReadOnlyDictionary<string, GstCertificate>? certificates)
    {
        using var stream = File.OpenRead(Fixture);
        return new OrganisationConverter().Convert(ClientSheetReader.Read(stream), MappingSeed.CreateCatalog(), certificates);
    }

    private static byte[] CertificatePdf(params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var y = 780;
        foreach (var line in lines)
        {
            page.AddText(line, 11, new UglyToad.PdfPig.Core.PdfPoint(50, y), font);
            y -= 20;
        }

        return builder.Build();
    }
}
