using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application;

internal sealed partial class AuthoringVersionWriter
{
    // ------------------------------------------------------------------ ReorderNodesAsync

    /// <summary>
    /// Renumbers the live siblings of one node's parent group (13.1.1 AC2) and appends <b>one</b> <c>update</c> row per
    /// moved node under <paramref name="batchId"/>, after the AC19 sibling-order precondition has been honoured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a member of its own, and why it does not go through <see cref="AppendAsync"/></b> (OI-13-110, Notes :524).
    /// A renumber changes one column of many rows at once, and the partial unique index
    /// <c>feature_node_sibling_position_uq</c> is not <c>DEFERRABLE</c> (a partial index cannot be), so the positions of a
    /// permutation cannot be written in one statement: row <c>i</c> would collide with the row that has not moved yet.
    /// AC2 therefore fixes the renumber to <b>two</b> statements in one transaction — park the moved rows above the
    /// group's maximum, then assign the final positions — and <b>every</b> <c>UPDATE</c> fires
    /// <c>set_updated_at_and_version</c> (0004:70-78), which bumps <c>version</c> unconditionally. A moved row is thus
    /// bumped twice while AC5/AC2 allow exactly one version row for it, so a per-change call into
    /// <see cref="UpdateAsync"/> — whose out-of-step guard demands <c>row.version = last_version_no + 1</c> — cannot
    /// describe this write. This member instead appends the row at the version the renumber left behind and asserts that
    /// it is exactly two greater, so the documented delta is checked, never assumed: the history stays in step
    /// (<c>authoring_version.version_no</c> always equals the entity row's <c>version</c> after the write) and no third
    /// statement can slip in unnoticed. The one visible consequence is that a reorder's version numbers are not dense
    /// (a moved node goes from <c>v</c> to <c>v + 2</c>); <c>version</c> is a monotone token, not a count, and 13.1.2's
    /// batch ETag compares <c>(id, version)</c> pairs, so a sparse number is equivalent for every consumer.
    /// </para>
    /// <para>
    /// <b>Order.</b> The anchor is <b>peeked</b> unlocked (its parent id, and whether it is still live), then the group's
    /// advisory lock is taken (<see cref="AuthoringLockGuard.LockSiblingGroupAsync"/>, the lock a create and a move-in
    /// into that group take before they read its append position), then the whole group is locked <c>for update</c> in
    /// ascending id, and only then is the anchor required to be a member of the locked set. Locking the anchor on its own
    /// first would let two reorders anchored on two different nodes of one group deadlock (each holds one row of the group
    /// and waits for the other's), which no retry can fix; one rank with one order cannot. No other <c>feature_node</c> row
    /// is locked: a reorder never sees a parent as a row, and the advisory key is not a row at all, so it cannot take part
    /// in the writer-wide rank-then-id order.
    /// </para>
    /// <para>
    /// <b>Refusals write nothing.</b> Every guard runs before the first <c>UPDATE</c> — the anchor, the sibling set, the
    /// order ETag — so a stale precondition (412) or a body that does not describe the live set (422) leaves the
    /// transaction untouched.
    /// </para>
    /// </remarks>
    /// <param name="orgId">The tenant; must equal the open scope's organization.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="batchId">The batch the appended rows share (§5).</param>
    /// <param name="actorMembershipId">The authoring member, or null.</param>
    /// <param name="anchorNodeId">The <c>{n}</c> of the route: any live node of the group being renumbered.</param>
    /// <param name="orderedIds">The group's live sibling ids, each once, in their new order.</param>
    /// <param name="presentedOrderHash">The <c>If-Match</c> the caller sent, exactly <see cref="AuthoringHashes.OrderHash"/>'s shape.</param>
    /// <param name="context">Source, reason and AI run of the call.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async ValueTask<Result<AuthoringReorderOutcome>> ReorderNodesAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        Guid anchorNodeId,
        IReadOnlyList<Guid> orderedIds,
        string presentedOrderHash,
        AuthoringAppendContext? context = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ReorderNodesCoreAsync(
                orgId,
                projectId,
                batchId,
                actorMembershipId,
                anchorNodeId,
                orderedIds,
                presentedOrderHash,
                context,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (IsContention(exception))
        {
            // AC2's renumber is the one write that locks a whole sibling group, so it is the most likely to meet the
            // 5 s lock_timeout: §5 answers 55P03/40P01 with the retryable 503, never a 500.
            return AuthoringErrors.Fail<AuthoringReorderOutcome>(AuthoringErrors.Unavailable());
        }
    }

