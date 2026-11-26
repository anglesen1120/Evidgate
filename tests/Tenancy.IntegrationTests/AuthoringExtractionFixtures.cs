using System.IO.Compression;
using System.Security;
using System.Text;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Builders for the documents <see cref="AuthoringExtractionSafetyTests"/> feeds the extractor. Every document is
/// generated in memory (no committed binaries), so a hostile shape is visible in the code that builds it.
/// </summary>
internal static class AuthoringExtractionFixtures
{
    public const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public const string RelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    public const string OfficeDocumentType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string DocumentRelationshipsPart = "word/_rels/document.xml.rels";

    private const string ContentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string WordMainContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";

    /// <summary>A minimal valid DOCX whose body holds one paragraph per text (the text is XML-escaped).</summary>
    public static byte[] Docx(params string[] paragraphs) =>
        DocxWithBody(string.Concat(paragraphs.Select(text => $"<w:p><w:r><w:t xml:space=\"preserve\">{SecurityElement.Escape(text)}</w:t></w:r></w:p>")));

    /// <summary>A minimal valid DOCX with the given WordprocessingML body markup and extra parts.</summary>
    public static byte[] DocxWithBody(string bodyXml, params (string Name, byte[] Content)[] extraParts) =>
        Zip(DocxParts(Wrap(bodyXml), WordMainContentType).Concat(extraParts).ToArray());

    /// <summary>A DOCX whose <c>[Content_Types].xml</c> declares the given main content type.</summary>
    public static byte[] DocxWithMainContentType(string contentType, string text) =>
        Zip(DocxParts(Wrap($"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>"), contentType).ToArray());

    /// <summary>A DOCX whose <c>word/document.xml</c> is exactly the given text, for the DTD and entity cases.</summary>
    public static byte[] DocxWithDocumentXml(string documentXml) =>
        Zip(DocxParts(documentXml, WordMainContentType).ToArray());

    /// <summary>A document part that wraps <paramref name="bodyXml"/> in the WordprocessingML root.</summary>
    public static string Wrap(string bodyXml) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"{WordNamespace}\"><w:body>{bodyXml}</w:body></w:document>";

    /// <summary>A footnotes part holding one footnote with the given text.</summary>
    public static (string Name, byte[] Content) Footnotes(string text) =>
        ("word/footnotes.xml", Utf8($"<?xml version=\"1.0\"?><w:footnotes xmlns:w=\"{WordNamespace}\"><w:footnote w:id=\"1\"><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:footnote></w:footnotes>"));

    /// <summary>A relationships part with one external relationship of the given type.</summary>
    public static (string Name, byte[] Content) ExternalRelationship(string relationshipType) =>
        (DocumentRelationshipsPart, Utf8(
            $"<?xml version=\"1.0\"?><Relationships xmlns=\"{RelationshipsNamespace}\">"
            + $"<Relationship Id=\"rId9\" Type=\"{OfficeDocumentType}/{relationshipType}\" Target=\"https://example.invalid/x\" TargetMode=\"External\"/>"
            + "</Relationships>"));

    /// <summary>A DOCX package as a zip of the given parts, deflated so the ratio guard sees real compression.</summary>
    public static byte[] Zip(params (string Name, byte[] Content)[] parts)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in parts)
            {
                using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
                stream.Write(content, 0, content.Length);
            }
        }

        return buffer.ToArray();
    }

    public static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    /// <summary>
    /// A one-font PDF with one page per text. Extra markup goes into the catalog, the page dictionaries or the
    /// trailer; extra objects are numbered from <see cref="FirstExtraObject"/>. Offsets are exact, so the
    /// parser needs no repair pass.
    /// </summary>
    public static byte[] Pdf(
        string[] pageTexts,
        string catalogExtra = "",
        string pageExtra = "",
        string trailerExtra = "",
        string[]? extraObjects = null)
    {
        var objects = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', pageTexts.Select((_, index) => $"{PageObject(index)} 0 R"))}] /Count {pageTexts.Length} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        for (var index = 0; index < pageTexts.Length; index++)
        {
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {PageObject(index) + 1} 0 R {pageExtra} >>");
            var content = $"BT /F1 12 Tf 72 700 Td ({Escape(pageTexts[index])}) Tj ET";
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

        objects.AddRange(extraObjects ?? []);

        return Assemble(objects, trailerExtra);
    }

    /// <summary>The object number the first entry of <c>extraObjects</c> receives for a PDF with <paramref name="pageCount"/> pages.</summary>
    public static int FirstExtraObject(int pageCount) => 4 + (2 * pageCount);

    private static int PageObject(int pageIndex) => 4 + (2 * pageIndex);

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);

    private static byte[] Assemble(List<string> objects, string trailerExtra)
    {
        var file = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();

        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(file.Length);
            file.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }

        var xref = file.Length;
        file.Append("xref\n0 ").Append(objects.Count + 1).Append('\n').Append("0000000000 65535 f \n");

        foreach (var offset in offsets)
        {
            file.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        file.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R ").Append(trailerExtra).Append(" >>\n")
            .Append("startxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(file.ToString());
    }

    private static IEnumerable<(string Name, byte[] Content)> DocxParts(string documentXml, string mainContentType)
    {
        yield return (
            "[Content_Types].xml",
            Utf8(
                $"<?xml version=\"1.0\"?><Types xmlns=\"{ContentTypesNamespace}\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + $"<Override PartName=\"/word/document.xml\" ContentType=\"{mainContentType}\"/></Types>"));
        yield return (
            "_rels/.rels",
            Utf8(
                $"<?xml version=\"1.0\"?><Relationships xmlns=\"{RelationshipsNamespace}\">"
                + $"<Relationship Id=\"rId1\" Type=\"{OfficeDocumentType}/officeDocument\" Target=\"word/document.xml\"/></Relationships>"));
        yield return ("word/document.xml", Utf8(documentXml));
    }
}
