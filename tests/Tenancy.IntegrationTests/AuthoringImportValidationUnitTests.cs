using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Application.Import;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1 AC13's phase-1 refusals at their own level (13.1.1-T5): <see cref="ImportValidator"/> decides everything the
/// file alone settles, with no database, and each test pins one reason of the AC13 vocabulary to the row and the fixed
/// column pointer it must name.
/// </summary>
/// <remarks>
/// <para>
/// <b>One reason per test, one pointer per row.</b> The vocabulary is a contract (route 25's <c>details[].reason</c>), so
/// a reason that drifted would break a client's switch; and the pointer must be a <em>column</em> of the flat table
/// (<c>/title</c>, <c>/parent_key</c>, …), because a client maps it back onto the cell it sent.
/// </para>
/// <para>
/// <b>No cell value ever reaches an issue.</b> <see cref="HostileRows_NoEcho"/> plants a marker in the title, the key and
/// the code of three rows and asserts the marker appears in none of the issue paths, reasons or the error the importer
/// builds from them (S3, AC16's "no echo" rule).
/// </para>
/// </remarks>
public sealed class AuthoringImportValidationUnitTests
{
    private static readonly Lazy<ContractSchemaSet> Schemas =
        new(() => ContractSchemaSet.Load(Path.Combine(RlsExceptionFile.RepositoryRoot, "contracts")));

    private static ImportValidator Validator() => new(new ManualCaseStepsValidator(Schemas));

    private static IReadOnlyList<ImportIssue> Validate(params ImportRow[] rows) =>
        Validator().Validate(rows, ImportSample.Prefix).Issues;

    /// <summary>A valid tree is accepted whatever order the file lists it in (parents are resolved by key).</summary>
    [Fact]
    public void Accepted_TreeInAnyOrder_IsAccepted()
    {
        // The case names its item before the item exists, the item names a category that appears after it, and the
        // category names a feature that appears last: AC1's ladder is module, feature, category (13.1.1 §4).
        var validation = Validator().Validate(
            [
                ImportSample.Case(1, "c1", "i1", "login works", ImportSample.Steps),
                ImportSample.Item(2, "i1", "n3", "login"),
                ImportSample.Node(3, "n3", "n2", "category", "Authentication"),
                ImportSample.Node(4, "n2", "n1", "feature", "Sign-in"),
                ImportSample.Node(5, "n1", null, "module", "Accounts"),
            ],
            ImportSample.Prefix);

        validation.IsAccepted.ShouldBeTrue(string.Join(", ", validation.Issues.Select(issue => issue.Path + " " + issue.Reason)));
        validation.Rows.Count.ShouldBe(5);
        validation.Rows.Single(row => row.Key == "n2").Depth.ShouldBe(1);
        validation.Rows.Single(row => row.Key == "n3").Depth.ShouldBe(2);
        validation.Rows.Single(row => row.Key == "c1").StepsJson.ShouldNotBeNull();
    }

    /// <summary>A row whose title carries a lone surrogate or U+0000 is <c>invalid_text</c> on that column, never a 500.</summary>
    [Fact]
    public void InvalidText_LoneSurrogateAndNul_AreRefusedOnTheirColumn()
    {
        var issues = Validate(
            ImportSample.Node(1, "n1", null, "module", "Ac\ud800counts"),
            ImportSample.Node(2, "n\u0000two", null, "module", "Billing"));

        issues.ShouldContain(issue => issue.Index == 1 && issue.Path == "/title" && issue.Reason == "invalid_text");
        issues.ShouldContain(issue => issue.Index == 2 && issue.Path == "/key" && issue.Reason == "invalid_text");
    }

    /// <summary>A parent key that names nothing in the file is <c>unknown_parent</c> (not an orphan: the key is misspelt).</summary>
    [Fact]
    public void UnknownParent_MissingKey_IsRefused()
    {
        var issues = Validate(ImportSample.Node(1, "n1", "n9", "feature", "Checkout"));

        issues.ShouldContain(issue => issue.Index == 1 && issue.Path == "/parent_key" && issue.Reason == "unknown_parent");
    }

