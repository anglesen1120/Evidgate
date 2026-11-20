using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Commands;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;

namespace Evidgate.Modules.Authoring.Application.Import;

/// <summary>What one imported row does to the project when the file is applied (13.1.1 AC13/AC15).</summary>
internal enum ImportOutcome
{
    /// <summary>The row names nothing stored yet: the entity is created (and its code allocated, when it named none).</summary>
    Create,

    /// <summary>The row names a stored entity whose snapshot differs: one <c>update</c> version row.</summary>
    Update,

    /// <summary>The row names a soft-deleted case: one <c>restore</c> version row re-applying the stored snapshot (AC8).</summary>
    Restore,

    /// <summary>The row describes exactly what is stored (same <c>content_hash</c>): nothing is written (AC15).</summary>
    Unchanged,
}

/// <summary>One row of a planned import: the decision phase 1 and phase 2 reached for it (13.1.1 AC13/AC15).</summary>
/// <param name="Row">The validated row.</param>
/// <param name="Outcome">Create, update, restore or unchanged.</param>
/// <param name="EntityId">The entity the row names: the stored id, or the id a create will use.</param>
/// <param name="Content">The snapshot the row describes, or null when nothing is written.</param>
/// <param name="RestoreSnapshot">
/// The stored snapshot of a soft-deleted case before the import restored it, or null when the row restores nothing.
/// </param>
/// <param name="Code">The case code the row carries after the import, or null for a node or item row.</param>
/// <param name="StoredVersion">
/// The entity's <c>version</c> as the planner read it; 0 for a create. The version rows of the apply are numbered from it.
/// </param>
/// <param name="StoredState">
/// The stored state of a matched item or case, or null for a node and for a create: the retired guard of the update path.
/// </param>
internal sealed record ImportPlannedRow(
    ImportValidatedRow Row,
    ImportOutcome Outcome,
    Guid EntityId,
    AuthoringSnapshot? Content,
    AuthoringSnapshot? RestoreSnapshot,
    string? Code,
    int StoredVersion,
    string? StoredState = null);

/// <summary>
/// One sibling group this file adds a row to: what the plan read and what it therefore assumed when it assigned the
/// positions of its new rows. The apply re-reads the same group under the advisory lock and refuses with the retryable
/// <c>503</c> when the two disagree, so an import can never write a position into a group that moved since the plan
/// (13.1.1 AC2/AC13).
/// </summary>
/// <param name="Kind">The sibling set's entity kind: a node group or an item group.</param>
/// <param name="ParentId">The group's parent, or <see cref="Guid.Empty"/> for the project's root node group.</param>
/// <param name="LiveCount">The number of live members the plan saw.</param>
/// <param name="Maximum">The highest position among them, or −1 for an empty group.</param>
internal sealed record ImportGroupExpectation(ImportRowKind Kind, Guid ParentId, int LiveCount, int Maximum);

/// <summary>What an item row's <c>item_key</c> resolved to: the entity, and the state it is (or will be) stored in.</summary>
/// <param name="Id">The item's id: stored, or the id its create will use.</param>
/// <param name="State">Its state after the row is applied.</param>
/// <param name="Stored">
/// True when the item exists in the project already, false when the same file creates it. The writer's retired-parent
/// rule is about a <em>stored</em> retired item taking new content, so a file that creates an item as retired together
/// with its cases still imports: that is exactly what a round trip of a live-but-retired item exports (AC14/AC15).
/// </param>
internal sealed record ResolvedItem(Guid Id, string State, bool Stored);

/// <summary>The answer of the resolution phase: what the file would change, or the issues that refuse it.</summary>
internal sealed class ImportPlan
{
    private ImportPlan(
        IReadOnlyList<ImportPlannedRow> rows,
        IReadOnlyList<ImportIssue> issues,
        int allocations,
        long? explicitCodeFloor,
        IReadOnlyList<ImportGroupExpectation> groups)
    {
        Rows = rows;
        Issues = issues;
        Allocations = allocations;
        ExplicitCodeFloor = explicitCodeFloor;
        Groups = groups;
    }

    /// <summary>One planned row per validated row, in file order.</summary>
    public IReadOnlyList<ImportPlannedRow> Rows { get; }

    /// <summary>The refusals; empty when the plan may be applied.</summary>
    public IReadOnlyList<ImportIssue> Issues { get; }

