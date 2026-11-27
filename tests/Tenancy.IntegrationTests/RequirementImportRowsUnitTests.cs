using System.Text;
using Evidgate.Modules.Authoring.Application.Import;
using Evidgate.Modules.Authoring.Application.Import.Formats;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The requirement half of the import table with no database: the three carriers (CSV, XLSX, Markdown) hand the importer
/// the same rows, and the file-only rules refuse what a file alone can show to be wrong, each with a fixed column pointer
/// and reason and never a cell value.
/// </summary>
public sealed class RequirementImportRowsUnitTests
{
    private const string Story = "user_story";
    private const string Criterion = "acceptance_criterion";

    [Fact]
    public async Task Csv_Xlsx_Markdown_SameOutline_ProduceSameRequirementRows()
    {
        var fromMarkdown = MarkdownFormat.Read(Encoding.UTF8.GetBytes(
            "# Checkout\nAs a shopper I pay for my cart.\n\n- Card is charged\n- Receipt is sent"));
        fromMarkdown.IsSuccess.ShouldBeTrue();

        using var csv = new MemoryStream();
        await CsvFormat.WriteAsync(fromMarkdown.Rows, csv, TestContext.Current.CancellationToken);
        var fromCsv = CsvFormat.Read(csv.ToArray());

        using var xlsx = new MemoryStream();
        await XlsxFormat.WriteAsync(fromMarkdown.Rows, xlsx, TestContext.Current.CancellationToken);
        var fromXlsx = XlsxFormat.Read(xlsx.ToArray(), new ParsingService(), TestContext.Current.CancellationToken);

        fromCsv.IsSuccess.ShouldBeTrue();
        fromXlsx.IsSuccess.ShouldBeTrue();
        Project(fromCsv.Rows).ShouldBe(Project(fromMarkdown.Rows));
        Project(fromXlsx.Rows).ShouldBe(Project(fromMarkdown.Rows));
        Project(fromMarkdown.Rows).ShouldBe(Project(RequirementSample.Outline()));
    }

    [Fact]
    public void Csv_RequirementRowWithReferenceAndFeatureNode_IsRead()
    {
        var node = Guid.NewGuid().ToString("D");
        var parse = CsvFormat.Read(Utf8($"{AuthoringImportCalls.Header}\r\nrequirement,s1,,{node},user_story,Checkout,Body,,,,,REQ-1,\r\n"));

        parse.IsSuccess.ShouldBeTrue(string.Join(';', parse.Issues));
        parse.Rows.Single().Kind.ShouldBe(ImportRowKind.Requirement);
        parse.Rows.Single().Code.ShouldBe("REQ-1");
        parse.Rows.Single().ItemKey.ShouldBe(node);
    }

    [Fact]
    public void Csv_RequirementRowWithTrailingLineFeedInReference_IsRefusedAtCode()
    {
        var parse = CsvFormat.Read(Utf8($"{AuthoringImportCalls.Header}\r\nrequirement,s1,,,user_story,Checkout,Body,,,,,\"REQ-1\n\",\r\n"));

        parse.IsSuccess.ShouldBeFalse();
        parse.Issues.ShouldContain(new ImportIssue(1, "/code", "value_invalid"));
    }

    [Fact]
    public void Csv_RequirementRowWithPrecondition_IsRefusedAtThatRow()
    {
        var parse = CsvFormat.Read(Utf8($"{AuthoringImportCalls.Header}\r\nrequirement,s1,,,user_story,Checkout,Body,filled,,,,,\r\n"));

        parse.IsSuccess.ShouldBeFalse();
        parse.Issues.ShouldAllBe(issue => issue.Index == 1);
    }

    [Fact]
    public void ReadImportFormat_Markdown_IsImportOnly()
    {
        ImportFormats.ReadImportFormat("markdown").ShouldBe("markdown");
        ImportFormats.ReadImportFormat("csv").ShouldBe("csv");
        ImportFormats.ReadFormat("markdown").ShouldBeNull();
        ImportFormats.UnsupportedImport().Message.ShouldContain("markdown");
        ImportFormats.Unsupported().Message.ShouldNotContain("markdown");
    }

    [Fact]
    public void MixedFamily_TreeRowAfterRequirementRow_RefusesTheFirstOffendingRow()
    {
        ImportRow[] rows =
        [
            RequirementSample.Row(1, "s1", null, Story, "Checkout"),
            RequirementSample.Row(2, "a1", "s1", Criterion, "Charged"),
            ImportSample.Node(3, "n1", null, "module", "Module"),
            ImportSample.Node(4, "n2", "n1", "feature", "Feature"),
        ];

        RequirementImportValidator.MixedFamily(rows).ShouldBe(new ImportIssue(3, "/kind", "value_invalid"));
        RequirementImportValidator.MixedFamily([rows[2], rows[0]]).ShouldBe(new ImportIssue(2, "/kind", "value_invalid"));
        RequirementImportValidator.MixedFamily(rows[..2]).ShouldBeNull();
        RequirementImportValidator.MixedFamily(rows[2..]).ShouldBeNull();
    }

