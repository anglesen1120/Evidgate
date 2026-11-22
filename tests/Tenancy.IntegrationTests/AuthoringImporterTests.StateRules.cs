using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Application.Import;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class AuthoringImporterTests
{
    /// <summary>Moves the imported item to its terminal state through the writer, the one path that may change a state.</summary>
    /// <param name="world">The world.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="nodeId">Its owning node.</param>
    private static async Task RetireItemAsync(World world, Guid itemId, Guid nodeId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, world.Project, false, cancellationToken);

        var writer = services.ServiceProvider.GetRequiredService<AuthoringVersionWriter>();
        var retired = new ChecklistItemSnapshot(
            nodeId,
            "Item 1",
            null,
            "high",
            ChecklistItemStateMachine.Retired,
            0);

        using var document = JsonDocument.Parse(AuthoringContent.Serialize(retired));

        var result = await writer.AppendAsync(
            world.Org,
            world.Project,
            Guid.CreateVersion7(),
            null,
            [EntityChange.Update(AuthoringEntityKind.ChecklistItem, itemId, document.RootElement.Clone())],
            // A move into `retired` without a reason is 422 /reason (AuthoringStates.cs:188, ReasonRequired), so the
            // retire this test performs must carry one, exactly like ChecklistItemUpdate.cs:144 does.
            new AuthoringAppendContext { Reason = "retired by the importer test" },
            cancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        await scope.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Code F1 (N2a): an empty <c>state</c> cell keeps the stored state. The case is approved through the writer, then the
    /// file is re-imported with one edited title: the title lands, the state stays approved, and the item row is unchanged.
    /// </summary>
    [Fact]
    public async Task MatchedState_EmptyCell_KeepsTheApprovedCaseApproved()
    {
        await using var world = await StartAsync();

        var created = await ImportAsync(world, world.Project, Single());
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        var (caseId, itemId, code) = await CaseIdentityAsync(world);
        await AppendCaseAsync(world, caseId, itemId, code, "approved", "Case 1.1");

        // Matched by code: the key is unique per file, the code is what identifies the stored case (AC4).
        var edited = await ImportAsync(world, world.Project, Single(code: code, title: "Case 1.1 edited"));

        edited.IsSuccess.ShouldBeTrue(edited.Error?.Message);
        edited.Value!.Counts.ShouldBe(new ImportCounts(Created: 0, Updated: 1, Restored: 0, Unchanged: 4));
        (await CaseStatesAsync(world)).ShouldBe(["approved"]);
        (await CaseTitlesAsync(world)).ShouldBe(["Case 1.1 edited"]);
    }

    /// <summary>
    /// Code F1 (N2b): a <c>state</c> cell is only taken when the same state machine the route uses allows the move. A move
    /// into <c>retired</c> without a reason and a value the item's state machine does not know are refused on
    /// <c>/state</c> as <c>value_invalid</c>, and no version row is written.
    /// </summary>
    [Fact]
    public async Task MatchedState_IllegalTransitionAndReasonlessRetire_Refused()
    {
        await using var world = await StartAsync();

        (await ImportAsync(world, world.Project, Single())).IsSuccess.ShouldBeTrue();
        var (_, _, code) = await CaseIdentityAsync(world);
        var versions = await VersionCountAsync(world);

        // `retired` is a legal case state but the move needs a reason, which the row table cannot carry (Code F1/F3). Index 5
        // is the case row, so this cannot pass on the item refusal below.
        var retire = await ImportAsync(world, world.Project, Single(code: code, state: "retired"));

        retire.IsSuccess.ShouldBeFalse();
        retire.Error!.Details.ShouldNotBeNull();
        retire.Error.Details.ShouldContain(
            detail => detail.Index == 5 && detail.Path == "/state" && detail.Reason == "value_invalid");

        // The item's own reasonless retire: `retired` is a state the schema accepts (so the row reaches the state machine,
        // unlike a value outside the enum), and the machine still refuses the move for want of a reason. Index 4.
        var illegal = await ImportAsync(world, world.Project, Single(code: code, itemState: "retired"));

        illegal.IsSuccess.ShouldBeFalse();
        illegal.Error!.Details.ShouldNotBeNull();
        illegal.Error.Details.ShouldContain(
            detail => detail.Index == 4 && detail.Path == "/state" && detail.Reason == "value_invalid");

        (await VersionCountAsync(world)).ShouldBe(versions, "a refused row writes nothing");
    }

    /// <summary>
    /// Code F3 (N2c): restoring a soft-deleted case always writes <see cref="ManualTestCaseStateMachine.RestoreState"/> —
    /// draft — whatever the file's <c>state</c> cell says.
    /// </summary>
    [Fact]
    public async Task Restore_ForcesDraft_WhateverTheFileSays()
    {
        await using var world = await StartAsync();

        (await ImportAsync(world, world.Project, Single())).IsSuccess.ShouldBeTrue();

        var (caseId, itemId, code) = await CaseIdentityAsync(world);
        await AppendCaseAsync(world, caseId, itemId, code, "approved", "Case 1.1");
        await SoftDeleteAsync(world, AuthoringEntityKind.ManualTestCase, caseId);

        var restored = await ImportAsync(world, world.Project, Single(code: code, state: "approved"));

        restored.IsSuccess.ShouldBeTrue(restored.Error?.Message);
        restored.Value!.Rows.Single(row => row.Kind == ImportRowKind.Case).Outcome.ShouldBe("restore");
        (await CaseStatesAsync(world)).ShouldBe([ManualTestCaseStateMachine.Draft]);
    }

    /// <summary>
    /// Findings E and N1 (N2d/N2e): an item stored as <c>retired</c> still re-imports unchanged (the export round trip must
    /// survive), and no case may be created under it.
    /// </summary>
    [Fact]
    public async Task StoredRetiredItem_UnchangedRowImports_NewCaseUnderItRefused()
    {
        await using var world = await StartAsync();

        (await ImportAsync(world, world.Project, Single())).IsSuccess.ShouldBeTrue();
        var (_, itemId, code) = await CaseIdentityAsync(world);
        var nodeId = (await _fixture.ColumnAsync<Guid>(
            world.Database,
            $"select feature_node_id from public.checklist_item where org_id = '{world.Org:D}'"
            + $" and project_id = '{world.Project:D}'")).Single();

        await RetireItemAsync(world, itemId, nodeId);

        // The export of that project, re-imported as-is: the retired item row is unchanged and permitted.
        var roundTrip = await ImportAsync(world, world.Project, Single(code: code));

        roundTrip.IsSuccess.ShouldBeTrue(roundTrip.Error?.Message);
        roundTrip.Value!.Counts.ShouldBe(new ImportCounts(Created: 0, Updated: 0, Restored: 0, Unchanged: 5));

        // A new case under the retired item is not: the writer refuses it, so the file must too.
        var refused = await ImportAsync(
            world,
            world.Project,
            Single(code: code, extraCases: [ImportSample.Case(6, "c2", "i1", "Case 1.2", ImportSample.Steps)]));

        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Details.ShouldNotBeNull();
        refused.Error.Details.ShouldContain(detail => detail.Path == "/item_key" && detail.Reason == "value_invalid");
        (await CaseStatesAsync(world)).Count.ShouldBe(1, "the refused row wrote nothing");
    }

    /// <summary>
    /// Decision N1 (N2f): a new row takes the file's <c>state</c> as written, so an export-shaped file that creates a
    /// retired item and an approved case imports as-is and a second import finds every row unchanged.
    /// </summary>
    [Fact]
    public async Task CreatedRows_TakeTheFileState_SoTheExportShapeRoundTrips()
    {
        await using var world = await StartAsync();

        // The export shape: the code is written, so the second import matches the stored case (the key is file-local).
        var code = (await PrefixAsync(world, world.Project)) + "-001";

        IReadOnlyList<ImportRow> file =
        [
            ImportSample.Node(1, "n1", null, "module", "Module 1"),
            ImportSample.Node(2, "n2", "n1", "feature", "Feature 1"),
            ImportSample.Node(3, "n3", "n2", "category", "Category 1"),
            ImportSample.Item(4, "i1", "n3", "Item 1", state: ChecklistItemStateMachine.Retired),
            ImportSample.Case(
                5, "c1", "i1", "Case 1.1", ImportSample.Steps, state: ManualTestCaseStateMachine.Approved, code: code),
        ];

        var created = await ImportAsync(world, world.Project, file);

        created.IsSuccess.ShouldBeTrue(
            created.Error?.Message + " | " + string.Join(
                "; ",
                (created.Error?.Details ?? []).Select(detail => $"{detail.Index}{detail.Path}:{detail.Reason}")));
        created.Value!.Counts.ShouldBe(new ImportCounts(Created: 5, Updated: 0, Restored: 0, Unchanged: 0));

        var itemState = (await _fixture.ColumnAsync<string>(
            world.Database,
            $"select state from public.checklist_item where org_id = '{world.Org:D}'"
            + $" and project_id = '{world.Project:D}'")).Single();

        itemState.ShouldBe(ChecklistItemStateMachine.Retired);
        (await CaseStatesAsync(world)).ShouldBe([ManualTestCaseStateMachine.Approved]);

        var versions = await VersionCountAsync(world);
        var again = await ImportAsync(world, world.Project, file);

        again.IsSuccess.ShouldBeTrue(again.Error?.Message);
        again.Value!.Counts.ShouldBe(new ImportCounts(Created: 0, Updated: 0, Restored: 0, Unchanged: 5));
        (await VersionCountAsync(world)).ShouldBe(versions, "an unchanged re-import appends nothing");
    }

    /// <summary>The case's id, its parent item's id and its code, from the one case the fixture creates.</summary>
    /// <param name="world">The world.</param>
    private async Task<(Guid CaseId, Guid ItemId, string Code)> CaseIdentityAsync(World world)
    {
        var rows = await _fixture.ColumnAsync<string>(
            world.Database,
            $"select id::text || '|' || checklist_item_id::text || '|' || code from public.manual_test_case"
            + $" where org_id = '{world.Org:D}' and project_id = '{world.Project:D}'");

        var parts = rows.Single().Split('|');

        return (Guid.Parse(parts[0]), Guid.Parse(parts[1]), parts[2]);
    }

    /// <summary>Moves one case to a state through the writer, the one path that may change it.</summary>
    /// <param name="world">The world.</param>
    /// <param name="caseId">The case.</param>
    /// <param name="itemId">Its parent item.</param>
    /// <param name="code">Its stored code.</param>
    /// <param name="state">The state to move to.</param>
    /// <param name="title">The title to store with it.</param>
    private static async Task AppendCaseAsync(
        World world,
        Guid caseId,
        Guid itemId,
        string code,
        string state,
        string title)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, world.Project, false, cancellationToken);

        var writer = services.ServiceProvider.GetRequiredService<AuthoringVersionWriter>();
        var approved = new ManualTestCaseSnapshot(itemId, code, title, null, null, ImportSample.Steps, "medium", state);

        using var document = JsonDocument.Parse(AuthoringContent.Serialize(approved));

        var result = await writer.AppendAsync(
            world.Org,
            world.Project,
            Guid.CreateVersion7(),
            null,
            [EntityChange.Update(AuthoringEntityKind.ManualTestCase, caseId, document.RootElement.Clone())],
            null,
            cancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        await scope.CommitAsync(cancellationToken);
    }

    /// <summary>Every stored case state of the project.</summary>
    /// <param name="world">The world.</param>
    private async Task<IReadOnlyList<string>> CaseStatesAsync(World world) =>
        await _fixture.ColumnAsync<string>(
            world.Database,
            $"select state from public.manual_test_case where org_id = '{world.Org:D}'"
            + $" and project_id = '{world.Project:D}' order by code");

    /// <summary>Every stored case title of the project.</summary>
    /// <param name="world">The world.</param>
    private async Task<IReadOnlyList<string>> CaseTitlesAsync(World world) =>
        await _fixture.ColumnAsync<string>(
            world.Database,
            $"select title from public.manual_test_case where org_id = '{world.Org:D}'"
            + $" and project_id = '{world.Project:D}' order by code");

    /// <summary>The project's <c>authoring_version</c> row count.</summary>
    /// <param name="world">The world.</param>
    private async Task<int> VersionCountAsync(World world) =>
        (await _fixture.ColumnAsync<int>(
            world.Database,
            $"select count(*)::int from public.authoring_version where org_id = '{world.Org:D}'"
            + $" and project_id = '{world.Project:D}'")).Single();

    /// <summary>Runs one import with the host's statement log cleared, and answers how many statements it sent.</summary>
    /// <param name="world">The world.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="rows">The rows.</param>
    private static async Task<int> StatementsAsync(World world, Guid projectId, IReadOnlyList<ImportRow> rows)
    {
        world.Api.Statements.Clear();
        var result = await ImportAsync(world, projectId, rows);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        return world.Api.Statements.Snapshot().Count;
    }
}