    /// <summary>The number of case rows this file creates without a code, i.e. the size of the one block to allocate.</summary>
    public int Allocations { get; }

    /// <summary>
    /// One past the highest number a row brought with it (<c>null</c> when no row created a case with an explicit code):
    /// the counter is raised to it so the next allocation cannot hand out a code the file already used (AC4).
    /// </summary>
    public long? ExplicitCodeFloor { get; }

    /// <summary>
    /// Every sibling group this file adds a row to, with the state the plan assigned its positions from: the apply
    /// re-reads them under the group's advisory lock (F5/F6) and answers the retryable <c>503</c> on a mismatch.
    /// </summary>
    public IReadOnlyList<ImportGroupExpectation> Groups { get; }

    /// <summary>True when the file may be applied (or reported as a dry run).</summary>
    public bool IsAccepted => Issues.Count == 0;

    /// <summary>The rows whose outcome writes a version row.</summary>
    public IEnumerable<ImportPlannedRow> Writes => Rows.Where(row =>
        row.Outcome is not ImportOutcome.Unchanged);

    /// <summary>An accepted plan.</summary>
    /// <param name="rows">One row per validated row, in file order.</param>
    /// <param name="allocations">The size of the one allocation block.</param>
    /// <param name="explicitCodeFloor">One past the highest explicit number, or null.</param>
    /// <param name="groups">The sibling groups this file adds to.</param>
    internal static ImportPlan Accepted(
        IReadOnlyList<ImportPlannedRow> rows,
        int allocations,
        long? explicitCodeFloor,
        IReadOnlyList<ImportGroupExpectation> groups) => new(rows, [], allocations, explicitCodeFloor, groups);

    /// <summary>A refused file; nothing is applied.</summary>
    /// <param name="issues">The issues that refuse it.</param>
    internal static ImportPlan Refused(IReadOnlyList<ImportIssue> issues) => new([], issues, 0, null, []);
}

/// <summary>
/// Phase 2 of an import (13.1.1 AC13/AC15): the row set is resolved against the project in a <b>bounded</b> number of
/// statements — three reads of the stored tree, one read of the hashes — whatever the file size, and every row gets one
/// outcome the apply then executes. Read-only: nothing here writes, allocates or bumps a counter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identity, per kind.</b> A case is identified by its <c>code</c> (<c>manual_test_case_code_key</c>, one code per
/// project), a node by <c>(resolved parent, node_kind, NFC title)</c> among the live siblings, an item by
/// <c>(resolved node, NFC title)</c> among the live siblings. The file's own <c>key</c>s only resolve parents; two rows
/// that describe one stored entity are the same row.
/// </para>
/// <para>
/// <b>"Unchanged" is a hash comparison.</b> The snapshot the row describes is hashed with the writer's own function
/// (<see cref="AuthoringHashes.ContentHash"/>, RFC 8785) and compared with the last version's stored
/// <c>content_hash</c>, so a re-import of the same tree appends no row at all (AC15) — and a row that differs by a single
/// code point does.
/// </para>
/// <para>
/// <b>The file never moves or reorders what is stored.</b> A stored row's position must equal the one the file names, a
/// stored case must still sit under the item the file names, and a taken position on a new row is refused; re-parenting
/// and reordering are routes 3, 4 and 19, and an import that guessed at them would rewrite a tree the owner arranged.
/// </para>
/// </remarks>
internal sealed partial class ImportPlanner(ITenantDb db)
{
    /// <summary>Resolves the validated rows against the project.</summary>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="rows">The validated rows, in file order.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async ValueTask<ImportPlan> PlanAsync(
        Guid orgId,
        Guid projectId,
        IReadOnlyList<ImportValidatedRow> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var issues = new List<ImportIssue>();
        var planned = new ImportPlannedRow?[rows.Count];
        var resolvedNodes = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var resolvedItems = new Dictionary<string, ResolvedItem>(StringComparer.Ordinal);
        var explicitFloor = (long?)null;

        // The live tree, one read: every node is matched by identity against it, so no per-row probe is needed.
        var liveNodes = await db.QueryAsync<ImportSql.NodeRow>(
            ImportSql.LiveNodes,
            new { org_id = orgId, project_id = projectId },
            cancellationToken).ConfigureAwait(false);