    /// <summary>An item with no node, and a case whose parent is a node, are both <c>orphan_row</c>.</summary>
    [Fact]
    public void OrphanRow_WrongOrMissingParentKind_IsRefused()
    {
        var issues = Validate(
            ImportSample.Item(1, "i1", null, "cart"),
            ImportSample.Node(2, "n1", null, "module", "Accounts"),
            ImportSample.Case(3, "c1", "n1", "works", ImportSample.Steps));

        issues.ShouldContain(issue => issue.Index == 1 && issue.Path == "/parent_key" && issue.Reason == "orphan_row");
        issues.ShouldContain(issue => issue.Index == 3 && issue.Path == "/item_key" && issue.Reason == "orphan_row");
    }

    /// <summary>An item may only hang under a category or a sub-category: under a module it is <c>unknown_parent</c> (AC3).</summary>
    [Fact]
    public void KindLadder_ItemUnderModuleOrFeature_IsRefused()
    {
        var issues = Validate(
            ImportSample.Node(1, "n1", null, "module", "Accounts"),
            ImportSample.Item(2, "i1", "n1", "login"),
            ImportSample.Node(3, "n2", "n1", "feature", "Checkout"),
            ImportSample.Item(4, "i2", "n2", "pay"));

        issues.ShouldContain(issue => issue.Index == 2 && issue.Path == "/parent_key" && issue.Reason == "unknown_parent");
        issues.ShouldContain(issue => issue.Index == 4 && issue.Path == "/parent_key" && issue.Reason == "unknown_parent");
    }

    /// <summary>Two rows with one file-local key are <c>duplicate_key</c> on the repeated one.</summary>
    [Fact]
    public void DuplicateKey_TwoRowsWithOneKey_IsRefused()
    {
        var issues = Validate(
            ImportSample.Node(1, "n1", null, "module", "Accounts"),
            ImportSample.Node(2, "n1", null, "module", "Billing"));

        issues.ShouldContain(issue => issue.Index == 2 && issue.Path == "/key" && issue.Reason == "duplicate_key");
    }

    /// <summary>Two rows with different keys that describe one entity are refused too: both would resolve to the same row.</summary>
    [Fact]
    public void DuplicateKey_TwoRowsWithOneIdentity_IsRefused()
    {
        var issues = Validate(
            ImportSample.Node(1, "n1", null, "module", "Accounts"),
            ImportSample.Node(2, "n2", null, "module", "Accounts"));

        issues.ShouldContain(issue => issue.Index == 2 && issue.Path == "/key" && issue.Reason == "duplicate_key");
    }

    /// <summary>A case code outside the project's prefix, the grammar or the explicit-number ceiling is <c>code_conflict</c> (AC4).</summary>
    [Theory]
    [InlineData("CHEQ-001")]
    [InlineData("ACME-1")]
    [InlineData("acme-001")]
    [InlineData("ACME-000")]
    [InlineData("ACME-1000000")]
    [InlineData("ACME-999999999")]
    public void CodeConflict_CodeOutsideTheProjectGrammar_IsRefused(string code)
    {
        var issues = Validate(
            ImportSample.Node(1, "n1", null, "module", "Cart"),
            ImportSample.Node(2, "n2", "n1", "feature", "Cart body"),
            ImportSample.Node(3, "n3", "n2", "category", "Cart items"),
            ImportSample.Item(4, "i1", "n3", "open cart"),
            ImportSample.Case(5, "c1", "i1", "opens", ImportSample.Steps, code: code));

        issues.ShouldContain(issue => issue.Index == 5 && issue.Path == "/code" && issue.Reason == "code_conflict");
    }

    /// <summary>One code twice in one file is <c>code_conflict</c> on the second row.</summary>
    [Fact]
    public void CodeConflict_SameCodeTwiceInOneFile_IsRefused()
    {
        var issues = Validate(
            ImportSample.Node(1, "n1", null, "module", "Cart"),
            ImportSample.Node(2, "n2", "n1", "feature", "Cart body"),
            ImportSample.Node(3, "n3", "n2", "category", "Cart items"),
            ImportSample.Item(4, "i1", "n3", "open cart"),
            ImportSample.Case(5, "c1", "i1", "opens", ImportSample.Steps, code: "ACME-007"),
            ImportSample.Case(6, "c2", "i1", "closes", ImportSample.Steps, code: "ACME-007"));

        issues.ShouldContain(issue => issue.Index == 6 && issue.Path == "/code" && issue.Reason == "code_conflict");
    }

