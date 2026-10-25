using Evidgate.Modules.Authoring.Application.Import;
using Evidgate.Modules.Authoring.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Finding A (13.1.1-T5, delta N2) at the unit level: <see cref="ImportPlanner.MatchedState"/> is the one place an import
/// decides what a matched row's <c>state</c> cell means, and its truth table is what stands between an export-shaped file
/// and a state reset. The importer-level cases in <see cref="AuthoringImporterTests"/> prove the same rules through
/// PostgreSQL; this file pins the table row by row, including the rows a database test cannot stage cheaply (a stored
/// <c>retired</c> row, which no live row may reach through the import).
/// </summary>
/// <remarks>
/// The expectation is written out, never read back from the machine: <c>Decide</c> is the answer the wrapper must
/// respect, not the thing it is checked against.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class AuthoringImportCoreStateTests
{
    /// <summary>An empty <c>state</c> cell keeps the stored state, whatever that state is: this is finding A's core.</summary>
    /// <param name="stored">The state the entity is stored in.</param>
    [Theory]
    [InlineData("draft")]
    [InlineData("approved")]
    [InlineData("retired")]
    public void EmptyCell_KeepsTheStoredCaseState(string stored)
    {
        var issues = new List<ImportIssue>();

        ImportPlanner
            .MatchedState(CaseRow(stateCell: null), stored, (from, to) => ManualTestCaseStateMachine.Decide(from, to, null), issues)
            .ShouldBe(stored);

        issues.ShouldBeEmpty("an empty cell is not a move, so it is never refused");
    }

    /// <summary>An empty <c>state</c> cell keeps a stored item's state the same way.</summary>
    /// <param name="stored">The state the item is stored in.</param>
    [Theory]
    [InlineData("draft")]
    [InlineData("active")]
    [InlineData("retired")]
    public void EmptyCell_KeepsTheStoredItemState(string stored)
    {
        var issues = new List<ImportIssue>();

        ImportPlanner
            .MatchedState(ItemRow(stateCell: null), stored, (from, to) => ChecklistItemStateMachine.Decide(from, to, null), issues)
            .ShouldBe(stored);

        issues.ShouldBeEmpty();
    }

    /// <summary>A move the state machine allows is the state the row writes, with no issue.</summary>
    [Fact]
    public void AllowedMove_WritesTheFileState()
    {
        var issues = new List<ImportIssue>();

        ImportPlanner
            .MatchedState(CaseRow("approved"), "draft", (from, to) => ManualTestCaseStateMachine.Decide(from, to, null), issues)
            .ShouldBe("approved");

        issues.ShouldBeEmpty();
    }

    /// <summary>
    /// A move into <c>retired</c> needs a reason, which the row table cannot carry, so it is refused on the state column —
    /// from any live state, and never with a partial write.
    /// </summary>
    /// <param name="stored">The state the row is stored in.</param>
    [Theory]
    [InlineData("draft")]
    [InlineData("approved")]
    public void MoveIntoRetired_IsRefused(string stored)
    {
        var issues = new List<ImportIssue>();

        ImportPlanner
            .MatchedState(CaseRow("retired"), stored, (from, to) => ManualTestCaseStateMachine.Decide(from, to, null), issues)
            .ShouldBeNull();

        issues.Count.ShouldBe(1);
        issues[0].Path.ShouldBe("/state");
        issues[0].Reason.ShouldBe("value_invalid");
    }

    /// <summary>A state outside the kind's set is refused the same way, for an item row.</summary>
    [Fact]
    public void MoveToAnItemStateTheMachineDoesNotAllow_IsRefused()
    {
        var issues = new List<ImportIssue>();

        ImportPlanner
            .MatchedState(ItemRow("retired"), "draft", (from, to) => ChecklistItemStateMachine.Decide(from, to, null), issues)
            .ShouldBeNull();

        issues.Count.ShouldBe(1);
        issues[0].Path.ShouldBe("/state");
    }

    /// <summary>
    /// Finding F (N2, the stale-group branch): the plan's per-group expectation is what a concurrent writer must not have
    /// changed by the time the locks are held. Every direction is a mismatch — a sibling added or soft-deleted moves the
    /// live count, a renumber moves the highest position, a vanished group is not "0 rows", and the same id under the other
    /// table is a different group.
    /// </summary>
    [Fact]
    public void GroupExpectation_HoldsOnlyForAnIdenticalGroup()
    {
        var group = Guid.CreateVersion7();
        ImportGroupExpectation[] planned = [new(ImportRowKind.Node, group, LiveCount: 3, Maximum: 7)];

        AuthoringImporter
            .ExpectationsHold(planned, new Dictionary<(string Kind, Guid ParentId), (int, int)>
            {
                [("feature_node", group)] = (3, 7),
            })
            .ShouldBeTrue("the group is exactly what the plan read");

        AuthoringImporter
            .ExpectationsHold(planned, new Dictionary<(string Kind, Guid ParentId), (int, int)>
            {
                [("feature_node", group)] = (4, 8),
            })
            .ShouldBeFalse("a sibling was added between the read and the lock");

        AuthoringImporter
            .ExpectationsHold(planned, new Dictionary<(string Kind, Guid ParentId), (int, int)>
            {
                [("feature_node", group)] = (2, 7),
            })
            .ShouldBeFalse("a sibling was soft-deleted between the read and the lock");

        AuthoringImporter
            .ExpectationsHold(planned, new Dictionary<(string Kind, Guid ParentId), (int, int)>
            {
                [("feature_node", group)] = (3, 9),
            })
            .ShouldBeFalse("another writer renumbered the group");

        AuthoringImporter
            .ExpectationsHold(planned, new Dictionary<(string Kind, Guid ParentId), (int, int)>())
            .ShouldBeFalse("a vanished group is not an empty one");

        AuthoringImporter
            .ExpectationsHold(planned, new Dictionary<(string Kind, Guid ParentId), (int, int)>
            {
                [("checklist_item", group)] = (3, 7),
            })
            .ShouldBeFalse("the same id under the item table is a different sibling set");
    }

    /// <summary>An item group is compared against the item table, not the node one.</summary>
    [Fact]
    public void GroupExpectation_ItemGroupReadsTheItemTable()
    {
        var group = Guid.CreateVersion7();
        ImportGroupExpectation[] planned = [new(ImportRowKind.Item, group, LiveCount: 2, Maximum: 5)];

        AuthoringImporter
            .ExpectationsHold(planned, new Dictionary<(string Kind, Guid ParentId), (int, int)>
            {
                [("checklist_item", group)] = (2, 5),
            })
            .ShouldBeTrue();

        AuthoringImporter
            .ExpectationsHold(planned, new Dictionary<(string Kind, Guid ParentId), (int, int)>
            {
                [("feature_node", group)] = (2, 5),
            })
            .ShouldBeFalse();
    }

    /// <summary>One case row of a file, with the given <c>state</c> cell.</summary>
    /// <param name="stateCell">The cell, or null for an empty one.</param>
    private static ImportValidatedRow CaseRow(string? stateCell) => new(
        new ImportRow(1, ImportRowKind.Case, "c1", null, "i1", null, "Case 1", null, null, "[]", "medium", stateCell, "ACME-001", null),
        ImportRowKind.Case,
        "c1",
        null,
        "i1",
        null,
        "Case 1",
        null,
        null,
        "[]",
        "medium",
        stateCell ?? "draft",
        "ACME-001",
        null,
        2,
        stateCell is not null);

    /// <summary>One item row of a file, with the given <c>state</c> cell.</summary>
    /// <param name="stateCell">The cell, or null for an empty one.</param>
    private static ImportValidatedRow ItemRow(string? stateCell) => new(
        new ImportRow(1, ImportRowKind.Item, "i1", "n3", null, null, "Item 1", null, null, null, "high", stateCell, null, null),
        ImportRowKind.Item,
        "i1",
        "n3",
        null,
        null,
        "Item 1",
        null,
        null,
        null,
        "high",
        stateCell ?? "active",
        null,
        null,
        1,
        stateCell is not null);
}
