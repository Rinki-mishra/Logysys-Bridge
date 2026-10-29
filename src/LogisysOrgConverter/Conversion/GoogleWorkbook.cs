using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace LogisysOrgConverter.Conversion;

/// <summary>
/// Google Sheets xlsx exports often include invalid relationship parts
/// (pivot cache links with Type/TargetMode but no Id or Target). ClosedXML rejects those.
/// </summary>
public static class GoogleWorkbook
{
    private static readonly XNamespace Pkg =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    public static Stream OpenableCopy(Stream stream)
    {
        var copy = new MemoryStream();
        stream.CopyTo(copy);
        copy.Position = 0;
        if (!LooksLikeZip(copy))
        {
            copy.Position = 0;
            return copy;
        }

        using (var zip = new ZipArchive(copy, ZipArchiveMode.Update, leaveOpen: true))
        {
            foreach (var entry in zip.Entries.Where(item => item.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                FixRelationships(entry);
            }
        }

        copy.Position = 0;
        return copy;
    }

    private static void FixRelationships(ZipArchiveEntry entry)
    {
        var name = entry.FullName;
        var archive = entry.Archive;
        string xml;
        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: false))
        {
            xml = reader.ReadToEnd();
        }

        if (!xml.Contains("<Relationship", StringComparison.Ordinal))
        {
            return;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch
        {
            return;
        }

        var root = document.Root;
        if (root is null)
        {
            return;
        }

        var changed = false;
        foreach (var relationship in root.Elements(Pkg + "Relationship").ToList())
        {
            var id = (string?)relationship.Attribute("Id");
            var target = (string?)relationship.Attribute("Target");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(target))
            {
                relationship.Remove();
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        entry.Delete();
        var replacement = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(replacement.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        document.Save(writer, SaveOptions.DisableFormatting);
    }

    private static bool LooksLikeZip(Stream stream)
    {
        Span<byte> header = stackalloc byte[2];
        var read = stream.Read(header);
        stream.Position = 0;
        return read == 2 && header[0] == 0x50 && header[1] == 0x4B;
    }
}