        var nodeIdentities = new Dictionary<(Guid? Parent, string Kind, string Title), ImportSql.NodeRow>();
        var nodeGroups = new Dictionary<Guid, ImportGroup>();

        foreach (var node in liveNodes)
        {
            nodeIdentities.TryAdd((node.ParentId, node.Kind, node.Title), node);
            Group(nodeGroups, node.ParentId).Live(node.Position);
        }

        Nodes(rows, planned, resolvedNodes, nodeIdentities, nodeGroups, issues);

        if (issues.Count != 0)
        {
            return ImportPlan.Refused([.. issues]);
        }

        var itemGroups = new Dictionary<Guid, ImportGroup>();

        await ItemsAsync(
            orgId,
            projectId,
            rows,
            planned,
            resolvedNodes,
            resolvedItems,
            itemGroups,
            issues,
            cancellationToken).ConfigureAwait(false);

        if (issues.Count != 0)
        {
            return ImportPlan.Refused([.. issues]);
        }

        explicitFloor = await CasesAsync(orgId, projectId, rows, planned, resolvedItems, issues, cancellationToken)
            .ConfigureAwait(false);

        if (issues.Count != 0)
        {
            return ImportPlan.Refused([.. issues]);
        }

        var outcomes = await DecideAsync(
            orgId,
            projectId,
            [.. planned.Select(row => row!)],
            resolvedItems,
            cancellationToken).ConfigureAwait(false);

        if (outcomes.Issues.Count != 0)
        {
            return ImportPlan.Refused(outcomes.Issues);
        }

        var allocations = outcomes.Rows.Count(row => row.Outcome == ImportOutcome.Create
            && row.Row.Kind == ImportRowKind.Case
            && row.Code is null);

        var groups = Expectations(ImportRowKind.Node, nodeGroups)
            .Concat(Expectations(ImportRowKind.Item, itemGroups))
            .ToArray();