    [Fact]
    public void Validate_Outline_AcceptsTrimmedNfcTextAndKeepsOrder()
    {
        var (accepted, issues) = RequirementImportValidator.Validate(
        [
            RequirementSample.Row(1, "s1", null, Story, "  Cafe\u0301  ", "Body e\u0301"),
            RequirementSample.Row(2, "a1", "s1", Criterion, "Charged", null, "REQ-1"),
        ]);

        issues.ShouldBeEmpty();
        accepted.Select(row => row.Title).ShouldBe(["Caf\u00e9", "Charged"]);
        accepted[0].Body.ShouldBe("Body \u00e9");
        accepted[1].Body.ShouldBe(string.Empty);
        accepted[1].ExternalRef.ShouldBe("REQ-1");
        accepted[1].ParentKey.ShouldBe("s1");
    }

    [Fact]
    public void Validate_BodyAtTheCap_IsAcceptedAfterNfcAndOneOverIsRefused()
    {
        var exact = new string('a', 20_000);
        var decomposed = string.Concat(Enumerable.Repeat("e\u0301", 20_000));

        RequirementImportValidator.Validate([RequirementSample.Row(1, "s1", null, Story, "T", exact)]).Issues.ShouldBeEmpty();
        RequirementImportValidator.Validate([RequirementSample.Row(1, "s1", null, Story, "T", decomposed)]).Issues.ShouldBeEmpty();
        RequirementImportValidator.Validate([RequirementSample.Row(1, "s1", null, Story, "T", exact + "a")]).Issues
            .ShouldBe([new ImportIssue(1, "/description", "length")]);
        RequirementImportValidator.Validate([RequirementSample.Row(1, "s1", null, Story, "T", decomposed + "e")]).Issues
            .ShouldBe([new ImportIssue(1, "/description", "length")]);
    }

    [Fact]
    public void Validate_KeyAndTextViolations_ReportFixedPointerAndReason()
    {
        var cases = new (string Name, ImportRow[] Rows, ImportIssue Expected)[]
        {
            ("duplicate key", [Row("s1", null), Row("s1", null, 2)], new ImportIssue(2, "/key", "duplicate_key")),
            ("parent below", [Row("a1", "s1"), Row("s1", null, 2)], new ImportIssue(1, "/parent_key", "unknown_parent")),
            ("own parent", [Row("s1", "s1")], new ImportIssue(1, "/parent_key", "unknown_parent")),
            ("unknown kind", [Row("s1", null) with { NodeKind = "story" }], new ImportIssue(1, "/node_kind", "value_invalid")),
            ("no kind", [Row("s1", null) with { NodeKind = null }], new ImportIssue(1, "/node_kind", "value_invalid")),
            ("blank title", [Row("s1", null) with { Title = "   " }], new ImportIssue(1, "/title", "value_invalid")),
            ("long title", [Row("s1", null) with { Title = new string('t', 201) }], new ImportIssue(1, "/title", "value_invalid")),
            ("nul title", [Row("s1", null) with { Title = "a\0b" }], new ImportIssue(1, "/title", "invalid_text")),
            ("nul body", [Row("s1", null) with { Description = "a\0b" }], new ImportIssue(1, "/description", "invalid_text")),
            ("space in reference", [Row("s1", null) with { Code = "REQ 1" }], new ImportIssue(1, "/code", "value_invalid")),
            ("line feed in reference", [Row("s1", null) with { Code = "REQ-1\n" }], new ImportIssue(1, "/code", "value_invalid")),
            ("long reference", [Row("s1", null) with { Code = new string('R', 129) }], new ImportIssue(1, "/code", "value_invalid")),
            ("same reference twice", [Row("s1", null) with { Code = "R-1" }, Row("s2", null, 2) with { Code = "R-1" }], new ImportIssue(2, "/code", "duplicate_key")),
            ("feature node not a guid", [Row("s1", null) with { ItemKey = "not-a-guid" }], new ImportIssue(1, "/item_key", "value_invalid")),
            ("precondition", [Row("s1", null) with { Precondition = "x" }], new ImportIssue(1, "/precondition", "value_invalid")),
            ("steps", [Row("s1", null) with { StepsJson = "{}" }], new ImportIssue(1, "/steps", "value_invalid")),
            ("priority", [Row("s1", null) with { Priority = "high" }], new ImportIssue(1, "/priority", "value_invalid")),
            ("state", [Row("s1", null) with { State = "draft" }], new ImportIssue(1, "/state", "value_invalid")),
            ("position", [Row("s1", null) with { Position = 3 }], new ImportIssue(1, "/position", "value_invalid")),
        };

        foreach (var (name, rows, expected) in cases)
        {
            var (accepted, issues) = RequirementImportValidator.Validate(rows);

            issues.ShouldContain(expected, name);
            accepted.Count.ShouldBeLessThan(rows.Length, name);
        }
    }

    private static ImportRow Row(string key, string? parent, int index = 1) =>
        RequirementSample.Row(index, key, parent, parent is null ? Story : Criterion, "Title " + key);

    private static string[] Project(IEnumerable<ImportRow> rows) =>
        [.. rows.Select(row => $"{row.Kind}|{row.Key}|{row.ParentKey}|{row.NodeKind}|{row.Title}|{row.Description}")];

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
