using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Evidgate.Modules.Authoring.Application.Import;
using Evidgate.Modules.Authoring.Application.Import.Formats;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T5.2 (AC13, AC14, AC16): the XLSX carrier on its own — the SpreadsheetML package this module writes and
/// the workbook shapes it reads. Every case drives the real 06.3.1 parser profile (<see cref="ParsingService"/>), so
/// the archive caps, the name normalisation and the DTD prohibition of the profile are exercised, never a stub.
/// </summary>
/// <remarks>
/// No database: the format is pure bytes in and rows out, so these cases run in the ordinary suite without a
/// container. The hostile half — the zip bomb, the entry flood, the DTD, the macro part and the traversal name —
/// lives in <c>AuthoringImportHardeningTests.Xlsx.cs</c> over the corpus in <c>fixtures/hostile/authoring/</c>.
/// </remarks>
public sealed class AuthoringXlsxFormatTests
{
    /// <summary>The SpreadsheetML main namespace the hand-built workbooks of this class use.</summary>
    private const string MainNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>The officeDocument relationship namespace, for the <c>r:id</c> of a sheet element.</summary>
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>The OPC relationships namespace.</summary>
    private const string PackageRelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>The OPC content-types namespace.</summary>
    private const string ContentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";

    /// <summary>The profile every case parses through — the real Evidence implementation, not a fake.</summary>
    private static readonly IParsing Parsing = new ParsingService();

    [Fact]
    public async Task Xlsx_RoundTrip_RowsEqual()
    {
        var rows = new[]
        {
            Row(1, ImportRowKind.Node, "n1", nodeKind: "module", title: "Checkout", position: 1),
            Row(2, ImportRowKind.Node, "n2", parentKey: "n1", nodeKind: "feature", title: "Thanh to\u00e1n th\u1ebb", position: 2),
            Row(
                3,
                ImportRowKind.Item,
                "i1",
                parentKey: "n2",
                title: "Payment card ",
                description: "line one\nline two",
                priority: "high",
                state: "draft",
                position: 1),
            Row(
                4,
                ImportRowKind.Case,
                "c1",
                itemKey: "i1",
                title: "=SUM(A1:A2)",
                description: "  leading and trailing  ",
                precondition: "control \u0001 char",
                stepsJson: "{\"steps\":[{\"no\":1,\"action\":\"Enter a card\",\"expected\":\"Refused\"}]}",
                priority: "high",
                state: "draft",
                code: "SHO-1"),
            Row(
                5,
                ImportRowKind.Case,
                "c2",
                itemKey: "i1",
                title: "'hello",
                description: "literal _x0041_ text",
                stepsJson: "{\"steps\":[{\"no\":1,\"action\":\"@mention\",\"expected\":\"ok\"}]}",
                priority: "low",
                state: "draft",
                code: "SHO-2"),
        };

        var bytes = await WriteAsync(rows);
        var parse = XlsxFormat.Read(bytes, Parsing, TestContext.Current.CancellationToken);

        parse.TooLarge.ShouldBeFalse();
        parse.Issues.ShouldBeEmpty();
        parse.Rows.ShouldBe(rows, "every value the writer carried comes back unchanged");
    }

