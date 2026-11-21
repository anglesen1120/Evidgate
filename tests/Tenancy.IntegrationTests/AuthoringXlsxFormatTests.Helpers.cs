using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Evidgate.Modules.Authoring.Application.Import;
using Evidgate.Modules.Authoring.Application.Import.Formats;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class AuthoringXlsxFormatTests
{
    /// <summary>Writes the rows and answers the package bytes.</summary>
    /// <param name="rows">The rows to carry.</param>
    /// <returns>The XLSX file's bytes.</returns>
    private static async Task<byte[]> WriteAsync(IReadOnlyList<ImportRow> rows)
    {
        using var buffer = new MemoryStream();
        await XlsxFormat.WriteAsync(rows, buffer, TestContext.Current.CancellationToken);

        return buffer.ToArray();
    }

    /// <summary>Builds one table row; every member the caller leaves out is an absent column.</summary>
    /// <param name="index">The row's 1-based data position.</param>
    /// <param name="kind">The row's kind.</param>
    /// <param name="key">The row's file-local key.</param>
    /// <param name="parentKey">The parent node's key or the owning node's key.</param>
    /// <param name="itemKey">The owning item's key.</param>
    /// <param name="nodeKind">The node kind.</param>
    /// <param name="title">The title.</param>
    /// <param name="description">The description.</param>
    /// <param name="precondition">The precondition.</param>
    /// <param name="stepsJson">The steps document as JSON text.</param>
    /// <param name="priority">The priority.</param>
    /// <param name="state">The state.</param>
    /// <param name="code">The case code.</param>
    /// <param name="position">The sibling position.</param>
    /// <returns>The row.</returns>
    private static ImportRow Row(
        int index,
        ImportRowKind kind,
        string key,
        string? parentKey = null,
        string? itemKey = null,
        string? nodeKind = null,
        string? title = null,
        string? description = null,
        string? precondition = null,
        string? stepsJson = null,
        string? priority = null,
        string? state = null,
        string? code = null,
        int? position = null) =>
        new(index, kind, key, parentKey, itemKey, nodeKind, title, description, precondition, stepsJson, priority, state, code, position);

    /// <summary>Hand-builds a workbook package with the parts a reader needs, as Excel would emit them.</summary>
    /// <param name="sheetXml">The <c>xl/worksheets/sheet1.xml</c> document.</param>
    /// <param name="sharedStringsXml">The optional <c>xl/sharedStrings.xml</c> document.</param>
    /// <param name="workbookRelsTarget">The worksheet target the workbook relationships declare.</param>
    /// <returns>The package bytes.</returns>
    private static byte[] BuildWorkbook(
        string sheetXml,
        string? sharedStringsXml = null,
        string workbookRelsTarget = "worksheets/sheet1.xml")
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var contentTypes = new StringBuilder();
            contentTypes.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            contentTypes.Append("<Types xmlns=\"").Append(ContentTypesNamespace).Append("\">");
            contentTypes.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            contentTypes.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            contentTypes.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"").Append(XlsxFormat.WorkbookContentType).Append("\"/>");
            contentTypes.Append("<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"").Append(XlsxFormat.WorksheetContentType).Append("\"/>");

            if (sharedStringsXml is not null)
            {
                contentTypes.Append("<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>");
            }

            contentTypes.Append("</Types>");

            Add(archive, "[Content_Types].xml", contentTypes.ToString());

            Add(archive, "_rels/.rels", string.Concat(
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>",
                "<Relationships xmlns=\"", PackageRelationshipsNamespace, "\">",
                "<Relationship Id=\"rId1\" Type=\"", XlsxFormat.OfficeDocumentRelationshipType, "\" Target=\"xl/workbook.xml\"/>",
                "</Relationships>"));

            Add(archive, "xl/workbook.xml", string.Concat(
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>",
                "<workbook xmlns=\"", MainNamespace, "\" xmlns:r=\"", RelationshipsNamespace, "\">",
                "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets>",
                "</workbook>"));

            Add(archive, "xl/_rels/workbook.xml.rels", string.Concat(
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>",
                "<Relationships xmlns=\"", PackageRelationshipsNamespace, "\">",
                "<Relationship Id=\"rId1\" Type=\"", XlsxFormat.WorksheetRelationshipType, "\" Target=\"",
                workbookRelsTarget, "\"/>",
                "</Relationships>"));

            Add(archive, "xl/worksheets/sheet1.xml", sheetXml);

            if (sharedStringsXml is not null)
            {
                Add(archive, "xl/sharedStrings.xml", sharedStringsXml);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Writes one part into the package being built.</summary>
    /// <param name="archive">The archive.</param>
    /// <param name="name">The part name.</param>
    /// <param name="content">The part's text.</param>
    private static void Add(ZipArchive archive, string name, string content)
    {
        // Stored, not deflated: the 5 001-row sheet of Xlsx_Over5000Rows_Large is highly repetitive, and a compressed
        // copy of it would trip the profile's 20:1 ratio guard before the row cap the case is about could fire. The
        // guard itself is proven by fixtures/hostile/authoring/xlsx_ratio_bomb.xlsx, not by this helper.
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        writer.Write(content);
    }

    /// <summary>Wraps a shared-string table around its items.</summary>
    /// <param name="strings">The items, in index order.</param>
    /// <returns>The <c>xl/sharedStrings.xml</c> document.</returns>
    private static string SharedStrings(IReadOnlyList<string> strings)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        builder.Append(string.Concat("<sst xmlns=\"", MainNamespace, "\" count=\"", strings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "\" uniqueCount=\"", strings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "\">"));

        foreach (var item in strings)
        {
            builder.Append("<si><t xml:space=\"preserve\">").Append(Escape(item)).Append("</t></si>");
        }

        builder.Append("</sst>");

        return builder.ToString();
    }

    /// <summary>One worksheet element opened with the main namespace.</summary>
    /// <param name="name">The element name.</param>
    /// <param name="row">The row number for a <c>row</c> element, otherwise ignored.</param>
    /// <returns>The start tag.</returns>
    private static string Element(string name, int row = 0) => row == 0
        ? string.Concat("<", name, " xmlns=\"", MainNamespace, "\">")
        : string.Concat("<", name, " r=\"", row.ToString(System.Globalization.CultureInfo.InvariantCulture), "\">");

    /// <summary>One inline-string cell.</summary>
    /// <param name="column">The zero-based column.</param>
    /// <param name="row">The row number.</param>
    /// <param name="value">The cell's text.</param>
    /// <returns>The cell XML.</returns>
    private static string InlineCell(int column, int row, string value) => string.Concat(
        "<c r=\"",
        Reference(column, row),
        "\" t=\"inlineStr\"><is><t>",
        Escape(value),
        "</t></is></c>");

    /// <summary>One shared-string cell.</summary>
    /// <param name="column">The zero-based column.</param>
    /// <param name="row">The row number.</param>
    /// <param name="index">The shared-string index.</param>
    /// <returns>The cell XML.</returns>
    private static string SharedStringCell(int column, int row, int index) => string.Concat(
        "<c r=\"",
        Reference(column, row),
        "\" t=\"s\"><v>",
        index.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "</v></c>");

    /// <summary>One <c>t="str"</c> cell.</summary>
    /// <param name="column">The zero-based column.</param>
    /// <param name="row">The row number.</param>
    /// <param name="value">The cell's text.</param>
    /// <returns>The cell XML.</returns>
    private static string StrCell(int column, int row, string value) => string.Concat(
        "<c r=\"",
        Reference(column, row),
        "\" t=\"str\"><v>",
        Escape(value),
        "</v></c>");

    /// <summary>One numeric cell.</summary>
    /// <param name="column">The zero-based column.</param>
    /// <param name="row">The row number.</param>
    /// <param name="value">The number.</param>
    /// <returns>The cell XML.</returns>
    private static string NumberCell(int column, int row, int value) => string.Concat(
        "<c r=\"",
        Reference(column, row),
        "\" t=\"n\"><v>",
        value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "</v></c>");

    /// <summary>The A1-style reference of one cell.</summary>
    /// <param name="column">The zero-based column.</param>
    /// <param name="row">The row number.</param>
    /// <returns>The reference.</returns>
    private static string Reference(int column, int row) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{(char)('A' + column)}{row}");

    /// <summary>Escapes text for XML content.</summary>
    /// <param name="value">The text.</param>
    /// <returns>The escaped text.</returns>
    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>Loads one part of the archive as a document (the test half of the assertion, not product code).</summary>
    /// <param name="archive">The archive.</param>
    /// <param name="name">The part name.</param>
    /// <returns>The parsed part.</returns>
    private static XDocument ReadXml(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name).ShouldNotBeNull($"{name} is one of the package parts");

        using var stream = entry.Open();

        return XDocument.Load(stream);
    }
}
