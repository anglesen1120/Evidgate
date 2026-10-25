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
internal sealed class ImportPlanner(ITenantDb db)
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

            var id = Guid.CreateVersion7();
            var content = new ChecklistItemSnapshot(nodeId, row.Title, row.Description, row.Priority, row.State, position);
            planned[slot] = new ImportPlannedRow(row, ImportOutcome.Create, id, content, null, null, 0);
            resolvedItems[row.Key] = new ResolvedItem(id, row.State, Stored: false);
        }
    }

    /// <summary>Resolves every case row by its <c>code</c> (AC15), including the soft-deleted ones a restore brings back.</summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="rows">The validated rows.</param>
    /// <param name="planned">The plan slots.</param>
    /// <param name="resolvedItems">The item id and state each file key resolved to.</param>
    /// <param name="issues">The refusals.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One past the highest number the file brought, or null when it brought none.</returns>
    private async ValueTask<long?> CasesAsync(
        Guid orgId,
        Guid projectId,
        IReadOnlyList<ImportValidatedRow> rows,
        ImportPlannedRow?[] planned,
        Dictionary<string, ResolvedItem> resolvedItems,
        List<ImportIssue> issues,
        CancellationToken cancellationToken)
    {
        // Returned rather than accumulated through a `ref`: an async method cannot take one (CS1988), and the value is
        // a plain result of the read this method performs.
        var explicitFloor = (long?)null;

        var order = rows
            .Select((row, slot) => (Row: row, Slot: slot))
            .Where(entry => entry.Row.Kind == ImportRowKind.Case)
            .ToArray();

        if (order.Length == 0)
        {
            return explicitFloor;
        }

        var codes = order
            .Where(entry => entry.Row.Code is not null)
            .Select(entry => CanonicalCode(entry.Row.Code!))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var storedByCode = new Dictionary<string, ImportSql.CaseRow>(StringComparer.Ordinal);

        if (codes.Length != 0)
        {
            var stored = await db.QueryAsync<ImportSql.CaseRow>(
                ImportSql.CasesByCode,
                new { org_id = orgId, project_id = projectId, codes = new ImportSql.TextArrayParameter(codes) },
                cancellationToken).ConfigureAwait(false);

            foreach (var row in stored)
            {
                storedByCode[row.Code] = row;
            }
        }

        foreach (var (row, slot) in order)
        {
            var itemId = resolvedItems[row.ItemKey!].Id;

            if (row.Code is null)
            {
                // A fresh number comes from the file's one allocation block, filled in by the importer before any write;
                // the empty code is a sentinel that the case CHECK would refuse, so a fill that ever failed loudly fails
                // here instead of storing a number nobody allocated.
                var content = new ManualTestCaseSnapshot(
                    itemId,
                    string.Empty,
                    row.Title,
                    row.Description,
                    row.Precondition,
                    row.StepsJson!,
                    row.Priority,
                    row.State);
                planned[slot] = new ImportPlannedRow(row, ImportOutcome.Create, Guid.CreateVersion7(), content, null, null, 0);
                continue;
            }

            var code = CanonicalCode(row.Code);

            if (!storedByCode.TryGetValue(code, out var stored))
            {
                // An explicit, unknown code: created with that code, and the counter is raised past it so the next
                // allocation cannot collide with it (AC4).
                var parsed = AuthoringCode.TryParse(code, out var value) ? value : throw new InvalidOperationException(
                    "the validator accepted a code outside the grammar");
                explicitFloor = explicitFloor is { } floor ? Math.Max(floor, parsed.Number + 1) : parsed.Number + 1;

                var content = new ManualTestCaseSnapshot(
                    itemId,
                    code,
                    row.Title,
                    row.Description,
                    row.Precondition,
                    row.StepsJson!,
                    row.Priority,
                    row.State);
                planned[slot] = new ImportPlannedRow(row, ImportOutcome.Create, Guid.CreateVersion7(), content, null, code, 0);
                continue;
            }

            if (stored.ChecklistItemId != itemId)
            {
                // Route 19 is the one path that moves a case; an import that re-parented it would take the case out of the
                // item the file names without the owner ever asking.
                Issue(issues, row.Source.Index, "/item_key", "value_invalid");
                continue;
            }

            // A restore always comes back in §6's restore state (draft), whatever the file's state cell says: the writer's
            // restore forces it (AC21) and an import must not be the way around the review that a state carries.
            if (stored.DeletedAt is not null)
            {
                var restored = new ManualTestCaseSnapshot(
                    itemId,
                    stored.Code,
                    row.Title,
                    row.Description,
                    row.Precondition,
                    row.StepsJson!,
                    row.Priority,
                    ManualTestCaseStateMachine.RestoreState);

                planned[slot] = new ImportPlannedRow(
                    row,
                    ImportOutcome.Restore,
                    stored.Id,
                    restored,
                    Restored(stored),
                    stored.Code,
                    stored.Version,
                    stored.State);
                continue;
            }

            if (MatchedState(
                row,
                stored.State,
                (from, to) => ManualTestCaseStateMachine.Decide(from, to, null),
                issues) is not { } state)
            {
                continue;
            }

            var desired = new ManualTestCaseSnapshot(
                itemId,
                stored.Code,
                row.Title,
                row.Description,
                row.Precondition,
                row.StepsJson!,
                row.Priority,
                state);

            planned[slot] = new ImportPlannedRow(
                row,
                ImportOutcome.Unchanged,
                stored.Id,
                desired,
                null,
                stored.Code,
                stored.Version,
                stored.State);
        }

        return explicitFloor;
    }

    /// <summary>Compares every planned snapshot with the stored <c>content_hash</c> and fixes each row's outcome (AC15).</summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="rows">One planned row per validated row, in file order.</param>
    /// <param name="resolvedItems">The item id and state each file key resolved to.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<PlanOutcome> DecideAsync(
        Guid orgId,
        Guid projectId,
        IReadOnlyList<ImportPlannedRow> rows,
        Dictionary<string, ResolvedItem> resolvedItems,
        CancellationToken cancellationToken)
    {
        var matched = rows.Where(row => row.StoredVersion != 0).ToArray();

        // One kind and one id per matched row, in the same order: the read pairs the two arrays positionally, so a
        // distinct-kind set zipped against a distinct-id set would ask for the wrong entities (Perf F1) and a re-import
        // of more than three matched rows would report every one of them as an update.
        var kinds = matched.Select(row => AuthoringWireValues.EntityKindName(KindOf(row.Row.Kind))).ToArray();
        var ids = matched.Select(row => row.EntityId).ToArray();

        var hashes = matched.Length == 0
            ? []
            : await db.QueryAsync<ImportSql.VersionHashRow>(
                ImportSql.LastContentHashes,
                new
                {
                    org_id = orgId,
                    project_id = projectId,
                    kinds = new ImportSql.TextArrayParameter(kinds),
                    ids,
                },
                cancellationToken).ConfigureAwait(false);

        var storedHashes = new Dictionary<(string Kind, Guid Id), string>();

        foreach (var hash in hashes)
        {
            storedHashes[(hash.EntityKind, hash.EntityId)] = hash.ContentHash;
        }

        var decided = new List<ImportPlannedRow>(rows.Count);
        var issues = new List<ImportIssue>();

        foreach (var row in rows)
        {
            if (row.StoredVersion == 0)
            {
                if (WritesUnderRetiredItem(row, resolvedItems, ImportOutcome.Create))
                {
                    RefuseRetiredItem(issues, row);
                    continue;
                }

                decided.Add(row);
                continue;
            }

            if (row.Content is null)
            {
                decided.Add(row);
                continue;
            }

            var kind = AuthoringWireValues.EntityKindName(KindOf(row.Row.Kind));
            var same = storedHashes.TryGetValue((kind, row.EntityId), out var storedHash)
                && string.Equals(storedHash, AuthoringHashes.ContentHash(row.Content), StringComparison.Ordinal);

            if (row.RestoreSnapshot is not null)
            {
                // A soft-deleted case comes back through one restore version row and one statement, which writes the
                // file's content and clears deleted_at. Never "unchanged": the row is still deleted. Folding the content
                // into the restore keeps one version row per entity per import — the entity's version is trigger-owned
                // and bumps once — and that is what makes the next identical import converge on `unchanged` (AC15).
                if (WritesUnderRetiredItem(row, resolvedItems, ImportOutcome.Restore))
                {
                    RefuseRetiredItem(issues, row);
                    continue;
                }

                decided.Add(row);
                continue;
            }

            if (!same && IsRetired(row))
            {
                // Every route that edits an item or a case goes through AuthoringStateRules.CheckEditable, which refuses
                // a terminal row; a set-based update has no such check of its own, so the planner applies it here.
                // Identical content stays `unchanged`, so importing a retired row back changes nothing.
                Issue(issues, row.Row.Source.Index, "/state", "value_invalid");
                continue;
            }

            var outcome = same ? ImportOutcome.Unchanged : ImportOutcome.Update;

            if (WritesUnderRetiredItem(row, resolvedItems, outcome))
            {
                RefuseRetiredItem(issues, row);
                continue;
            }

            decided.Add(same
                ? row with { Outcome = ImportOutcome.Unchanged, Content = null }
                : row with { Outcome = ImportOutcome.Update });
        }

        return new PlanOutcome(decided, issues);
    }

    /// <summary>
    /// True when a case row would write content under a checklist item that is <em>stored</em> in its terminal state. The
    /// writer refuses that placement — <see cref="AuthoringVersionWriter.CheckCaseParentAsync"/>: "a retired checklist
    /// item takes no new content; restore it first" — and an import must not be the way around it. A row that writes
    /// nothing (an unchanged row of a round trip) is not a placement and stays accepted, and an item this same file
    /// creates as retired is not a stored one: refusing that would break the round trip of a live-but-retired item and
    /// its cases, which is exactly what the exporter emits (AC14/AC15).
    /// </summary>
    /// <param name="row">The planned row.</param>
    /// <param name="resolvedItems">The item each file key resolved to, with its state and whether it is stored.</param>
    /// <param name="outcome">The outcome the row is heading for.</param>
    private static bool WritesUnderRetiredItem(
        ImportPlannedRow row,
        Dictionary<string, ResolvedItem> resolvedItems,
        ImportOutcome outcome) =>
        outcome != ImportOutcome.Unchanged
        && row.Row.Kind == ImportRowKind.Case
        && row.Row.ItemKey is { } itemKey
        && resolvedItems.TryGetValue(itemKey, out var item)
        && item.Stored
        && string.Equals(item.State, ChecklistItemStateMachine.Retired, StringComparison.Ordinal);

    /// <summary>Refuses a case row whose item is retired, on the column that names the item.</summary>
    /// <param name="issues">The refusals.</param>
    /// <param name="row">The refused row.</param>
    private static void RefuseRetiredItem(List<ImportIssue> issues, ImportPlannedRow row) =>
        Issue(issues, row.Row.Source.Index, "/item_key", "value_invalid");

    /// <summary>
    /// The state a matched row writes (13.1.1 §6, AC13): the stored one when the file's <c>state</c> cell was empty, the
    /// file's own when the entity's state machine allows exactly that move, and <c>null</c> — with the issue counted —
    /// when it does not. The import carries no reason, so a move into <c>retired</c> (which needs one) is refused here
    /// too: an import may not be the path that retires a row without a reason.
    /// </summary>
    /// <param name="row">The matched row.</param>
    /// <param name="stored">The state the entity is stored in.</param>
    /// <param name="decide">The entity's own <c>Decide</c>, with the reason bound to null.</param>
    /// <param name="issues">The refusals.</param>
    private static string? MatchedState(
        ImportValidatedRow row,
        string stored,
        Func<string, string, AuthoringTransition> decide,
        List<ImportIssue> issues)
    {
        if (!row.StateGiven)
        {
            // An export that leaves the state column empty describes the stored state, never the creation default: this
            // is what keeps an import from resetting an approved case to draft (Perf/security F2, findings A/F4).
            return stored;
        }

        var transition = decide(stored, row.State);

        switch (transition.Outcome)
        {
            case AuthoringTransitionOutcome.Allowed:
            case AuthoringTransitionOutcome.Unchanged:
                return transition.ToState;

            default:
                // A move outside §6's table, a move out of `retired`, and a move into `retired` without a reason are one
                // refusal on the state column in the import's vocabulary.
                Issue(issues, row.Source.Index, "/state", "value_invalid");
                return null;
        }
    }

    /// <summary>True when a matched item or case is stored in its terminal state, which no update path may edit.</summary>
    /// <param name="row">The planned row.</param>
    private static bool IsRetired(ImportPlannedRow row) =>
        row.Row.Kind != ImportRowKind.Node
        && row.StoredState is { } state
        && string.Equals(
            state,
            row.Row.Kind == ImportRowKind.Case
                ? ManualTestCaseStateMachine.Retired
                : ChecklistItemStateMachine.Retired,
            StringComparison.Ordinal);

    /// <summary>The stored snapshot of a soft-deleted case (its state is §6's restore state, draft): the marker that makes
    /// the row a restore, and what the apply's lock read proves is still deleted when it runs.</summary>
    /// <param name="stored">The soft-deleted case.</param>
    private static ManualTestCaseSnapshot Restored(ImportSql.CaseRow stored) => new(
        stored.ChecklistItemId,
        stored.Code,
        stored.Title,
        stored.Description,
        stored.Precondition,
        stored.Steps,
        stored.Priority,
        ManualTestCaseStateMachine.RestoreState);

    /// <summary>The stored form of a case code: the number is formatted with its minimum digits, so a file's <c>SHOP-0007</c>
    /// matches the stored <c>SHOP-007</c>.</summary>
    /// <param name="code">The code as the file wrote it.</param>
    private static string CanonicalCode(string code) =>
        AuthoringCode.TryParse(code, out var parsed)
            ? parsed.Value
            : throw new InvalidOperationException("the validator accepted a code outside the grammar");

    /// <summary>The entity kind of a row kind.</summary>
    /// <param name="kind">The row kind.</param>
    private static AuthoringEntityKind KindOf(ImportRowKind kind) => kind switch
    {
        ImportRowKind.Node => AuthoringEntityKind.FeatureNode,
        ImportRowKind.Item => AuthoringEntityKind.ChecklistItem,
        ImportRowKind.Case => AuthoringEntityKind.ManualTestCase,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown import row kind"),
    };

    /// <summary>The position state of one sibling group, live members and the rows this file adds.</summary>
    /// <param name="groups">The groups seen so far, keyed by parent id.</param>
    /// <param name="parent">The parent, or null for the project's root group.</param>
    /// <remarks>
    /// The key is a plain <see cref="Guid"/> because a nullable key is a nullable-struct key to the dictionary contract
    /// (CS8714, and a warning here is an error): the root group is <see cref="Guid.Empty"/>, which no entity id is
    /// (<c>Guid.CreateVersion7</c> never yields it) and which no row can name as a parent.
    /// </remarks>
    private static ImportGroup Group(Dictionary<Guid, ImportGroup> groups, Guid? parent)
    {
        var key = parent ?? Guid.Empty;

        if (!groups.TryGetValue(key, out var group))
        {
            group = new ImportGroup();
            groups[key] = group;
        }

        return group;
    }

    /// <summary>The groups this file adds a row to, as the apply re-reads them under the advisory lock (finding F).</summary>
    /// <param name="kind">The entity kind of the group.</param>
    /// <param name="groups">The groups of one phase, keyed by parent id.</param>
    /// <remarks>
    /// A group the file only reads (it adds nothing to it) carries no assumption the apply could invalidate, so it is not
    /// an expectation: the apply locks and re-reads exactly what a file writes into.
    /// </remarks>
    private static IEnumerable<ImportGroupExpectation> Expectations(
        ImportRowKind kind,
        Dictionary<Guid, ImportGroup> groups) =>
        groups
            .Where(entry => entry.Value.AddedCount != 0)
            .OrderBy(entry => entry.Key)
            .Select(entry => new ImportGroupExpectation(kind, entry.Key, entry.Value.LiveCount, entry.Value.LiveMaximum));

    /// <summary>Counts one issue, bounded like the validation phase.</summary>
    /// <param name="issues">The refusals.</param>
    /// <param name="index">The row's file position.</param>
    /// <param name="path">The fixed column pointer.</param>
    /// <param name="reason">One of the AC13 reasons.</param>
    private static void Issue(List<ImportIssue> issues, int index, string path, string reason)
    {
        if (issues.Count < ImportValidator.MaximumIssues)
        {
            issues.Add(new ImportIssue(index, path, reason));
        }
    }

    /// <summary>The decided rows and the issues of the hash pass.</summary>
    /// <param name="Rows">One row per validated row, in file order.</param>
    /// <param name="Issues">Always empty today; kept so the caller has one shape to answer with.</param>
    private sealed record PlanOutcome(IReadOnlyList<ImportPlannedRow> Rows, IReadOnlyList<ImportIssue> Issues);

    /// <summary>
    /// One sibling group's positions: the live members read from the database and the rows this file adds, so an append
    /// lands at <c>max + 1</c> and an explicit position that is already held is refused instead of colliding in the
    /// unique index of AC2.
    /// </summary>
    private sealed class ImportGroup
    {
        private readonly HashSet<int> taken = [];

        /// <summary>The number of live members of the group.</summary>
        public int LiveCount { get; private set; }

        /// <summary>The number of rows this file adds to the group.</summary>
        public int AddedCount { get; private set; }

        /// <summary>
        /// The highest position among the group's <b>live</b> members, or −1 for an empty group: what the apply must still
        /// find when it re-reads the group under the advisory lock (finding F). It is deliberately separate from
        /// <see cref="Maximum"/>: the assignment cursor moves as this file's own rows take positions, and comparing that
        /// against the live state would make every importing append look like a change by another request.
        /// </summary>
        public int LiveMaximum { get; private set; } = -1;

        /// <summary>The highest position the group holds, live or added by this file: the append cursor.</summary>
        public int Maximum { get; private set; } = -1;

        /// <summary>Records one live member.</summary>
        /// <param name="position">Its stored position.</param>
        public void Live(int position)
        {
            LiveCount++;
            taken.Add(position);
            Maximum = Math.Max(Maximum, position);
            LiveMaximum = Math.Max(LiveMaximum, position);
        }

        /// <summary>Assigns the position of a row this file adds: the named one when it is free, else the append position.</summary>
        /// <param name="position">The position the row named, or null.</param>
        /// <param name="assigned">The assigned position.</param>
        public bool Next(int? position, out int assigned)
        {
            if (position is { } named)
            {
                if (!taken.Add(named))
                {
                    assigned = named;
                    return false;
                }

                Maximum = Math.Max(Maximum, named);
                assigned = named;
                AddedCount++;
                return true;
            }

            assigned = Maximum + 1;
            taken.Add(assigned);
            Maximum = assigned;
            AddedCount++;
            return true;
        }
    }
}
