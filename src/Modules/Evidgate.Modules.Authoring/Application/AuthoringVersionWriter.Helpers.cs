using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application;

internal sealed partial class AuthoringVersionWriter
{
    private static object RestoreParameters(Guid orgId, Guid projectId, Guid entityId, AuthoringSnapshot snapshot) => snapshot switch
    {
        FeatureNodeSnapshot node => new
        {
            id = entityId,
            org_id = orgId,
            project_id = projectId,
            parent_id = node.ParentId,
            kind = node.Kind,
            title = node.Title,
            position = node.Position,
        },
        ChecklistItemSnapshot item => new
        {
            id = entityId,
            org_id = orgId,
            project_id = projectId,
            feature_node_id = item.FeatureNodeId,
            title = item.Title,
            description = item.Description,
            priority = item.Priority,
            state = item.State,
            position = item.Position,
        },
        ManualTestCaseSnapshot manualCase => new
        {
            id = entityId,
            org_id = orgId,
            project_id = projectId,
            checklist_item_id = manualCase.ChecklistItemId,
            code = manualCase.Code,
            title = manualCase.Title,
            description = manualCase.Description,
            precondition = manualCase.Precondition,
            steps = manualCase.Steps,
            priority = manualCase.Priority,
            state = manualCase.State,
        },
        _ => throw new InvalidOperationException("unknown snapshot"),
    };

    private static object InsertParameters(
        Guid orgId,
        Guid projectId,
        Guid entityId,
        AuthoringSnapshot snapshot,
        AuthoringSource source) => new
        {
            id = entityId,
            org_id = orgId,
            project_id = projectId,
            source = AuthoringWireValues.SourceName(source),
            parent_id = (snapshot as FeatureNodeSnapshot)?.ParentId,
            kind = (snapshot as FeatureNodeSnapshot)?.Kind,
            title = TitleOf(snapshot),
            position = PositionOf(snapshot),
            feature_node_id = (snapshot as ChecklistItemSnapshot)?.FeatureNodeId,
            description = (snapshot as ChecklistItemSnapshot)?.Description ?? (snapshot as ManualTestCaseSnapshot)?.Description,
            priority = (snapshot as ChecklistItemSnapshot)?.Priority ?? (snapshot as ManualTestCaseSnapshot)?.Priority,
            state = (snapshot as ChecklistItemSnapshot)?.State ?? (snapshot as ManualTestCaseSnapshot)?.State,
            checklist_item_id = (snapshot as ManualTestCaseSnapshot)?.ChecklistItemId,
            code = (snapshot as ManualTestCaseSnapshot)?.Code,
            precondition = (snapshot as ManualTestCaseSnapshot)?.Precondition,
            steps = (snapshot as ManualTestCaseSnapshot)?.Steps,
        };

    private static object UpdateParameters(Guid orgId, Guid projectId, Guid entityId, AuthoringSnapshot snapshot) => new
    {
        id = entityId,
        org_id = orgId,
        project_id = projectId,
        parent_id = (snapshot as FeatureNodeSnapshot)?.ParentId,
        kind = (snapshot as FeatureNodeSnapshot)?.Kind,
        title = TitleOf(snapshot),
        position = PositionOf(snapshot),
        feature_node_id = (snapshot as ChecklistItemSnapshot)?.FeatureNodeId,
        description = (snapshot as ChecklistItemSnapshot)?.Description ?? (snapshot as ManualTestCaseSnapshot)?.Description,
        priority = (snapshot as ChecklistItemSnapshot)?.Priority ?? (snapshot as ManualTestCaseSnapshot)?.Priority,
        state = (snapshot as ChecklistItemSnapshot)?.State ?? (snapshot as ManualTestCaseSnapshot)?.State,
        checklist_item_id = (snapshot as ManualTestCaseSnapshot)?.ChecklistItemId,
        code = (snapshot as ManualTestCaseSnapshot)?.Code,
        precondition = (snapshot as ManualTestCaseSnapshot)?.Precondition,
        steps = (snapshot as ManualTestCaseSnapshot)?.Steps,
    };