    /// <summary>A row that breaks a §4 value rule is <c>value_invalid</c> on the column that broke it.</summary>
    [Fact]
    public void ValueInvalid_EachRowShapeProblem_IsRefusedOnItsColumn()
    {
        var longTitle = new string('a', 201);

        var issues = Validate(
            // 1: a title is required.
            ImportSample.Node(1, "n1", null, "module", null),
            // 2: a node has no description (the flat table would silently drop it).
            ImportSample.Node(2, "n2", null, "module", "Accounts", description: "not a node member"),
            // 3: the kind set is closed.
            ImportSample.Node(3, "n3", null, "epic", "Billing"),
            // 4: an item needs a priority.
            ImportSample.Item(4, "i1", "n3", "login", priority: null),
            // 5: and a state from the closed set.
            ImportSample.Item(5, "i2", "n3", "login", state: "approved"),
            // 6: a negative position is not a position.
            ImportSample.Node(6, "n6", null, "module", "Reports", position: -1),
            // 7: a title is at most 200 code points.
            ImportSample.Node(7, "n7", null, "module", longTitle),
            // 8: a case's steps belong to a case, not to an item.
            ImportSample.Item(8, "i8", "n3", "cart", steps: ImportSample.Steps));

        issues.ShouldContain(issue => issue.Index == 1 && issue.Path == "/title" && issue.Reason == "value_invalid");
        issues.ShouldContain(issue => issue.Index == 2 && issue.Path == "/description" && issue.Reason == "value_invalid");
        issues.ShouldContain(issue => issue.Index == 3 && issue.Path == "/node_kind" && issue.Reason == "value_invalid");
        issues.ShouldContain(issue => issue.Index == 4 && issue.Path == "/priority" && issue.Reason == "value_invalid");
        issues.ShouldContain(issue => issue.Index == 5 && issue.Path == "/state" && issue.Reason == "value_invalid");
        issues.ShouldContain(issue => issue.Index == 6 && issue.Path == "/position" && issue.Reason == "value_invalid");
        issues.ShouldContain(issue => issue.Index == 7 && issue.Path == "/title" && issue.Reason == "value_invalid");
        issues.ShouldContain(issue => issue.Index == 8 && issue.Path == "/steps" && issue.Reason == "value_invalid");
    }

    /// <summary>A steps cell that is not JSON, or that the schema refuses, is <c>steps_invalid</c> — one issue, one pointer.</summary>
    [Fact]
    public void StepsInvalid_NotJsonAndSchemaViolation_AreRefused()
    {
        var issues = Validate(
            ImportSample.Node(1, "n1", null, "category", "Cart"),
            ImportSample.Item(2, "i1", "n1", "open cart"),
            ImportSample.Case(3, "c1", "i1", "opens", "{not json"),
            ImportSample.Case(4, "c2", "i1", "closes", """{"steps":[]}"""),
            ImportSample.Case(5, "c3", "i1", "empties", """{"steps":[{"no":1,"action":"a"}]}"""));

        issues.ShouldContain(issue => issue.Index == 3 && issue.Path == "/steps" && issue.Reason == "steps_invalid");
        issues.ShouldContain(issue => issue.Index == 4 && issue.Path == "/steps" && issue.Reason == "steps_invalid");
        issues.ShouldContain(issue => issue.Index == 5 && issue.Path == "/steps" && issue.Reason == "steps_invalid");
    }

    /// <summary>The issue cap stops the list at 100 rows, so a hostile file cannot grow the envelope without bound.</summary>
    [Fact]
    public void Issues_AreCappedAtOneHundred()
    {
        var rows = Enumerable.Range(1, 150)
            .Select(index => ImportSample.Node(index, "n" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), "missing", "feature", "Feature"))
            .ToArray();