        return ImportPlan.Accepted(outcomes.Rows, allocations, explicitFloor, groups);
    }

    /// <summary>Resolves every node row, parents first, against the live tree (AC13 identity by parent, kind and title).</summary>
    /// <param name="rows">The validated rows.</param>
    /// <param name="planned">The plan slots, one per validated row.</param>
    /// <param name="resolved">The node id each file key resolved to.</param>
    /// <param name="identities">The live nodes by identity.</param>
    /// <param name="groups">The sibling-position state of every live group.</param>
    /// <param name="issues">The refusals.</param>
    private static void Nodes(
        IReadOnlyList<ImportValidatedRow> rows,
        ImportPlannedRow?[] planned,
        Dictionary<string, Guid> resolved,
        Dictionary<(Guid? Parent, string Kind, string Title), ImportSql.NodeRow> identities,
        Dictionary<Guid, ImportGroup> groups,
        List<ImportIssue> issues)
    {
        // A parent always precedes its child in this order (the ladder fixes the depth of each kind), so resolving by
        // key needs no recursion and a file may list a child before its parent.
        var order = rows
            .Select((row, slot) => (Row: row, Slot: slot))
            .Where(entry => entry.Row.Kind == ImportRowKind.Node)
            .OrderBy(entry => entry.Row.Depth)
            .ThenBy(entry => entry.Slot)
            .ToArray();

        foreach (var (row, slot) in order)
        {
            var parentId = row.ParentKey is null ? (Guid?)null : resolved[row.ParentKey];
            var group = Group(groups, parentId);

            if (identities.TryGetValue((parentId, row.NodeKind!, row.Title), out var stored))
            {
                if (row.Position is { } named && named != stored.Position)
                {
                    // The stored node exists and the file names another position: that is route 4's reorder, not an import.
                    Issue(issues, row.Source.Index, "/position", "value_invalid");
                    continue;
                }

                var unchanged = new FeatureNodeSnapshot(stored.Kind, row.Title, stored.Position, parentId);
                planned[slot] = new ImportPlannedRow(row, ImportOutcome.Unchanged, stored.Id, unchanged, null, null, stored.Version);
                resolved[row.Key] = stored.Id;
                continue;
            }

            if (group.LiveCount + group.AddedCount >= NodeLimits.MaximumSiblingsPerGroup)
            {
                Issue(issues, row.Source.Index, "/parent_key", "value_invalid");
                continue;
            }

            if (!group.Next(row.Position, out var position))
            {
                Issue(issues, row.Source.Index, "/position", "value_invalid");
                continue;
            }

            var id = Guid.CreateVersion7();
            var content = new FeatureNodeSnapshot(row.NodeKind!, row.Title, position, parentId);
            planned[slot] = new ImportPlannedRow(row, ImportOutcome.Create, id, content, null, null, 0);
            resolved[row.Key] = id;
        }
    }

    /// <summary>Resolves every checklist item row against the live items of the nodes it names (AC13).</summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="rows">The validated rows.</param>
    /// <param name="planned">The plan slots.</param>
    /// <param name="resolvedNodes">The node id each file key resolved to.</param>
    /// <param name="resolvedItems">The item id and state each file key resolved to.</param>
    /// <param name="groups">The sibling-position state of every item group this file touches.</param>
    /// <param name="issues">The refusals.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask ItemsAsync(
        Guid orgId,
        Guid projectId,
        IReadOnlyList<ImportValidatedRow> rows,
        ImportPlannedRow?[] planned,
        Dictionary<string, Guid> resolvedNodes,
        Dictionary<string, ResolvedItem> resolvedItems,
        Dictionary<Guid, ImportGroup> groups,
        List<ImportIssue> issues,
        CancellationToken cancellationToken)
    {
        var order = rows
            .Select((row, slot) => (Row: row, Slot: slot))
            .Where(entry => entry.Row.Kind == ImportRowKind.Item)
            .ToArray();

        if (order.Length == 0)
        {
            return;
        }

        // Only the nodes this file names: an item identity is scoped to its node, so the read never scans the project's
        // whole item table.
        var nodeIds = order
            .Select(entry => resolvedNodes[entry.Row.ParentKey!])
            .Distinct()
            .ToArray();

        var liveItems = await db.QueryAsync<ImportSql.ItemRow>(
            ImportSql.LiveItems,
            new { org_id = orgId, project_id = projectId, node_ids = nodeIds },
            cancellationToken).ConfigureAwait(false);

        var identities = new Dictionary<(Guid NodeId, string Title), ImportSql.ItemRow>();

        foreach (var item in liveItems)
        {
            identities.TryAdd((item.FeatureNodeId, item.Title), item);
            Group(groups, item.FeatureNodeId).Live(item.Position);
        }

        foreach (var (row, slot) in order)
        {
            var nodeId = resolvedNodes[row.ParentKey!];
            var group = Group(groups, nodeId);

            if (identities.TryGetValue((nodeId, row.Title), out var stored))
            {
                if (row.Position is { } named && named != stored.Position)
                {
                    // Route 7 owns the item position; an import that renumbered a stored item would rewrite the table.
                    Issue(issues, row.Source.Index, "/position", "value_invalid");
                    continue;
                }

                if (MatchedState(
                    row,
                    stored.State,
                    (from, to) => ChecklistItemStateMachine.Decide(from, to, null),
                    issues) is not { } state)
                {
                    continue;
                }

                var unchanged = new ChecklistItemSnapshot(
                    nodeId,
                    row.Title,
                    row.Description,
                    row.Priority,
                    state,
                    stored.Position);
                planned[slot] = new ImportPlannedRow(
                    row,
                    ImportOutcome.Unchanged,
                    stored.Id,
                    unchanged,
                    null,
                    null,
                    stored.Version,
                    stored.State);
                resolvedItems[row.Key] = new ResolvedItem(stored.Id, state, Stored: true);
                continue;
            }

            if (!group.Next(row.Position, out var position))
            {
                Issue(issues, row.Source.Index, "/position", "value_invalid");
                continue;
            }

            // A new row takes the file's state as written (an empty cell already became the kind's default in the
            // validator), the deliberate decision N1 of the T5 security delta: an export writes every row's real state, so
            // a project whose item is retired must come back retired, or the export could not be re-imported. The writer
            // takes the same state on create (`ImportApplySql.InsertEntities`), and a *stored* row is the one whose state
            // may only move through §6 (`MatchedState` below).
            var id = Guid.CreateVersion7();
            var content = new ChecklistItemSnapshot(nodeId, row.Title, row.Description, row.Priority, row.State, position);
            planned[slot] = new ImportPlannedRow(row, ImportOutcome.Create, id, content, null, null, 0);
            resolvedItems[row.Key] = new ResolvedItem(id, row.State, Stored: false);
        }
    }
}
