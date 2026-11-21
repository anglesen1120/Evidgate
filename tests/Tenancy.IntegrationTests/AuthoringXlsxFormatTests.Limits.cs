using System.Text;
using System.Xml;
using Evidgate.Modules.Authoring.Application.Import;
using Evidgate.Modules.Authoring.Application.Import.Formats;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class AuthoringXlsxFormatTests
{
    [Fact]
    public void Xlsx_CellOver64KiB_Refused()
    {
        // The cell cap is the CSV one, measured on the text the file carries: a workbook whose description cell is one
        // character past it is refused at that column, before the value is unescaped or composed.
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
        sheet.Append(InlineCell(6, 2, new string('d', ImportText.MaxCellChars + 1)));
        sheet.Append("</row>");

        sheet.Append("</sheetData></worksheet>");

        var parse = XlsxFormat.Read(BuildWorkbook(sheet.ToString()), Parsing, TestContext.Current.CancellationToken);

        parse.TooLarge.ShouldBeFalse();
        parse.Rows.ShouldBeEmpty();
        parse.Issues.ShouldBe([new ImportIssue(1, "/description", "value_invalid")]);
    }

    [Fact]
    public void Xlsx_OneOversizedTextNode_Capped()
    {
        // A cell's value can arrive as one text node of any size, so the reader bounds the append itself rather than
        // trusting the node count: a 4 MiB node becomes at most the cell budget plus one character, the row is refused
        // at the column, and the walk never materialises the rest of the part's text (AC16).
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
        sheet.Append("<c r=\"G2\" t=\"inlineStr\"><is><t>");
        sheet.Append(new string('x', 4 * 1024 * 1024));
        sheet.Append("</t></is></c>");
        sheet.Append("</row>");

        sheet.Append("</sheetData></worksheet>");

        var parse = XlsxFormat.Read(BuildWorkbook(sheet.ToString()), Parsing, TestContext.Current.CancellationToken);

        parse.TooLarge.ShouldBeFalse();
        parse.Rows.ShouldBeEmpty();
        parse.Issues.ShouldBe([new ImportIssue(1, "/description", "value_invalid")]);

        // The refusal above fires with or without the truncation, so the bound itself is asserted here (delta R3): one
        // text node of any size leaves at most the budget plus the one character the caller compares against.
        using var node = XmlReader.Create(new StringReader("<t>" + new string('x', 200_000) + "</t>"));
        node.MoveToContent();

        var bounded = XlsxReader.ReadElementText(node, maxChars: 64);

        bounded.Length.ShouldBe(65);
        bounded.ShouldBe(new string('x', 65));
    }

    [Fact]
    public void Xlsx_BlankRowFlood_Refused()
    {
        // A worksheet of empty rows is about twelve bytes per row, so a 32 MiB part holds millions of them before the
        // data-row cap could fire: the skipped rows are counted and the file is the 413 arm once it passes the row cap.
        static string Sheet(int blankRows)
        {
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

            for (var row = 2; row <= blankRows + 1; row++)
            {
                sheet.Append(Element("row", row)).Append("</row>");
            }

            sheet.Append("</sheetData></worksheet>");

            return sheet.ToString();
        }

        var accepted = XlsxFormat.Read(
            BuildWorkbook(Sheet(ImportLimits.MaxDataRows)),
            Parsing,
            TestContext.Current.CancellationToken);

        accepted.TooLarge.ShouldBeFalse("blank rows under the cap are skipped, not refused");
        accepted.Rows.ShouldBeEmpty();

        var refused = XlsxFormat.Read(
            BuildWorkbook(Sheet(ImportLimits.MaxDataRows + 1)),
            Parsing,
            TestContext.Current.CancellationToken);

        refused.TooLarge.ShouldBeTrue("a worksheet of blank rows past the cap is the 413 arm");
        refused.Rows.ShouldBeEmpty();
        refused.Issues.ShouldBeEmpty();
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
}
