using System.IO.Compression;
using System.Text;
using LogisysOrgConverter.Conversion;

namespace LogisysOrgConverter.Tests;

public class GoogleWorkbookTests
{
    [Fact]
    public void Removes_google_pivot_relationships_without_id_or_target()
    {
        using var broken = BuildWorkbookWithBrokenPivotRels();
        using var fixedStream = GoogleWorkbook.OpenableCopy(broken);
        using var zip = new ZipArchive(fixedStream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry("xl/pivotCache/_rels/pivotCacheDefinition1.xml.rels");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open());
        var xml = reader.ReadToEnd();
        Assert.DoesNotContain("externalLinkPath", xml);
        Assert.Contains("<Relationships", xml);
    }

    [Fact]
    public void Opens_a_google_export_that_closedxml_would_reject()
    {
        var dump = Path.Combine(Path.GetTempPath(), "logisys-create-sheet.xlsx");
        if (!File.Exists(dump))
        {
            return;
        }

        using var stream = File.OpenRead(dump);
        var rows = ClientSheetReader.Read(stream);
        Assert.NotEmpty(rows);
        Assert.Contains(rows, row => row.Cells.ContainsKey("W"));
    }

    private static MemoryStream BuildWorkbookWithBrokenPivotRels()
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                </Types>
                """);
            Write(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            Write(zip, "xl/workbook.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Form Responses 1" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            Write(zip, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """);
            Write(zip, "xl/worksheets/sheet1.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData/>
                </worksheet>
                """);
            Write(zip, "xl/pivotCache/_rels/pivotCacheDefinition1.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/externalLinkPath" TargetMode="External"/>
                </Relationships>
                """);
        }

        stream.Position = 0;
        return stream;
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