        Validate(rows).Count.ShouldBe(ImportValidator.MaximumIssues);
    }

    /// <summary>
    /// A cell value planted in the title, the key and the code never reaches an issue path, an issue reason or the error
    /// the importer builds from them (S3): a refusal names a column, never what the caller wrote in it.
    /// </summary>
    [Fact]
    public void HostileRows_NoEcho()
    {
        const string marker = "HOSTILE-MARKER-7f3a";
        const string hostileCode = marker + "-001";

        var validation = Validator().Validate(
            [
                ImportSample.Node(1, "n1", null, "module", marker),
                // A surrogate makes the title unstorable, so the issue carries the column and not the text.
                ImportSample.Node(2, marker + "\ud800", null, "epic", "Billing"),
                ImportSample.Item(3, "i1", "n1", marker, code: null),
                ImportSample.Case(4, "c1", "i1", marker, "{" + marker, code: hostileCode),
            ],
            ImportSample.Prefix);

        validation.IsAccepted.ShouldBeFalse();
        validation.Issues.ShouldNotBeEmpty();

        foreach (var issue in validation.Issues)
        {
            issue.Path.ShouldNotContain(marker);
            issue.Reason.ShouldNotContain(marker);
        }

        var error = AuthoringImporter.Refusal(validation.Issues).Error;

        error.ShouldNotBeNull();
        error.Message.ShouldNotContain(marker);

        foreach (var detail in error.Details ?? [])
        {
            detail.Path.ShouldNotContain(marker);
            detail.Reason.ShouldNotContain(marker);
        }
    }
}

/// <summary>
/// The flat rows the import tests build, one factory per kind with the §3 defaults of a valid row, so each test states
/// only the member it is about.
/// </summary>
internal static class ImportSample
{
    /// <summary>The project's <c>case_prefix</c> the tests import against.</summary>
    internal const string Prefix = "ACME";

    /// <summary>A valid <c>authoring-steps.v1</c> document.</summary>
    internal const string Steps = """{"steps":[{"no":1,"action":"open the cart","expected":"the cart opens"}]}""";

    /// <summary>A feature node row.</summary>
    /// <param name="index">The 1-based data-row position.</param>
    /// <param name="key">The file-local key.</param>
    /// <param name="parentKey">The parent node's key, or null.</param>
    /// <param name="kind">The node kind.</param>
    /// <param name="title">The title.</param>
    /// <param name="description">A description the node kind does not carry (a refusal case).</param>
    /// <param name="position">The named position.</param>
    internal static ImportRow Node(
        int index,
        string? key,
        string? parentKey,
        string? kind,
        string? title,
        string? description = null,
        int? position = null) => new(
            index,
            ImportRowKind.Node,
            key!,
            parentKey,
            null,
            kind,
            title,
            description,
            null,
            null,
            null,
            null,
            null,
            position);

    /// <summary>A checklist item row.</summary>
    /// <param name="index">The 1-based data-row position.</param>
    /// <param name="key">The file-local key.</param>
    /// <param name="nodeKey">The owning node's key.</param>
    /// <param name="title">The title.</param>
    /// <param name="description">The description.</param>
    /// <param name="steps">A steps cell the item kind does not carry (a refusal case).</param>
    /// <param name="priority">The priority.</param>
    /// <param name="state">The state.</param>
    /// <param name="code">A code the item kind does not carry (a refusal case).</param>
    /// <param name="position">The named position.</param>
    internal static ImportRow Item(
        int index,
        string? key,
        string? nodeKey,
        string? title,
        string? description = null,
        string? steps = null,
        string? priority = "high",
        string? state = null,
        string? code = null,
        int? position = null) => new(
            index,
            ImportRowKind.Item,
            key!,
            nodeKey,
            null,
            null,
            title,
            description,
            null,
            steps,
            priority,
            state,
            code,
            position);

    /// <summary>A manual test case row.</summary>
    /// <param name="index">The 1-based data-row position.</param>
    /// <param name="key">The file-local key.</param>
    /// <param name="itemKey">The owning item's key.</param>
    /// <param name="title">The title.</param>
    /// <param name="steps">The steps cell (JSON text).</param>
    /// <param name="description">The description.</param>
    /// <param name="precondition">The precondition.</param>
    /// <param name="priority">The priority.</param>
    /// <param name="state">The state.</param>
    /// <param name="code">The case code, or null to have one allocated.</param>
    internal static ImportRow Case(
        int index,
        string? key,
        string? itemKey,
        string? title,
        string? steps,
        string? description = null,
        string? precondition = null,
        string? priority = "medium",
        string? state = null,
        string? code = null) => new(
            index,
            ImportRowKind.Case,
            key!,
            null,
            itemKey,
            null,
            title,
            description,
            precondition,
            steps,
            priority,
            state,
            code,
            null);
}
