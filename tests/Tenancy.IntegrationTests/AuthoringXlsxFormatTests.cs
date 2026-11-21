using System.IO.Compression;
using System.Text;
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
public sealed partial class AuthoringXlsxFormatTests
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
                code: "SHO-001"),
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
                code: "SHO-002"),
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
        // Two different things live behind the _xHHHH_ escape, and the product answers them differently:
        //  - U+0001 is a control character XML 1.0 cannot carry but the normaliser accepts, so it is escaped on export
        //    and comes back as itself: the row round-trips.
        //  - U+FFFE and U+FFFF are noncharacters the process's normaliser refuses, so this module cannot store them
        //    composed (AC14). The export must still not throw — it carries them escaped and keeps the file readable —
        //    and the re-import refuses the cell as invalid_text, exactly as a NUL or a lone surrogate is refused today.
        var control = "control \u0001 char";
        var noncharacters = "end \uFFFF and \uFFFE";

        var exported = await WriteAsync(
        [
            Row(1, ImportRowKind.Node, "n1", nodeKind: "module", title: control),
            Row(2, ImportRowKind.Node, "n2", nodeKind: "module", title: noncharacters),
        ]);

        using (var archive = new ZipArchive(new MemoryStream(exported), ZipArchiveMode.Read))
        {
            var entry = archive.GetEntry(XlsxFormat.WorksheetPart).ShouldNotBeNull("the sheet is one package part");
            await using var stream = await entry.OpenAsync(TestContext.Current.CancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sheet = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

            sheet.ShouldNotContain("\uFFFF", Case.Sensitive);
            sheet.ShouldNotContain("\uFFFE", Case.Sensitive);
            sheet.ShouldContain("_xFFFF_", Case.Sensitive);
            sheet.ShouldContain("_xFFFE_", Case.Sensitive);
            sheet.ShouldContain("_x0001_", Case.Sensitive);
        }

        // The re-import refuses the row that carries a noncharacter and names the column, never the value; the control
        // character's row is untouched by that refusal.
        var parse = XlsxFormat.Read(exported, Parsing, TestContext.Current.CancellationToken);

        parse.TooLarge.ShouldBeFalse();
        parse.Issues.ShouldBe([new ImportIssue(2, "/title", "invalid_text")]);

        // The same file without the noncharacter row is accepted, with U+0001 preserved exactly.
        var accepted = XlsxFormat.Read(
            await WriteAsync([Row(1, ImportRowKind.Node, "n1", nodeKind: "module", title: control)]),
            Parsing,
            TestContext.Current.CancellationToken);

        accepted.Issues.ShouldBeEmpty();
        accepted.Rows.ShouldHaveSingleItem().Title.ShouldBe(control);
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
                code: "SHO-001"),
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
            "case", "c1", "{\"steps\":[{\"no\":1,\"action\":\"Pay\",\"expected\":\"Refused\"}]}", "SHO-001",
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
                code: "SHO-001"),
        });
    }

    [Fact]
    public void Xlsx_RepeatedLargeSharedString_IsCapped()
    {
        // One 64 KiB shared string referenced by the description cell of 400 rows: a few-hundred-kilobyte workbook
        // would otherwise ask for 26 MiB of JSON, because the 5 001-row limit alone allows thirteen cells per row of
        // that size (AC16). Each row is a valid item row, so no row rule answers before the document cap does. The string is pseudo-random so the archive stays under the 20:1 ratio guard, and every row is a valid
        // node row, so the only guard that can answer is the document cap the reader keeps while it builds.
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

        for (var row = 2; row <= 401; row++)
        {
            sheet.Append(Element("row", row));
            sheet.Append(InlineCell(0, row, "item"));
            sheet.Append(InlineCell(1, row, "i" + row.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            sheet.Append(InlineCell(2, row, "n1"));
            sheet.Append(InlineCell(5, row, "Row " + row.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            sheet.Append(SharedStringCell(6, row, bigIndex));
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
    public void Xlsx_CellPastTheLastColumn_NotMaterialised()
    {
        // A cell past the thirteenth column can never enter the table, so the reader refuses the row and consumes the
        // cell without collecting its text: a file whose only payload is 4 MiB in column T is refused without that text
        // ever becoming a string (AC16).
        var sheet = new StringBuilder();
        sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sheet.Append(Element("worksheet"));
        sheet.Append("<sheetData>");

        sheet.Append("<row r=\"1\">");
        for (var column = 0; column < XlsxFormat.ColumnCount; column++)
        {
            sheet.Append(InlineCell(column, 1, XlsxFormat.ColumnNames[column]));
        }

        sheet.Append("</row>");

        sheet.Append(Element("row", 2));
        sheet.Append(InlineCell(0, 2, "node"));
        sheet.Append(InlineCell(1, 2, "n1"));
        sheet.Append(InlineCell(4, 2, "module"));
        sheet.Append(InlineCell(5, 2, "Checkout"));
        sheet.Append(InlineCell(20, 2, new string('t', 4 * 1024 * 1024)));
        sheet.Append("</row>");

        sheet.Append("</sheetData></worksheet>");

        var parse = XlsxFormat.Read(BuildWorkbook(sheet.ToString()), Parsing, TestContext.Current.CancellationToken);

        parse.TooLarge.ShouldBeFalse();
        parse.Rows.ShouldBeEmpty();
        parse.Issues.ShouldBe([new ImportIssue(1, "/extra_column", "value_invalid")]);
    }
}
