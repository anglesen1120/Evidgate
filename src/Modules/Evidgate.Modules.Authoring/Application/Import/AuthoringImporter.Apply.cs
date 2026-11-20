using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;

namespace Evidgate.Modules.Authoring.Application.Import;

internal sealed partial class AuthoringImporter
{
    /// <summary>
    /// Phase 5: the set-based apply, in the writer-wide lock order — sibling-group advisory lock, then row locks by rank
    /// (<c>feature_node</c> → <c>checklist_item</c> → <c>manual_test_case</c>) ascending by id — then the writes.
    /// </summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="batchId">The one batch id of every version row.</param>
    /// <param name="actorMembershipId">The authoring member, or null.</param>
    /// <param name="rows">The decided rows, in file order.</param>
    /// <param name="groups">The sibling groups the plan assigned positions in (finding F).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<Result<bool>> ApplyAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        IReadOnlyList<ImportPlannedRow> rows,
        IReadOnlyList<ImportGroupExpectation> groups,
        CancellationToken cancellationToken)
    {
        // The writer-wide bound (§5): a holder that never commits answers the retryable 503 instead of parking the import.
        await db.ExecuteAsync(AuthoringSql.SetLockTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

        var createdNodes = rows
            .Where(row => row.Outcome == ImportOutcome.Create && row.Row.Kind == ImportRowKind.Node)
            .Select(row =>
            {
                var snapshot = (FeatureNodeSnapshot)row.Content!;
                return new NodeInsertRow(row.EntityId, snapshot.ParentId, snapshot.Kind, snapshot.Title, snapshot.Position);
            })
            .ToArray();

        var createdItems = rows
            .Where(row => row.Outcome == ImportOutcome.Create && row.Row.Kind == ImportRowKind.Item)
            .Select(row =>
            {
                var snapshot = (ChecklistItemSnapshot)row.Content!;
                return new ItemInsertRow(
                    row.EntityId,
                    snapshot.FeatureNodeId,
                    snapshot.Title,
                    snapshot.Description,
                    snapshot.Priority,
                    snapshot.State,
                    snapshot.Position);
            })
            .ToArray();

        var createdCases = rows
            .Where(row => row.Outcome == ImportOutcome.Create && row.Row.Kind == ImportRowKind.Case)
            .Select(row =>
            {
                var snapshot = (ManualTestCaseSnapshot)row.Content!;
                return new CaseInsertRow(
                    row.EntityId,
                    snapshot.ChecklistItemId,
                    snapshot.Code,
                    snapshot.Title,
                    snapshot.Description,
                    snapshot.Precondition,
                    snapshot.Steps,
                    snapshot.Priority,
                    snapshot.State);
            })
            .ToArray();

        // The advisory lock of every group this file adds to, before any row lock (the create, move and reorder paths take
        // the same key), so an import cannot meet a parked position of a concurrent renumber.
        var groupKeys = createdNodes
            .Select(node => AuthoringLockGuard.SiblingGroupKey(projectId, node.ParentId, AuthoringLockGuard.SiblingGroup.FeatureNode))
            .Concat(createdItems.Select(item => AuthoringLockGuard.SiblingGroupKey(
                projectId,
                item.FeatureNodeId,
                AuthoringLockGuard.SiblingGroup.ChecklistItem)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (groupKeys.Length != 0)
        {
            await db.ExecuteAsync(
                ImportApplySql.LockSiblingGroups,
                new { group_keys = new ImportSql.TextArrayParameter(groupKeys) },
                cancellationToken).ConfigureAwait(false);
        }

        // Under the locks, every group this file adds to must still hold what the plan read: a count or a highest position
        // that moved since the resolution read means another writer renumbered or added to the same sibling set, so the
        // positions this file assigned are no longer append positions. Retryable, and nothing has been written yet
        // (finding F).
        if (!await GroupsUnchangedAsync(orgId, projectId, groups, cancellationToken).ConfigureAwait(false))
        {
            return AuthoringErrors.Fail<bool>(AuthoringErrors.Unavailable());
        }

        var createdIds = rows
            .Where(row => row.Outcome == ImportOutcome.Create)
            .Select(row => row.EntityId)
            .ToHashSet();

        // Rank order, ascending id, one statement per rank. A parent this file creates is skipped: it does not exist yet
        // and the same transaction inserts it before its child.
        var nodeIds = LockSet(
            rows,
            ImportRowKind.Node,
            createdIds,
            createdNodes.Select(node => node.ParentId),
            createdItems.Select(item => (Guid?)item.FeatureNodeId));
        // Every parent item this file's cases hang under, created cases and update/restore cases alike: a case may only be
        // written under a live, non-retired item, so the lock set has to carry the parent itself and not just the item row
        // the file edits (N1/N3). The parents this file does not write are locked `for share` below, never `for update`.
        var caseParents = createdCases
            .Select(item => (Guid?)item.ChecklistItemId)
            .Concat(rows
                .Where(row => row.Row.Kind == ImportRowKind.Case
                    && row.Outcome is ImportOutcome.Update or ImportOutcome.Restore)
                .Select(row => (Guid?)((ManualTestCaseSnapshot)row.Content!).ChecklistItemId));

        var itemIds = LockSet(
            rows,
            ImportRowKind.Item,
            createdIds,
            caseParents,
            []);
        var caseIds = LockSet(rows, ImportRowKind.Case, createdIds, [], []);

        var locked = await LockRanksAsync(orgId, projectId, rows, nodeIds, itemIds, caseIds, cancellationToken)
            .ConfigureAwait(false);

        if (!locked.IsSuccess)
        {
            return locked;
        }

        var outcome = await InsertAsync(orgId, projectId, ImportApplySql.InsertEntities(AuthoringEntityKind.FeatureNode), null, createdNodes, cancellationToken)
            .ConfigureAwait(false)
            ?? await InsertAsync(orgId, projectId, ImportApplySql.InsertEntities(AuthoringEntityKind.ChecklistItem), AuthoringWireValues.ImportSource, createdItems, cancellationToken)
                .ConfigureAwait(false)
            ?? await InsertAsync(orgId, projectId, ImportApplySql.InsertEntities(AuthoringEntityKind.ManualTestCase), AuthoringWireValues.ImportSource, createdCases, cancellationToken)
                .ConfigureAwait(false);

        if (outcome is not null)
        {
            return AuthoringErrors.Fail<bool>(outcome);
        }

        // A restore writes the file's content as well as clearing deleted_at, in the same one statement, so the row that
        // comes back is exactly the row the file describes (AC8 records it, AC15 converges on it).
        var restores = rows
            .Where(row => row.Outcome == ImportOutcome.Restore && row.Row.Kind == ImportRowKind.Case)
            .Select(CaseUpdateRowOf)
            .ToArray();

        outcome = await UpdateAsync(orgId, projectId, ImportApplySql.RestoreCases, restores, cancellationToken)
            .ConfigureAwait(false);

        if (outcome is not null)
        {
            return AuthoringErrors.Fail<bool>(outcome);
        }

        var updatedItems = rows
            .Where(row => row.Outcome == ImportOutcome.Update && row.Row.Kind == ImportRowKind.Item)
            .Select(row =>
            {
                var snapshot = (ChecklistItemSnapshot)row.Content!;
                return new ItemUpdateRow(
                    row.EntityId,
                    snapshot.Title,
                    snapshot.Description,
                    snapshot.Priority,
                    snapshot.State,
                    snapshot.Position);
            })
            .ToArray();

        outcome = await UpdateAsync(
            orgId,
            projectId,
            ImportApplySql.UpdateEntities(AuthoringEntityKind.ChecklistItem),
            updatedItems,
            cancellationToken).ConfigureAwait(false);

        if (outcome is not null)
        {
            return AuthoringErrors.Fail<bool>(outcome);
        }

        var updatedCases = rows
            .Where(row => row.Outcome == ImportOutcome.Update && row.Row.Kind == ImportRowKind.Case)
            .Select(CaseUpdateRowOf)
            .ToArray();

        outcome = await UpdateAsync(
            orgId,
            projectId,
            ImportApplySql.UpdateEntities(AuthoringEntityKind.ManualTestCase),
            updatedCases,
            cancellationToken).ConfigureAwait(false);

        if (outcome is not null)
        {
            return AuthoringErrors.Fail<bool>(outcome);
        }

        return await AppendVersionsAsync(orgId, projectId, batchId, actorMembershipId, rows, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The ids one rank's lock statement covers: every row of that kind the apply mutates, plus the parents of the rows it
    /// creates that already exist. A parent this file creates is left out — the same transaction inserts it first.
    /// </summary>
    /// <param name="rows">The decided rows.</param>
    /// <param name="kind">The rank's kind.</param>
    /// <param name="createdIds">The ids this file creates.</param>
    /// <param name="first">The first parent set the rank covers.</param>
    /// <param name="second">The second parent set the rank covers.</param>
    private static Guid[] LockSet(
        IReadOnlyList<ImportPlannedRow> rows,
        ImportRowKind kind,
        HashSet<Guid> createdIds,
        IEnumerable<Guid?> first,
        IEnumerable<Guid?> second) =>
        rows
            .Where(row => row.Row.Kind == kind
                && row.Outcome is ImportOutcome.Update or ImportOutcome.Restore)
            .Select(row => row.EntityId)
            .Concat(first.OfType<Guid>())
            .Concat(second.OfType<Guid>())
            .Where(id => !createdIds.Contains(id))
            .Distinct()
            .Order()
            .ToArray();

    /// <summary>Locks every rank in order and proves each planned row is still at the version the plan resolved.</summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="rows">The decided rows.</param>
    /// <param name="nodeIds">The rank-0 lock set.</param>
    /// <param name="itemIds">The rank-1 lock set.</param>
    /// <param name="caseIds">The rank-2 lock set.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<Result<bool>> LockRanksAsync(
        Guid orgId,
        Guid projectId,
        IReadOnlyList<ImportPlannedRow> rows,
        Guid[] nodeIds,
        Guid[] itemIds,
        Guid[] caseIds,
        CancellationToken cancellationToken)
    {
        // Items split by mode: the ones this file writes are locked `for update`, the parent items it only reads `for share`
        // (delta R1). Two statements, and no inversion is possible: every transaction that touches the item table takes at
        // most one of the two modes — the batch writer takes `for share` only (AuthoringVersionWriter.LockBatchAsync), the
        // item routes take `for update` only — and this one takes all of its `for update` ids (ascending) before any
        // `for share` id (ascending), so no second transaction can take item share before item update.
        var writtenItems = rows
            .Where(row => row.Row.Kind == ImportRowKind.Item && row.Outcome is ImportOutcome.Update or ImportOutcome.Restore)
            .Select(row => row.EntityId)
            .ToHashSet();

        var ranks = new (AuthoringEntityKind Kind, Guid[] Ids, bool Share)[]
        {
            (AuthoringEntityKind.FeatureNode, nodeIds, false),
            (AuthoringEntityKind.ChecklistItem, [.. itemIds.Where(writtenItems.Contains)], false),
            (AuthoringEntityKind.ChecklistItem, [.. itemIds.Where(id => !writtenItems.Contains(id))], true),
            (AuthoringEntityKind.ManualTestCase, caseIds, false),
        };

        foreach (var (kind, ids, share) in ranks)
        {
            if (ids.Length == 0)
            {
                continue;
            }

            var locked = await db.QueryAsync<ImportApplySql.LockedRow>(
                ImportApplySql.LockEntities(kind, share),
                new { org_id = orgId, project_id = projectId, ids },
                cancellationToken).ConfigureAwait(false);

            if (locked.Count != ids.Length)
            {
                // A parent of a created row, or a row to update, is gone: a concurrent delete of something this file
                // depends on. Retryable, and nothing has been written yet.
                return AuthoringErrors.Fail<bool>(AuthoringErrors.Unavailable());
            }

            var stored = locked.ToDictionary(row => row.Id);
            var rowKind = kind switch
            {
                AuthoringEntityKind.FeatureNode => ImportRowKind.Node,
                AuthoringEntityKind.ChecklistItem => ImportRowKind.Item,
                _ => ImportRowKind.Case,
            };

            if (rowKind != ImportRowKind.Case
                && locked.Any(row => row.DeletedAt is not null))
            {
                // Only a *parent* can be locked and deleted: a soft-deleted node is not in the live tree the plan read, so
                // it is never an update target, and the same holds for an item. A row created or restored under it would
                // hang from a parent the project no longer lists: the plan is stale, and a retry after the delete settles
                // answers truthfully (finding D).
                return AuthoringErrors.Fail<bool>(AuthoringErrors.Unavailable());
            }

            if (rowKind == ImportRowKind.Item
                && locked.Any(row => string.Equals(
                    row.State,
                    ChecklistItemStateMachine.Retired,
                    StringComparison.Ordinal)))
            {
                // The writer's rule — "a retired checklist item takes no new content; restore it first"
                // (AuthoringVersionWriter.CheckCaseParentAsync) — re-checked under the lock: the plan saw a live item, and a
                // concurrent retire would otherwise let this file create, restore or update a case under a terminal item.
                // An unchanged row is never in a lock set, so a round trip of a retired item still imports (N1/N3).
                return AuthoringErrors.Fail<bool>(AuthoringErrors.Unavailable());
            }

            // Only an `for update` pass can hold a row this file writes, so only it has versions to prove: a share pass
            // carries parents that no planned row matches, and checking them here would read a missing key as a stale plan.
            if (share)
            {
                continue;
            }

            foreach (var row in rows.Where(row => row.Row.Kind == rowKind
                && row.Outcome is ImportOutcome.Update or ImportOutcome.Restore))
            {
                if (!stored.TryGetValue(row.EntityId, out var current) || current.Version != row.StoredVersion)
                {
                    return AuthoringErrors.Fail<bool>(AuthoringErrors.Unavailable());
                }

                if (row.RestoreSnapshot is not null && current.DeletedAt is null)
                {
                    return AuthoringErrors.Fail<bool>(AuthoringErrors.Unavailable());
                }
            }
        }

        return Result.Success(true);
    }

    /// <summary>
    /// Re-reads every sibling group this file adds to, under its advisory lock, and answers false when any of them moved
    /// since the plan assigned positions from it (finding F). One statement for every group of both kinds.
    /// </summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="groups">The groups and the state the plan read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<bool> GroupsUnchangedAsync(
        Guid orgId,
        Guid projectId,
        IReadOnlyList<ImportGroupExpectation> groups,
        CancellationToken cancellationToken)
    {
        if (groups.Count == 0)
        {
            return true;
        }

        var nodeGroups = groups
            .Where(group => group.Kind == ImportRowKind.Node)
            .Select(group => group.ParentId)
            .Distinct()
            .ToArray();
        var itemGroups = groups
            .Where(group => group.Kind == ImportRowKind.Item)
            .Select(group => group.ParentId)
            .Distinct()
            .ToArray();

        var observed = await db.QueryAsync<ImportApplySql.GroupStateRow>(
            ImportApplySql.GroupState,
            new
            {
                org_id = orgId,
                project_id = projectId,
                root = Guid.Empty,
                node_groups = nodeGroups,
                item_groups = itemGroups,
            },
            cancellationToken).ConfigureAwait(false);

        var state = observed.ToDictionary(
            row => (Kind: row.Kind, row.ParentId),
            row => (row.LiveCount, row.Maximum));

        return ExpectationsHold(groups, state);
    }
}
