using System.Globalization;
using System.Text;
using Evidgate.Modules.Authoring.Application.Import;
using Evidgate.Modules.Authoring.Application.Import.Formats;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T5 hardening, format by format (the CSV part): what a hostile or merely unusual file does to the readers that
/// the acceptance cases do not walk. Each partial file of this class covers one format, so the XLSX and importer slices
/// add their own without touching this one. No database: a format reader only ever sees bytes.
/// </summary>
public sealed partial class AuthoringImportHardeningTests
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private const string Header =
        "kind,key,parent_key,item_key,node_kind,title,description,precondition,steps,priority,state,code,position";

    /// <summary>
    /// AC16: a cell a spreadsheet would evaluate is written with a leading apostrophe and stored as the literal text
    /// behind it, for every leading character the plan names — and a genuine apostrophe is left alone.
    /// </summary>
    [Fact]
    public async Task Csv_FormulaCellsStoredLiterally()
    {
        foreach (var trigger in new[] { "=", "+", "-", "@", "\t", "\r" })
        {
            var stored = trigger + "cmd";
            var bytes = await WriteCsv([Node(1, "n1", stored)]);

            // The exported cell carries exactly one apostrophe in front of the trigger (`"'\rcmd"` is quoted, because a
            // carriage return is a line break to every CSV reader).
            CsvText(bytes).ShouldContain("'" + trigger + "cmd", Case.Sensitive);

            // Reading that file back stores the value without the apostrophe, and nothing evaluates it on the way.
            var parse = CsvFormat.Read(bytes);
            parse.IsSuccess.ShouldBeTrue(Describe(parse));
            parse.Rows.Single().Title.ShouldBe(stored);
            parse.Rows.Single().Title.ShouldNotStartWith("'", Case.Sensitive);
        }

        // A value that begins with an apostrophe and then a trigger is doubled on export and returns unchanged, so the
        // neutralisation never eats an author's own apostrophe.
        var doubled = await WriteCsv([Node(1, "n1", "'=cmd")]);
        CsvText(doubled).ShouldContain("''=cmd", Case.Sensitive);
        CsvFormat.Read(doubled).Rows.Single().Title.ShouldBe("'=cmd");

        // An apostrophe before ordinary text is genuine text in both directions.
        var genuine = await WriteCsv([Node(1, "n1", "'hello")]);
        CsvText(genuine).ShouldContain(",'hello", Case.Sensitive);
        CsvFormat.Read(genuine).Rows.Single().Title.ShouldBe("'hello");

        // A trigger that is not in the first position is not neutralised at all.
        var anywhere = CsvText(await WriteCsv([Node(1, "n1", "a=b")]));
        anywhere.ShouldContain("a=b", Case.Sensitive);
        anywhere.ShouldNotContain("'a=b", Case.Sensitive);
    }

    /// <summary>
    /// OI-13-116 through a CSV cell: a <c>steps</c> document whose member name cannot be decoded is a 422 with a reason,
    /// never a 500 out of the schema engine. A CSV cell is nested JSON, so it gets the same scan the JSON body gets.
    /// </summary>
    [Fact]
    public void Csv_StepsCellWithUnreadableMemberName_Refused()
    {
        // The cell text is {"steps":[{"\ud800":1}]} — an escaped lone surrogate as a member name inside the steps
        // document — written the way the CSV format escapes a quote.
        var steps = """{"steps":[{"\ud800":1}]}""";
        var cell = "\"" + steps.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        var body = Header
            + "\r\n"
            + string.Join(',', "case", "c1", string.Empty, "i1", string.Empty, "Declined card", string.Empty, string.Empty, cell, "high", "draft", string.Empty, string.Empty)
            + "\r\n";

        var parse = CsvFormat.Read(Encoding.UTF8.GetBytes(body));

        parse.IsSuccess.ShouldBeFalse();
        parse.Issues.ShouldBe([new ImportIssue(1, "/steps", "invalid_text")]);

        // A cell that is real JSON but not a steps document is the other reason, and both name the fixed pointer rather
        // than the text: the value never reaches a report or an exception.
        var notADocument = "\"{\"\"steps\"\":[\"\"a\"\"}]}\"";
        var notADocumentBody = Header
            + "\r\n"
            + string.Join(',', "case", "c1", string.Empty, "i1", string.Empty, "Declined card", string.Empty, string.Empty, notADocument, "high", "draft", string.Empty, string.Empty)
            + "\r\n";

        var refused = CsvFormat.Read(Encoding.UTF8.GetBytes(notADocumentBody));
        refused.IsSuccess.ShouldBeFalse();
        refused.Issues.ShouldNotBeEmpty();
        refused.Issues.ShouldAllBe(
            issue => issue.Index == 1 && (issue.Path == "/steps" || issue.Path == "/row" || issue.Path == "/kind"));
    }

    /// <summary>One module row at the root, positioned first.</summary>
    /// <param name="index">The 1-based row index.</param>
    /// <param name="key">The row key.</param>
    /// <param name="title">The module title.</param>
    /// <returns>The row.</returns>
    private static ImportRow Node(int index, string key, string title) =>
        new(index, ImportRowKind.Node, key, null, null, "module", title, null, null, null, null, null, null, index - 1);

    /// <summary>Writes rows as a CSV body (BOM, CRLF).</summary>
    /// <param name="rows">The rows.</param>
    /// <returns>The body.</returns>
    private static async Task<byte[]> WriteCsv(IReadOnlyList<ImportRow> rows)
    {
        using var stream = new MemoryStream();
        await CsvFormat.WriteAsync(rows, stream);
        return stream.ToArray();
    }

    /// <summary>A body read as text (a BOM prefix is dropped when present).</summary>
    /// <param name="bytes">The body.</param>
    /// <returns>The text.</returns>
    private static string CsvText(byte[] bytes) =>
        bytes.AsSpan().StartsWith(Bom) ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : Encoding.UTF8.GetString(bytes);

    /// <summary>The issues of a refused parse, as a failure message that echoes no cell text (S3).</summary>
    /// <param name="parse">The parse.</param>
    /// <returns>The message.</returns>
    private static string Describe(ImportParse parse) =>
        "expected an accepted file, got: " + string.Join(
            "; ",
            parse.Issues.Select(issue => $"{issue.Index?.ToString(CultureInfo.InvariantCulture) ?? "-"}{issue.Path}:{issue.Reason}"));
}