    private static string TitleOf(AuthoringSnapshot snapshot) => snapshot switch
    {
        FeatureNodeSnapshot node => node.Title,
        ChecklistItemSnapshot item => item.Title,
        ManualTestCaseSnapshot manualCase => manualCase.Title,
        _ => throw new InvalidOperationException("unknown snapshot"),
    };

    private static int PositionOf(AuthoringSnapshot snapshot) => snapshot switch
    {
        FeatureNodeSnapshot node => node.Position,
        ChecklistItemSnapshot item => item.Position,
        _ => 0,
    };

    private static bool IsDeleted(object row) => row switch
    {
        NodeRow node => node.DeletedAt is not null,
        ItemRow item => item.DeletedAt is not null,
        CaseRow manualCase => manualCase.DeletedAt is not null,
        _ => throw new InvalidOperationException("the row is not an authoring entity"),
    };

    /// <summary>
    /// True when a restore adds a live member to its target group (AC2's cap, review F3): the row is a tombstone, so the
    /// write brings it back into the group, or it is live in another group, so the write moves it in. A live row restored
    /// inside its own group keeps its place — it re-appends at <c>max + 1</c> only when a sibling took its number, which
    /// changes no count.
    /// </summary>
    /// <param name="stored">The entity row as locked by the calling restore.</param>
    /// <param name="target">The snapshot being re-appended.</param>
    private static bool AddsGroupMember(object stored, FeatureNodeSnapshot target) =>
        IsDeleted(stored) || (SnapshotOf(stored) is FeatureNodeSnapshot current && current.ParentId != target.ParentId);

    /// <summary>
    /// §6's restore rule, applied to every restore: an item comes back <c>active</c> and a case <c>draft</c>, whatever
    /// the stored state was — a retired item or an approved case never returns retired/approved.
    /// </summary>
    /// <param name="snapshot">The snapshot being re-appended.</param>
    /// <returns>The snapshot with its restored state.</returns>
    private static AuthoringSnapshot RestoredSnapshot(AuthoringSnapshot snapshot) => snapshot switch
    {
        ChecklistItemSnapshot item => item with { State = ChecklistItemStateMachine.RestoreState },
        ManualTestCaseSnapshot manualCase => manualCase with { State = ManualTestCaseStateMachine.RestoreState },
        _ => snapshot,
    };

    /// <summary>
    /// The AC19 refusal of a restore whose <c>If-Match</c> no longer describes the locked row: the shared
    /// <see cref="AuthoringRequestScope.Stale"/> answer, so every authoring 412 has one shape (hand-over (c), the
    /// <c>/version</c> pointer, belongs to <see cref="AuthoringRequestScope"/>). The writer returns the finished refusal
    /// instead of a code the handler re-maps, so a route and a writer-level test assert the same bytes.
    /// </summary>
    /// <param name="kind">Which entity the caller addressed.</param>
    /// <param name="currentVersion">The version of the locked row.</param>
    private static Error StaleRestore(AuthoringEntityKind kind, int currentVersion) =>
        AuthoringRequestScope.Stale(
            kind switch
            {
                AuthoringEntityKind.FeatureNode => "feature node",
                AuthoringEntityKind.ChecklistItem => "checklist item",
                AuthoringEntityKind.ManualTestCase => "manual test case",
                _ => "entity",
            },
            currentVersion);