    [Fact]
    public async Task Xlsx_RoundTrip_ComposesNfc()
    {
        // AC14 composes both ways: a decomposed title is written NFC into the sheet and read back NFC, so the file
        // carries only composed text whichever way the tree reached the writer.
        var decomposed = "Cafe\u0301 au lait";
        var composed = "Caf\u00e9 au lait";

        var bytes = await WriteAsync([Row(1, ImportRowKind.Node, "n1", nodeKind: "module", title: decomposed)]);

        using (var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
        {
            var entry = archive.GetEntry(XlsxFormat.WorksheetPart).ShouldNotBeNull("the sheet is one package part");
            await using var stream = await entry.OpenAsync(TestContext.Current.CancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sheet = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

            sheet.ShouldContain(composed, Case.Sensitive);
            sheet.ShouldNotContain(decomposed, Case.Sensitive);
        }

        var parse = XlsxFormat.Read(bytes, Parsing, TestContext.Current.CancellationToken);

        parse.Issues.ShouldBeEmpty();
        parse.Rows.ShouldHaveSingleItem().Title.ShouldBe(composed);
    }

    [Fact]
    public async Task Xlsx_RoundTrip_EscapesXmlProhibitedNoncharacters()
    {
        // U+FFFE and U+FFFF are storable authoring text but XML 1.0 cannot carry them, so the writer emits the
        // _xHHHH_ form Excel itself uses and the reader turns it back: a title the JSON carrier accepts exports and
        // re-imports instead of failing the export with an XmlWriter character check (AC14/AC16).
        var title = "end \uFFFF and \uFFFE and \u0001";

        var bytes = await WriteAsync([Row(1, ImportRowKind.Node, "n1", nodeKind: "module", title: title)]);

        using (var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
        {
            var entry = archive.GetEntry(XlsxFormat.WorksheetPart).ShouldNotBeNull("the sheet is one package part");
            await using var stream = await entry.OpenAsync(TestContext.Current.CancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sheet = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

            sheet.ShouldNotContain("\uFFFF", Case.Sensitive);
            sheet.ShouldContain("_xFFFF_", Case.Sensitive);
            sheet.ShouldContain("_xFFFE_", Case.Sensitive);
        }

        var parse = XlsxFormat.Read(bytes, Parsing, TestContext.Current.CancellationToken);

        parse.Issues.ShouldBeEmpty();
        parse.Rows.ShouldHaveSingleItem().Title.ShouldBe(title);
    }

    [Fact]
    public async Task Xlsx_Export_HasStandardParts()
    {
        var bytes = await WriteAsync([Row(1, ImportRowKind.Node, "n1", nodeKind: "module", title: "Root")]);

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        archive.Entries.Select(entry => entry.FullName).ToList().ShouldBe(
            [
                "[Content_Types].xml",
                "_rels/.rels",
                "xl/workbook.xml",
                "xl/_rels/workbook.xml.rels",
                "xl/worksheets/sheet1.xml",
            ],
            Case.Sensitive,
            "the package holds exactly the five parts the format declares, in order");

        var contentTypes = ReadXml(archive, "[Content_Types].xml");
        var typesRoot = contentTypes.Root.ShouldNotBeNull("the content types are one document element");
        typesRoot.Name.LocalName.ShouldBe("Types");
        typesRoot.Name.NamespaceName.ShouldBe(ContentTypesNamespace);

        var defaults = typesRoot.Elements().Where(element => element.Name.LocalName == "Default").ToList();
        defaults.Select(element => (string?)element.Attribute("Extension")).ShouldContain("rels");
        defaults.Select(element => (string?)element.Attribute("Extension")).ShouldContain("xml");

        var overrides = typesRoot.Elements()
            .Where(element => element.Name.LocalName == "Override")
            .ToDictionary(element => (string?)element.Attribute("PartName") ?? string.Empty);

        overrides.Keys.ShouldContain("/xl/workbook.xml");
        overrides.Keys.ShouldContain("/xl/worksheets/sheet1.xml");
        ((string?)overrides["/xl/workbook.xml"].Attribute("ContentType")).ShouldBe(XlsxFormat.WorkbookContentType);
        ((string?)overrides["/xl/worksheets/sheet1.xml"].Attribute("ContentType")).ShouldBe(XlsxFormat.WorksheetContentType);

        var packageRelationships = ReadXml(archive, "_rels/.rels");
        var officeDocument = packageRelationships.Root!.Elements()
            .Single(element => element.Name.LocalName == "Relationship");
        ((string?)officeDocument.Attribute("Type")).ShouldBe(XlsxFormat.OfficeDocumentRelationshipType);
        ((string?)officeDocument.Attribute("Target")).ShouldBe("xl/workbook.xml");

        var workbook = ReadXml(archive, "xl/workbook.xml");
        var sheet = workbook.Root!.Elements()
            .Single(element => element.Name.LocalName == "sheets")
            .Elements()
            .Single(element => element.Name.LocalName == "sheet");
        ((string?)sheet.Attribute("name")).ShouldBe(XlsxFormat.SheetName);
        var relationshipId = sheet.Attributes()
            .Single(attribute => attribute.Name.LocalName == "id" && attribute.Name.NamespaceName == RelationshipsNamespace)
            .Value;
        relationshipId.ShouldNotBeNullOrWhiteSpace();

        var workbookRelationships = ReadXml(archive, "xl/_rels/workbook.xml.rels");
        var worksheetRelationship = workbookRelationships.Root!.Elements()
            .Single(element => element.Name.LocalName == "Relationship"
                && string.Equals((string?)element.Attribute("Id"), relationshipId, StringComparison.Ordinal));
        ((string?)worksheetRelationship.Attribute("Type")).ShouldBe(XlsxFormat.WorksheetRelationshipType);
        ((string?)worksheetRelationship.Attribute("Target")).ShouldBe("worksheets/sheet1.xml");
    }

    [Fact]
    public async Task Xlsx_Export_Twice_ByteIdentical()
    {
        var rows = new[]
        {
            Row(1, ImportRowKind.Node, "n1", nodeKind: "module", title: "Checkout", position: 1),
            Row(2, ImportRowKind.Item, "i1", parentKey: "n1", title: "Payment card", priority: "high", state: "draft", position: 1),
            Row(
                3,
                ImportRowKind.Case,
                "c1",
                itemKey: "i1",
                title: "Declined card",
                stepsJson: "{\"steps\":[{\"no\":1,\"action\":\"Pay\",\"expected\":\"Refused\"}]}",
                priority: "high",
                state: "draft",
                code: "SHO-1"),
        };

        var first = await WriteAsync(rows);
        var second = await WriteAsync(rows);

        second.SequenceEqual(first).ShouldBeTrue("two exports of one unchanged tree are byte-identical");
    }

    [Fact]
    public void Xlsx_ExternalXlsxFixtureWithSharedStrings_Read()
    {
        // A workbook as Excel writes it: the header and every text cell address the shared-string table by index, one
        // cell uses the t="str" form and the position column is numeric.
        var sharedStrings = new[]
        {
            // 0..12 — the header row.
            "kind", "key", "parent_key", "item_key", "node_kind", "title", "description", "precondition", "steps",
            "priority", "state", "code", "position",
            // 13.. — the values.
            "node", "n1", "module", "Checkout",
            "item", "i1", "Payment card", "high", "draft",
            "case", "c1", "{\"steps\":[{\"no\":1,\"action\":\"Pay\",\"expected\":\"Refused\"}]}", "SHO-1",
        };

        var sheet = new StringBuilder();
        sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sheet.Append(Element("worksheet"));
        sheet.Append("<sheetData>");

        sheet.Append("<row r=\"1\">");
        for (var column = 0; column < 13; column++)
        {
            sheet.Append(SharedStringCell(column, 1, column));
        }

        sheet.Append("</row>");

        sheet.Append("<row r=\"2\">");
        sheet.Append(SharedStringCell(0, 2, 13));
        sheet.Append(SharedStringCell(1, 2, 14));
        sheet.Append(SharedStringCell(4, 2, 15));
        sheet.Append(SharedStringCell(5, 2, 16));
        sheet.Append(NumberCell(12, 2, 1));
        sheet.Append("</row>");

        sheet.Append("<row r=\"3\">");
        sheet.Append(SharedStringCell(0, 3, 17));
        sheet.Append(SharedStringCell(1, 3, 18));
        sheet.Append(SharedStringCell(2, 3, 14));
        sheet.Append(SharedStringCell(5, 3, 19));
        sheet.Append(SharedStringCell(9, 3, 20));
        sheet.Append(SharedStringCell(10, 3, 21));
        sheet.Append(NumberCell(12, 3, 1));
        sheet.Append("</row>");

        sheet.Append("<row r=\"4\">");
        sheet.Append(SharedStringCell(0, 4, 22));
        sheet.Append(SharedStringCell(1, 4, 23));
        sheet.Append(SharedStringCell(3, 4, 18));
        sheet.Append(StrCell(5, 4, "Declined card"));
        sheet.Append(SharedStringCell(8, 4, 24));
        sheet.Append(SharedStringCell(9, 4, 20));
        sheet.Append(SharedStringCell(11, 4, 25));
        sheet.Append("</row>");

        sheet.Append("</sheetData></worksheet>");

        var bytes = BuildWorkbook(sheet.ToString(), SharedStrings(sharedStrings));
        var parse = XlsxFormat.Read(bytes, Parsing, TestContext.Current.CancellationToken);

        parse.Issues.ShouldBeEmpty();
        parse.Rows.ShouldBe(new[]
        {
            Row(1, ImportRowKind.Node, "n1", nodeKind: "module", title: "Checkout", position: 1),
            Row(
                2,
                ImportRowKind.Item,
                "i1",
                parentKey: "n1",
                title: "Payment card",
                priority: "high",
                state: "draft",
                position: 1),
            Row(
                3,
                ImportRowKind.Case,
                "c1",
                itemKey: "i1",
                title: "Declined card",
                stepsJson: "{\"steps\":[{\"no\":1,\"action\":\"Pay\",\"expected\":\"Refused\"}]}",
                priority: "high",
                code: "SHO-1"),
        });
    }

    [Fact]
    public void Xlsx_RepeatedLargeSharedString_IsCapped()
    {
        // One 64 KiB shared string referenced by every cell of seven rows: a few-kilobyte file would otherwise ask for
        // six megabytes of JSON, and the 5 001-row limit alone would allow thirteen times the body budget (AC16). The
        // string is pseudo-random so the archive stays under the 20:1 ratio guard and only the document cap can fire.
        var seed = 0x2F6E2B1u;
        var big = new StringBuilder(ImportText.MaxCellChars);

        for (var index = 0; index < ImportText.MaxCellChars; index++)
        {
            seed = (seed * 1_664_525u) + 1_013_904_223u;
            big.Append((char)('a' + (int)(seed >> 24) % 26));
        }

        var sharedStrings = new List<string>(XlsxFormat.ColumnNames) { big.ToString() };

        var bigIndex = XlsxFormat.ColumnNames.Length;

        var sheet = new StringBuilder();
        sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sheet.Append(Element("worksheet"));
        sheet.Append("<sheetData>");

        sheet.Append("<row r=\"1\">");
        for (var column = 0; column < XlsxFormat.ColumnCount; column++)
        {
            sheet.Append(SharedStringCell(column, 1, column));
        }

        sheet.Append("</row>");

        for (var row = 2; row <= 8; row++)
        {
            sheet.Append(Element("row", row));
            for (var column = 0; column < XlsxFormat.ColumnCount; column++)
            {
                sheet.Append(SharedStringCell(column, row, bigIndex));
            }

            sheet.Append("</row>");
        }

        sheet.Append("</sheetData></worksheet>");

        var parse = XlsxFormat.Read(
            BuildWorkbook(sheet.ToString(), SharedStrings(sharedStrings)),
            Parsing,
            TestContext.Current.CancellationToken);

        parse.TooLarge.ShouldBeTrue("the document the reader builds is capped at the body budget");
        parse.Rows.ShouldBeEmpty();
        parse.Issues.ShouldBeEmpty();
    }

    [Fact]
    public void Xlsx_Over5000Rows_Large()
    {
        var sheet = new StringBuilder();
        sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sheet.Append(Element("worksheet"));
        sheet.Append("<sheetData>");

        sheet.Append("<row r=\"1\">");
        for (var column = 0; column < XlsxFormat.ColumnNames.Length; column++)
        {
            sheet.Append(InlineCell(column, 1, XlsxFormat.ColumnNames[column]));
        }

        sheet.Append("</row>");

        // 5 001 data rows: one over ImportLimits.MaxDataRows, which is the 413 arm, not a per-row refusal.
        for (var row = 2; row <= 5002; row++)
        {
            sheet.Append(Element("row", row));
            sheet.Append(InlineCell(0, row, "node"));
            sheet.Append(InlineCell(1, row, "n" + row.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            sheet.Append("</row>");
        }

        sheet.Append("</sheetData></worksheet>");

        var parse = XlsxFormat.Read(
            BuildWorkbook(sheet.ToString()),
            Parsing,
            TestContext.Current.CancellationToken);

        parse.TooLarge.ShouldBeTrue("a file over the row cap is the 413 arm");
        parse.Rows.ShouldBeEmpty();
        parse.Issues.ShouldBeEmpty();
    }

    [Fact]
    public void Xlsx_RelTargetEscapesXl_Refused()
    {
        var sheet = new StringBuilder();
        sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sheet.Append(Element("worksheet"));
        sheet.Append("<sheetData><row r=\"1\">");
        sheet.Append(InlineCell(0, 1, XlsxFormat.ColumnNames[0]));
        sheet.Append("</row></sheetData></worksheet>");

        var bytes = BuildWorkbook(sheet.ToString(), workbookRelsTarget: "../../evil/sheet1.xml");
        var parse = XlsxFormat.Read(bytes, Parsing, TestContext.Current.CancellationToken);

        var issue = parse.Issues.ShouldHaveSingleItem();
        issue.Index.ShouldBeNull("a relationship that escapes xl/ is a file-level refusal");
        issue.Path.ShouldBe("/file");
        issue.Reason.ShouldBe("unsupported_format");
    }

    [Fact]
    public void Xlsx_MissingRequiredColumn_IsRowIndexed()
    {
        // The reader builds the same contract document a JSON body is, so a row the schema refuses is reported at that
        // row's index and a fixed pointer: the sheet's own structure never reaches the report (00 §5.7 S3).
        var sheet = new StringBuilder();
        sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sheet.Append(Element("worksheet"));
        sheet.Append("<sheetData>");

        sheet.Append("<row r=\"1\">");
        for (var column = 0; column < XlsxFormat.ColumnNames.Length; column++)
        {
            sheet.Append(InlineCell(column, 1, XlsxFormat.ColumnNames[column]));
        }

        sheet.Append("</row>");

        // A node row without its node_kind: the schema's own required list refuses it, exactly as it would a JSON body.
        sheet.Append(Element("row", 2));
        sheet.Append(InlineCell(0, 2, "node"));
        sheet.Append(InlineCell(1, 2, "n1"));
        sheet.Append(InlineCell(5, 2, "Checkout"));
        sheet.Append("</row>");

        sheet.Append("</sheetData></worksheet>");

        var parse = XlsxFormat.Read(BuildWorkbook(sheet.ToString()), Parsing, TestContext.Current.CancellationToken);

        parse.TooLarge.ShouldBeFalse();
        parse.Rows.ShouldBeEmpty();

        // The schema reports the row it refused; the pointer is the row or the required column, never the sheet.
        parse.Issues.ShouldNotBeEmpty("the row the contract's own required list refuses is reported");
        parse.Issues.ShouldAllBe(issue => issue.Index == 1);
        parse.Issues.ShouldAllBe(issue => issue.Path == "/row" || issue.Path == "/node_kind");
        parse.Issues.ShouldAllBe(issue => issue.Reason == "value_invalid");
    }

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
