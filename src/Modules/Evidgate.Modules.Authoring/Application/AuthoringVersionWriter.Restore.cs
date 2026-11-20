using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Commands;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application;

internal sealed partial class AuthoringVersionWriter
{
    // ------------------------------------------------------------------ RestoreAsync

    /// <summary>
    /// Re-appends a snapshot as a new version (AC8): clears <c>deleted_at</c>, writes the snapshot back, resolves a
    /// position clash at <c>max + 1</c>, refuses a parent that is still soft-deleted, refuses a sibling group already at
    /// AC2's cap and sets a restored item to <c>active</c> and a restored case to <c>draft</c> (§6).
    /// </summary>
    /// <remarks>
    /// <b>Serialisation (hand-over (a), OI-13-128).</b> The restore takes the sibling group's transaction-scoped advisory
    /// lock (<see cref="AuthoringLockGuard.LockSiblingGroupAsync"/>) <em>before</em> it resolves <c>max + 1</c>, and it is
    /// the first lock of the call — the node create, the node move-into-group and the node reorder take that same
    /// feature-node lock before any row lock, so the advisory lock is the outermost of every path and cannot sit in a
    /// cycle with them. Before this, a restore resolved its position unlocked and survived a lost race only through the
    /// <c>23505</c> → <c>503</c> mapping below, which stays as the defence in depth for a writer that does not take the
    /// group lock (13.1.2's batch path, OI-13-129).
    /// <para>
    /// <b>The group cap (AC2, review F3).</b> A restore that adds a live member to a sibling group — a tombstone coming
    /// back, or a live row re-applied into another group — is refused with <see cref="NodeLimits.Refusal"/>
    /// (<c>422 children_limit_exceeded</c>) when that group already holds
    /// <see cref="NodeLimits.MaximumSiblingsPerGroup"/> live siblings, exactly as a create or a move-in of the group is.
    /// The count is read under the same group lock, so nothing can slip past it.
    /// </para>
    /// </remarks>
    /// <param name="orgId">The tenant; must equal the open scope's organization.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="batchId">The batch of the calling command.</param>
    /// <param name="actorMembershipId">The authoring member, or null.</param>
    /// <param name="entityKind">Which entity to restore.</param>
    /// <param name="entityId">The entity.</param>
    /// <param name="expectedVersion">
    /// The <c>version</c> the caller last read (the route's <c>If-Match</c>, AC19), or null when the route carries no
    /// precondition (route 24). It is compared with the <b>locked</b> row's <c>version</c> before anything is written, so
    /// a restore that lost the race to another writer answers the <c>412 precondition_failed</c> of
    /// <see cref="AuthoringRequestScope.Stale"/> with the current version in its details, and nothing else happens.
    /// </param>
    /// <param name="reason">An optional 1..500 character reason.</param>
    /// <param name="fromVersionId">A version of the entity whose snapshot is re-applied, or null for its own last state.</param>
    /// <param name="context">Source, reason and AI run of the call.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async ValueTask<Result<AuthoringVersionRef>> RestoreAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        AuthoringEntityKind entityKind,
        Guid entityId,
        int? expectedVersion = null,
        string? reason = null,
        Guid? fromVersionId = null,
        AuthoringAppendContext? context = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await RestoreCoreAsync(orgId, projectId, batchId, actorMembershipId, entityKind, entityId, expectedVersion, reason, fromVersionId, context, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (IsContention(exception))
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.Unavailable());
        }
    }

    private async ValueTask<Result<AuthoringVersionRef>> RestoreCoreAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        AuthoringEntityKind entityKind,
        Guid entityId,
        int? expectedVersion = null,
        string? reason = null,
        Guid? fromVersionId = null,
        AuthoringAppendContext? context = null,
        CancellationToken cancellationToken = default)
    {
        EnsureScope(orgId);

        var settings = context ?? new AuthoringAppendContext();

        if (entityId == Guid.Empty)
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.Invalid("/id", "required", "the entity id is required"));
        }

        if (projectId == Guid.Empty)
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.Invalid("/project_id", "required", "the project is required"));
        }

        if (batchId == Guid.Empty)
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.Invalid("/batch_id", "required", "the batch id is required"));
        }

        if (!Enum.IsDefined(entityKind))
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.Invalid("/entity_kind", "value_invalid", "unknown entity kind"));
        }

        if (CheckContext(settings) is { } contextFailure)
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(contextFailure);
        }

        await db.ExecuteAsync(AuthoringSql.SetLockTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

        var effectiveReason = reason ?? settings.Reason;
        if (effectiveReason is not null && !IsReasonLengthValid(effectiveReason))
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.Invalid("/reason", "length_invalid", "a reason is 1..500 characters"));
        }

        // Lock order, writer-wide: parent before child (feature_node -> checklist_item -> manual_test_case). A restore
        // therefore peeks the entity without a lock to learn its parent, takes the parent's share lock, and only then
        // locks the entity for update. The snapshot written back is always taken from the *locked* row (or from the
        // named version), never from the peek: a concurrent restore between the peek and the lock would otherwise
        // re-append a stale position and state under a version number that still lines up.
        var peeked = await PeekAsync(orgId, projectId, entityId, entityKind, cancellationToken).ConfigureAwait(false);
        if (peeked is null)
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.EntityNotFound(entityKind));
        }

        AuthoringSnapshot? fromVersion = null;

        if (fromVersionId is { } versionId)
        {
            var version = await db.QuerySingleOrDefaultAsync<StoredVersion>(
                AuthoringSql.ReadVersion,
                new { org_id = orgId, project_id = projectId, id = versionId },
                cancellationToken).ConfigureAwait(false);

            if (version is null
                || !string.Equals(version.EntityKind, AuthoringWireValues.EntityKindName(entityKind), StringComparison.Ordinal)
                || version.EntityId != entityId)
            {
                return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.NotFound(
                    "no version with that id describes this entity in this project"));
            }

            using var document = JsonDocument.Parse(version.Content);
            if (!AuthoringContent.Parse(entityKind, document.RootElement, out var parsed, out var details))
            {
                throw new InvalidOperationException(
                    "the stored snapshot of authoring version " + versionId.ToString("D") + " is not a valid "
                    + AuthoringWireValues.EntityKindName(entityKind) + " content: "
                    + string.Join(", ", details.Select(detail => detail.Path + " " + detail.Reason)));
            }

            fromVersion = parsed!;

            if (entityKind == AuthoringEntityKind.ManualTestCase
                && CheckSteps((ManualTestCaseSnapshot)fromVersion, "/content/") is { } stepsFailure)
            {
                return AuthoringErrors.Fail<AuthoringVersionRef>(stepsFailure);
            }
        }
        else if (!IsDeleted(peeked))
        {
            // AC19 answers before AC8's "not soft-deleted" refusal: a second restore that presents the tag the first one
            // already consumed is a stale precondition (412), not a 409. The authoritative comparison runs again under
            // the entity lock below; this one only picks the right refusal for a row that is already live.
            if (expectedVersion is { } presented && VersionOf(peeked) != presented)
            {
                return AuthoringErrors.Fail<AuthoringVersionRef>(StaleRestore(entityKind, VersionOf(peeked)));
            }

            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.InvalidTransition(
                "the entity is not soft-deleted; restore names the version it re-applies"));
        }

        // The parent to lock comes from the row (or from the named version) as peeked; the entity lock follows it.
        var peekedSnapshot = fromVersion ?? SnapshotOf(peeked);

        // Hand-over (a) / OI-13-128: the sibling group's advisory lock, and it is the FIRST lock of this call.
        // Why here: the node create and move-into-group (NodeCommands) and the node reorder (ReorderNodesCoreAsync) take
        // this same feature-node key before their first row lock, so taking it before the parent's share lock and before
        // the entity's own lock keeps the advisory lock the outermost of every path and no cycle can include it. Taking it
        // later — after the entity lock, next to the max + 1 read it protects — would put it behind a row lock a create or
        // a reorder already holds, which is the one inversion this order forbids.
        // Which group: the parent the restored snapshot lands in — a node's parent_id (null = root), an item's
        // feature_node_id, a case's checklist_item_id. The group of the peeked parent is taken even when the lock below
        // finds that the row moved in between: that case answers the retryable 503 (see below) and the retry re-peeks,
        // so locking a group the write does not use can only cost a wait, never a wrong append.
        // Who else takes it: for a feature node, the create, the move-in and the reorder of that group. For a checklist
        // item and a manual test case the only taker today is a restore, so the two child keys serialise concurrent
        // restores of one group and nothing more (review F5): the item create picks its position from a plain read with no
        // group lock (ChecklistItemCreate.cs:86-91, AuthoringChecklistReadSql.NextPosition), so a create's number and a
        // restore's max + 1 can still coincide — and no key catches it either, because only feature_node carries a
        // sibling-position unique index (migrations/0039_authoring_tree.sql:237-239); the two live items then share a
        // position, which their reads order deterministically by (position, id). Closing that race means taking this key in
        // the item create, which is outside this step. A case carries no position at all (route 12 orders by (code, id)
        // and the code is immutable), so its key guards no append position: it is the key a path that brings cases into one
        // item must take once one exists (13.1.2's batch path).
        await AuthoringLockGuard
            .LockSiblingGroupAsync(db, projectId, ParentOf(peekedSnapshot), SiblingGroupOf(entityKind), cancellationToken)
            .ConfigureAwait(false);

        await LockRestoreParentAsync(orgId, projectId, peekedSnapshot, cancellationToken).ConfigureAwait(false);

        var stored = await LockAsync(orgId, projectId, entityId, entityKind, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.EntityNotFound(entityKind));
        }

        // AC19, atomically with the write: the tag the caller presented is compared with the version of the row this
        // transaction now holds for update, before a single column is written. A second restore that presents the tag the
        // first one consumed therefore answers 412 with the current version in details — never a second restore and never
        // a 409 — and a refusal here leaves the transaction untouched (no entity write, no version row, no audit row).
        if (expectedVersion is { } ifMatch && VersionOf(stored) != ifMatch)
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(StaleRestore(entityKind, VersionOf(stored)));
        }

        AuthoringSnapshot snapshot;
        if (fromVersion is not null)
        {
            snapshot = fromVersion;
        }
        else
        {
            if (!IsDeleted(stored))
            {
                // Another writer restored the row between the peek and this lock. Nothing has been written, so the
                // caller's retry is safe.
                return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.Unavailable());
            }

            snapshot = SnapshotOf(stored);

            if (ParentOf(snapshot) != ParentOf(peekedSnapshot))
            {
                // The row moved to another parent between the peek and the lock. Taking that parent's share lock now
                // would be the one child-before-parent acquisition of the writer, so this is answered as the retryable
                // 503 instead: a retry re-peeks and locks the right parent first.
                return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.Unavailable());
            }
        }

        // §6: a restored item is active and a restored case is a draft again, whatever it was before. Applied before
        // the position resolution and the write, so the entity row and the version snapshot record the same state.
        snapshot = RestoredSnapshot(snapshot);

        var lastVersionNo = LastVersionNoOf(stored);
        var previousVersion = VersionOf(stored);

        var parentFailure = await CheckRestoreParentAsync(orgId, projectId, snapshot, cancellationToken).ConfigureAwait(false);
        if (parentFailure is not null)
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(parentFailure);
        }

        // AC2's group cap, re-applied to a restore (review F3): a restore re-appends its row as a live member, so it grows
        // its target group exactly like a create or a move-into-group does. Unchecked it could push a group past
        // NodeLimits.MaximumSiblingsPerGroup, and such a group could never be renumbered again — the reorder refuses more
        // than that many ids (ReorderNodesCommandValidator.MaxOrderedIds) — and a restore could not put it back. The count
        // is read under the group's advisory lock taken above, so a concurrent create or move-in of the same group either
        // committed before this read or waits for this transaction. Only a feature node has a renumbered group and a
        // position to protect: a checklist item's position is never renumbered as a set and a case carries none.
        if (snapshot is FeatureNodeSnapshot restoredNode
            && AddsGroupMember(stored, restoredNode)
            && await db.QuerySingleOrDefaultAsync<AuthoringSql.NodeGroupRow>(
                AuthoringSql.NodeGroupSummary(restoredNode.ParentId is null),
                new { org_id = orgId, project_id = projectId, parent_id = restoredNode.ParentId },
                cancellationToken).ConfigureAwait(false) is { } group
            && group.LiveCount >= NodeLimits.MaximumSiblingsPerGroup)
        {
            // Refused before the first UPDATE: the row keeps its tombstone, its position and its history (AC8's "nothing
            // changes"), and the caller can free a slot — soft delete a sibling or move one out — and retry.
            return AuthoringErrors.Fail<AuthoringVersionRef>(NodeLimits.Refusal());
        }

        snapshot = await ResolveRestorePositionAsync(orgId, projectId, entityId, snapshot, cancellationToken).ConfigureAwait(false);

        VersionRow? version2;
        try
        {
            version2 = await db.QuerySingleOrDefaultAsync<VersionRow>(
                AuthoringSql.RestoreEntity(entityKind),
                RestoreParameters(orgId, projectId, entityId, snapshot),
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(exception.ConstraintName, SiblingPositionIndex, StringComparison.Ordinal))
        {
            // The resolved max + 1 was taken by a concurrent sibling between the read and this write. The transaction is
            // already aborted, so the retry belongs to the caller: answer the retryable 503 of §5 rather than a 500.
            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.Unavailable());
        }

        if (version2 is null)
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.EntityNotFound(entityKind));
        }

        if (version2.Version != previousVersion + 1 || version2.Version != lastVersionNo + 1)
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(AuthoringErrors.InvalidTransition(
                "the version history of this entity is out of step"));
        }

        var pending = Pending(entityKind, entityId, version2.Version, AuthoringWireValues.RestoreChangeKind, snapshot, effectiveReason);

        if (await AppendVersionsAsync(orgId, projectId, batchId, actorMembershipId, settings, [pending], cancellationToken).ConfigureAwait(false) is { } failure)
        {
            return AuthoringErrors.Fail<AuthoringVersionRef>(failure);
        }

        return Result.Success(new AuthoringVersionRef(pending.VersionId, entityKind, entityId, pending.VersionNo, pending.ContentHash, batchId));
    }
}