    /// <summary>
    /// The sibling group a restore of <paramref name="kind"/> serialises on (hand-over (a)): a node's parent group, an
    /// item's group under its node, a case's group under its item. For a feature node that is the key its create, its
    /// move-into-group and its reorder take; for an item and a case it is a key no other writer takes today, so it
    /// serialises two restores of one group (the item create and the case create take no group lock — review F5).
    /// </summary>
    /// <param name="kind">The entity kind.</param>
    private static AuthoringLockGuard.SiblingGroup SiblingGroupOf(AuthoringEntityKind kind) => kind switch
    {
        AuthoringEntityKind.FeatureNode => AuthoringLockGuard.SiblingGroup.FeatureNode,
        AuthoringEntityKind.ChecklistItem => AuthoringLockGuard.SiblingGroup.ChecklistItem,
        AuthoringEntityKind.ManualTestCase => AuthoringLockGuard.SiblingGroup.ManualTestCase,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown authoring entity kind"),
    };

    /// <summary>
    /// Takes the parent's share lock before the entity's own lock (the writer-wide order parent before child): a
    /// concurrent soft delete of the parent cannot commit between this call and the write, and a missing parent is left
    /// to <see cref="CheckRestoreParentAsync"/> so it reports its own <c>details[].reason</c>.
    /// </summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="snapshot">The snapshot being restored.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask LockRestoreParentAsync(
        Guid orgId,
        Guid projectId,
        AuthoringSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        switch (snapshot)
        {
            case FeatureNodeSnapshot { ParentId: { } parentId }:
                await LockAsync(orgId, projectId, parentId, AuthoringEntityKind.FeatureNode, cancellationToken, shared: true).ConfigureAwait(false);
                return;
            case ChecklistItemSnapshot item:
                await LockAsync(orgId, projectId, item.FeatureNodeId, AuthoringEntityKind.FeatureNode, cancellationToken, shared: true).ConfigureAwait(false);
                return;
            case ManualTestCaseSnapshot manualCase:
                await LockAsync(orgId, projectId, manualCase.ChecklistItemId, AuthoringEntityKind.ChecklistItem, cancellationToken, shared: true).ConfigureAwait(false);
                return;
            default:
                return;
        }
    }

    /// <summary>The snapshot of a locked row, whichever of the three entity kinds it is.</summary>
    /// <param name="row">The row.</param>
    private static AuthoringSnapshot SnapshotOf(object row) => row switch
    {
        NodeRow node => SnapshotOf(node),
        ItemRow item => SnapshotOf(item),
        CaseRow manualCase => SnapshotOf(manualCase),
        _ => throw new InvalidOperationException("the row is not an authoring entity"),
    };

    /// <summary>The parent id a snapshot names: <c>parent_id</c>, <c>feature_node_id</c> or <c>checklist_item_id</c>.</summary>
    /// <param name="snapshot">The snapshot.</param>
    private static Guid? ParentOf(AuthoringSnapshot snapshot) => snapshot switch
    {
        FeatureNodeSnapshot node => node.ParentId,
        ChecklistItemSnapshot item => item.FeatureNodeId,
        ManualTestCaseSnapshot manualCase => manualCase.ChecklistItemId,
        _ => null,
    };

    /// <summary>The last version number of a locked row (0 for a row with no version yet).</summary>
    /// <param name="row">The row.</param>
    private static int LastVersionNoOf(object row) => row switch
    {
        NodeRow node => node.LastVersionNo,
        ItemRow item => item.LastVersionNo,
        CaseRow manualCase => manualCase.LastVersionNo,
        _ => throw new InvalidOperationException("the row is not an authoring entity"),
    };

    /// <summary>The entity's own <c>version</c> column of a locked row.</summary>
    /// <param name="row">The row.</param>
    private static int VersionOf(object row) => row switch
    {
        NodeRow node => node.Version,
        ItemRow item => item.Version,
        CaseRow manualCase => manualCase.Version,
        _ => throw new InvalidOperationException("the row is not an authoring entity"),
    };

    private static FeatureNodeSnapshot SnapshotOf(NodeRow row) => new(row.Kind, row.Title, row.Position, row.ParentId);

    private static ChecklistItemSnapshot SnapshotOf(ItemRow row) =>
        new(row.FeatureNodeId, row.Title, row.Description, row.Priority, row.State, row.Position);

    private static ManualTestCaseSnapshot SnapshotOf(CaseRow row) =>
        new(row.ChecklistItemId, row.Code, row.Title, row.Description, row.Precondition, row.Steps, row.Priority, row.State);

    private static PendingVersion Pending(
        AuthoringEntityKind kind,
        Guid entityId,
        int versionNo,
        string changeKind,
        AuthoringSnapshot snapshot,
        string? reason) => new(
            Guid.CreateVersion7(),
            kind,
            entityId,
            versionNo,
            changeKind,
            AuthoringContent.Serialize(snapshot),
            AuthoringHashes.ContentHash(snapshot),
            reason);

    /// <summary>Maps a refused state move to its 00 §9.2 answer (§6), or null when it may go ahead.</summary>
    private static Error? AuthoringStateFailure(AuthoringTransition transition, string path) => transition.Outcome switch
    {
        AuthoringTransitionOutcome.Allowed or AuthoringTransitionOutcome.Unchanged => null,
        AuthoringTransitionOutcome.ReasonRequired => AuthoringErrors.Invalid(
            "/reason",
            "required",
            "a move into retired needs a 1..500 character reason"),
        _ => AuthoringErrors.InvalidTransition("the entity's state does not allow that move"),
    };

    private static Error? CheckContext(AuthoringAppendContext settings)
    {
        if (settings.Source == AuthoringSource.Ai && settings.AiRunId is null)
        {
            return AuthoringErrors.Invalid("/ai_run_id", "required", "an AI change names the run that produced it");
        }

        if (settings.Source != AuthoringSource.Ai && settings.AiRunId is not null)
        {
            return AuthoringErrors.Invalid("/ai_run_id", "unexpected", "only an AI change carries a run id");
        }

        if (settings.Reason is { } reason && !IsReasonLengthValid(reason))
        {
            return AuthoringErrors.Invalid("/reason", "length_invalid", "a reason is 1..500 characters");
        }

        return null;
    }

    /// <summary>
    /// A reason is 1..<see cref="MaximumReasonLength"/> <b>code points</b> — the unit of the column's own CHECK
    /// (<c>char_length(reason) between 1 and 500</c>, migration 0039) and of every authoring text
    /// (<see cref="AuthoringText"/>), so a 500-character Vietnamese or emoji reason is stored, not refused as if it were
    /// 1 000 characters.
    /// </summary>
    /// <param name="reason">The candidate reason.</param>
    private static bool IsReasonLengthValid(string reason) =>
        AuthoringText.CodePointLength(reason) is > 0 and <= MaximumReasonLength;

    /// <summary>
    /// True for the two contention SQLSTATEs that mean "another writer holds what this call needs", so §5 answers them
    /// with the retryable <c>503 unavailable</c> instead of a 500: a lock timeout (<c>55P03</c>, the writer's own 5 s
    /// <c>lock_timeout</c>) and a deadlock (<c>40P01</c>).
    /// </summary>
    /// <param name="exception">The database refusal.</param>
    private static bool IsContention(PostgresException exception) => AuthoringLockGuard.IsContention(exception);

    /// <summary>
    /// Takes every lock one batch can need, in the writer-wide order (rank, then id): the rows the batch mutates
    /// <c>for update</c>, and the parents its new content names <c>for share</c>. Taking the parent probes here, in the
    /// same sorted phase, is what makes the order total — a probe that only happened later, inside a change, could be
    /// acquired after a higher-rank row and form a cycle with another batch (e.g. <c>[update node N, update case X]</c>
    /// against <c>[update case X, create item under N]</c>). A row named both ways keeps the stronger lock.
    /// </summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="parsed">The validated batch.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask LockBatchAsync(
        Guid orgId,
        Guid projectId,
        List<(EntityChange Change, AuthoringSnapshot? Snapshot)> parsed,
        CancellationToken cancellationToken)
    {
        var locks = new Dictionary<(AuthoringEntityKind Kind, Guid Id), bool>();

        foreach (var (change, snapshot) in parsed)
        {
            if (change.ChangeKind != AuthoringChangeKind.Create)
            {
                locks[(change.EntityKind, change.EntityId)] = true;
            }

            if (snapshot is not null && ParentReferenceOf(snapshot) is { } parent)
            {
                locks[parent] = locks.TryGetValue(parent, out var mutated) && mutated;
            }
        }

        foreach (var entry in locks
            .OrderBy(entry => LockRank(entry.Key.Kind))
            .ThenBy(entry => entry.Key.Id))
        {
            // A missing row is not reported here: the change answers not_found with its own /changes/{i} path, and a
            // missing parent its own authoring_parent_invalid with its own details[].reason.
            await LockAsync(orgId, projectId, entry.Key.Id, entry.Key.Kind, cancellationToken, shared: !entry.Value).ConfigureAwait(false);
        }
    }

    /// <summary>The parent a snapshot names, or null for a root node.</summary>
    /// <param name="snapshot">The snapshot.</param>
    private static (AuthoringEntityKind Kind, Guid Id)? ParentReferenceOf(AuthoringSnapshot snapshot) => snapshot switch
    {
        FeatureNodeSnapshot { ParentId: { } parentId } => (AuthoringEntityKind.FeatureNode, parentId),
        ChecklistItemSnapshot item => (AuthoringEntityKind.FeatureNode, item.FeatureNodeId),
        ManualTestCaseSnapshot manualCase => (AuthoringEntityKind.ChecklistItem, manualCase.ChecklistItemId),
        _ => null,
    };

    /// <summary>The writer-wide lock rank: <c>feature_node</c> → <c>checklist_item</c> → <c>manual_test_case</c>.</summary>
    /// <param name="kind">The entity kind.</param>
    private static int LockRank(AuthoringEntityKind kind) => kind switch
    {
        AuthoringEntityKind.FeatureNode => 0,
        AuthoringEntityKind.ChecklistItem => 1,
        AuthoringEntityKind.ManualTestCase => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown authoring entity kind"),
    };

    /// <summary>
    /// Validates a case's <c>steps</c> document against <c>authoring-steps.v1</c> on every write path: a document the
    /// schema refuses is a <c>422 request_invalid</c> with one detail row per violation, rebased under
    /// <c>/content/steps</c>, never a stored document the read model or the ETag would have to guess at. The snapshot's
    /// steps text is already NFC-normalised (<see cref="AuthoringContent"/> normalises every string leaf), so the bytes
    /// validated here are the bytes stored.
    /// </summary>
    /// <param name="manualCase">The snapshot to check.</param>
    /// <param name="path">The change's <c>/changes/{i}/content/</c> prefix (or <c>/content/</c> for a restore).</param>
    /// <returns>The refusal, or null when the document is valid.</returns>
    private Error? CheckSteps(ManualTestCaseSnapshot manualCase, string path)
    {
        using var document = JsonDocument.Parse(manualCase.Steps);
        var verdict = steps.ValidateDocument(document.RootElement);

        return verdict.Errors.Count == 0
            ? null
            : AuthoringErrors.InvalidMany(
                "the steps document is not a valid authoring-steps.v1 document",
                [.. verdict.Errors.Select(error => new ErrorDetail(path + "steps" + StepsSuffix(error.Path), error.Reason))]);
    }

    /// <summary>The part of a schema pointer below the stored document's own <c>/steps</c> member.</summary>
    /// <param name="pointer">The pointer the schema reported.</param>
    private static string StepsSuffix(string pointer) =>
        pointer.StartsWith("/steps", StringComparison.Ordinal) ? pointer["/steps".Length..] : pointer;

    private void EnsureScope(Guid orgId)
    {
        if (scopes.Require().OrgId != orgId)
        {
            throw new ArgumentException("the open tenant scope belongs to another organization", nameof(orgId));
        }
    }
}