    private async ValueTask<Result<AuthoringReorderOutcome>> ReorderNodesCoreAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        Guid anchorNodeId,
        IReadOnlyList<Guid> orderedIds,
        string presentedOrderHash,
        AuthoringAppendContext? context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);

        if (anchorNodeId == Guid.Empty)
        {
            return AuthoringErrors.Fail<AuthoringReorderOutcome>(AuthoringErrors.Invalid("/node_id", "required", "the node is required"));
        }

        if (orderedIds.Count == 0)
        {
            return AuthoringErrors.Fail<AuthoringReorderOutcome>(AuthoringErrors.Invalid("/ordered_ids", "required", "the ordered sibling ids are required"));
        }

        if (batchId == Guid.Empty)
        {
            return AuthoringErrors.Fail<AuthoringReorderOutcome>(AuthoringErrors.Invalid("/batch_id", "required", "the batch id is required"));
        }

        if (string.IsNullOrEmpty(presentedOrderHash))
        {
            return AuthoringErrors.Fail<AuthoringReorderOutcome>(AuthoringErrors.Invalid("/If-Match", "required", "the order ETag is required"));
        }

        var settings = context ?? new AuthoringAppendContext();

        if (CheckContext(settings) is { } contextFailure)
        {
            return AuthoringErrors.Fail<AuthoringReorderOutcome>(contextFailure);
        }

        EnsureScope(orgId);

        await db.ExecuteAsync(AuthoringSql.SetLockTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

        // The anchor decides which group is renumbered, but it must NOT be locked on its own: two reorders anchored on two
        // different nodes of one group would each hold one row of that group and then wait for the other's — a cycle, and
        // so would a concurrent AppendAsync batch, whose own order is rank-then-id (AppendCoreAsync). The parent id is
        // therefore read unlocked (PeekAsync, the RestoreAsync pattern, which must not lock a child before its parent
        // either) and only the whole group is locked, in ascending id — one order for every caller.
        if (await PeekAsync(orgId, projectId, anchorNodeId, AuthoringEntityKind.FeatureNode, cancellationToken).ConfigureAwait(false) is not NodeRow anchor
            || anchor.DeletedAt is not null)
        {
            return AuthoringErrors.Fail<AuthoringReorderOutcome>(AuthoringErrors.EntityNotFound(AuthoringEntityKind.FeatureNode));
        }

        var parentId = anchor.ParentId;

        // AC2's group lock, taken before the sibling rows: a create or a move-in into this group takes the same lock
        // before it reads the group's append position, so no row can enter the group between the read below and the park
        // statement — which is the one way the partial unique index could refuse an UPDATE the caller never made.
        await AuthoringLockGuard
            .LockSiblingGroupAsync(db, projectId, parentId, cancellationToken)
            .ConfigureAwait(false);

        var siblings = await db.QueryAsync<ReorderSiblingRow>(
            AuthoringSql.LockNodeSiblings(parentId is null),
            new { org_id = orgId, project_id = projectId, parent_id = parentId },
            cancellationToken).ConfigureAwait(false);

        if (!siblings.Any(row => row.Id == anchorNodeId))
        {
            // The anchor left the group between the peek and the group lock: it was soft-deleted, or a PATCH re-parented
            // it. The rows we now hold are not the group the caller addressed, so nothing is renumbered and the caller
            // reads the node again (404, the same answer AC10 gives an absent or gone node).
            return AuthoringErrors.Fail<AuthoringReorderOutcome>(AuthoringErrors.EntityNotFound(AuthoringEntityKind.FeatureNode));
        }

        if (ValidateOrderedIds(orderedIds, siblings) is { } malformed)
        {
            return AuthoringErrors.Fail<AuthoringReorderOutcome>(malformed);
        }

        // AC19 over the sibling set: the ETag covers the sorted (id, version) pairs, so an unchanged set in a changed
        // order still matches — which is exactly what a reorder needs — while a sibling written since the caller read the
        // group answers 412 with the current tag in details, and nothing below runs.
        var currentOrderHash = AuthoringHashes.OrderHash(siblings.Select(row => (row.Id, row.Version)));
        if (!string.Equals(currentOrderHash, presentedOrderHash, StringComparison.Ordinal))
        {
            return AuthoringErrors.Fail<AuthoringReorderOutcome>(OrderStale(currentOrderHash));
        }

        var targets = new Dictionary<Guid, int>(orderedIds.Count);
        for (var index = 0; index < orderedIds.Count; index++)
        {
            targets[orderedIds[index]] = index;
        }

        var moved = siblings.Where(row => targets[row.Id] != row.Position).ToList();
        if (moved.Count == 0)
        {
            // AC5's no-op rule, applied to the reorder: the list already describes the stored order, so no row moves,
            // no version is appended and the answer is `changed: false`.
            return Result.Success(new AuthoringReorderOutcome(false, []));
        }

        var offset = siblings.Max(row => row.Position) + 1;
        var movedIds = moved.Select(row => row.Id).ToArray();

        // AC2's two statements, in this order and no other (AuthoringSql.ReorderPark/ReorderAssign explain why the
        // parked values can never meet a final one).
        try
        {
            await db.ExecuteAsync(
                AuthoringSql.ReorderPark(parentId is null),
                new { org_id = orgId, project_id = projectId, parent_id = parentId, offset, ids = movedIds },
                cancellationToken).ConfigureAwait(false);

            await db.ExecuteAsync(
                AuthoringSql.ReorderAssign,
                new
                {
                    org_id = orgId,
                    project_id = projectId,
                    rows = JsonSerializer.Serialize(
                        moved.Select(row => new ReorderTargetRow(row.Id, targets[row.Id])),
                        VersionRowJsonOptions),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(exception.ConstraintName, SiblingPositionIndex, StringComparison.Ordinal))
        {
            // A writer that does not take the group's advisory lock (13.1.2's apply composes batches straight into
            // AppendAsync) landed a row in this group between the lock of the siblings and the park. The transaction is
            // aborted, so the retry belongs to the caller: the same retryable 503 the restore answers when its own
            // resolved position is taken (:613), never a 500. A renumber itself now takes AuthoringLockGuard's group
            // lock, so a racing create or move-in cannot reach this branch.
            return AuthoringErrors.Fail<AuthoringReorderOutcome>(AuthoringErrors.Unavailable());
        }

        var renumbered = await db.QueryAsync<ReorderVersionRow>(
            AuthoringSql.ReorderVersions,
            new { org_id = orgId, project_id = projectId, ids = movedIds },
            cancellationToken).ConfigureAwait(false);

        if (renumbered.Count != moved.Count)
        {
            throw new InvalidOperationException("the renumber read back a different number of rows than it moved");
        }

        var versions = renumbered.ToDictionary(row => row.Id, row => row.Version);
        var appended = new List<PendingVersion>(moved.Count);

        foreach (var row in moved)
        {
            if (!versions.TryGetValue(row.Id, out var version) || version != row.Version + 2)
            {
                // The two position statements are the only writers of `version` here; a delta other than two means a
                // third statement (or a concurrent writer that should have been blocked by the group lock) reached the
                // row, so the history cannot be appended truthfully. Refusing keeps `version_no` == row.version.
                return AuthoringErrors.Fail<AuthoringReorderOutcome>(AuthoringErrors.InvalidTransition(
                    "the version history of this reorder is out of step"));
            }

            appended.Add(Pending(
                AuthoringEntityKind.FeatureNode,
                row.Id,
                version,
                AuthoringWireValues.UpdateChangeKind,
                new FeatureNodeSnapshot(row.Kind, row.Title, targets[row.Id], parentId),
                settings.Reason));
        }

        var written = await AppendVersionsAsync(orgId, projectId, batchId, actorMembershipId, settings, appended, cancellationToken).ConfigureAwait(false);
        if (written is not null)
        {
            return AuthoringErrors.Fail<AuthoringReorderOutcome>(written);
        }

        return Result.Success(new AuthoringReorderOutcome(
            true,
            [.. appended.Select(row => new AuthoringVersionRef(row.VersionId, row.EntityKind, row.EntityId, row.VersionNo, row.ContentHash, batchId))]));
    }

    /// <summary>
    /// The AC2/AC19 shape of <c>ordered_ids</c>: the group's live siblings, each exactly once. Duplicates, an id that is
    /// not a live sibling of the group and a live sibling the list leaves out are all <c>422 request_invalid</c> under
    /// <c>/ordered_ids</c>, because none of them describes a permutation of the group. The route's <c>{n}</c> needs no
    /// separate rule: it is a live sibling of its own group, so leaving it out is the "incomplete" case.
    /// </summary>
    /// <param name="orderedIds">The caller's list.</param>
    /// <param name="siblings">The locked live group.</param>
    private static Error? ValidateOrderedIds(IReadOnlyList<Guid> orderedIds, IReadOnlyList<ReorderSiblingRow> siblings)
    {
        var live = new HashSet<Guid>(siblings.Select(row => row.Id));
        var seen = new HashSet<Guid>(orderedIds.Count);

        foreach (var id in orderedIds)
        {
            if (id == Guid.Empty)
            {
                return AuthoringErrors.Invalid("/ordered_ids", "required", "an ordered id is required");
            }

            if (!seen.Add(id))
            {
                return AuthoringErrors.Invalid("/ordered_ids", "duplicate_id", "a sibling appears more than once in the new order");
            }

            if (!live.Contains(id))
            {
                return AuthoringErrors.Invalid("/ordered_ids", "not_a_sibling", "an id is not a live sibling of that node");
            }
        }

        return seen.Count == live.Count
            ? null
            : AuthoringErrors.Invalid("/ordered_ids", "incomplete", "the new order must list every live sibling of that node");
    }

    /// <summary>
    /// 412 <c>precondition_failed</c> of the reorder (AC19): the sibling set moved since the caller read it. <c>details</c>
    /// carries the current order ETag so a client can retry from a fresh read; it is a digest of ids and versions, never
    /// a title or any other content.
    /// </summary>
    /// <param name="currentOrderHash">The tag the group carries now.</param>
    private static Error OrderStale(string currentOrderHash) => new(
        ApiErrorCode.PreconditionFailed,
        "the sibling order changed since it was read",
        StatusCodes.Status412PreconditionFailed,
        Retryable: false,
        Details:
        [
            new ErrorDetail("/If-Match", "stale"),
            new ErrorDetail("/order", currentOrderHash),
        ]);
}
