using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application;

internal sealed partial class AuthoringVersionWriter
{
    // ------------------------------------------------------------------ shared pieces

    /// <summary>
    /// Locks one entity row and reads its last version: <c>for update</c> for the entity the call mutates,
    /// <c>for share</c> when <paramref name="shared"/> is true, i.e. for a parent-only probe. The probe's purpose is to
    /// block a concurrent soft delete of the parent; an update lock there would serialise every unrelated child of one
    /// parent behind the same lock.
    /// </summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="entityId">The entity.</param>
    /// <param name="kind">The entity kind.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="shared">True for a parent-only probe.</param>
    /// <returns>The row with its last version, or null when no such row is visible in this project.</returns>
    private async ValueTask<object?> LockAsync(
        Guid orgId,
        Guid projectId,
        Guid entityId,
        AuthoringEntityKind kind,
        CancellationToken cancellationToken,
        bool shared = false)
    {
        var row = await ReadRowAsync(
            AuthoringSql.LockEntity(kind, shared), orgId, projectId, entityId, kind, cancellationToken).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        // A statement of its own, after the lock: a lateral join inside the locking select would answer from the
        // statement's original snapshot and hide the version the lock holder just committed (AuthoringSql.LockEntity).
        var last = await db.QuerySingleOrDefaultAsync<LastVersionRow>(
            AuthoringSql.ReadLastVersion,
            new
            {
                org_id = orgId,
                project_id = projectId,
                entity_kind = AuthoringWireValues.EntityKindName(kind),
                id = entityId,
            },
            cancellationToken).ConfigureAwait(false);

        var versionNo = last?.VersionNo ?? 0;
        var contentHash = last?.ContentHash ?? string.Empty;

        switch (row)
        {
            case NodeRow node:
                node.LastVersionNo = versionNo;
                node.LastContentHash = contentHash;
                break;
            case ItemRow item:
                item.LastVersionNo = versionNo;
                item.LastContentHash = contentHash;
                break;
            case CaseRow manualCase:
                manualCase.LastVersionNo = versionNo;
                manualCase.LastContentHash = contentHash;
                break;
            default:
                throw new InvalidOperationException("the locked row is not an authoring entity");
        }

        return row;
    }

    /// <summary>
    /// The unlocked read a restore takes before any lock, to learn its parent id: the writer-wide lock order is parent
    /// before child (<c>feature_node</c> → <c>checklist_item</c> → <c>manual_test_case</c>), so the child row must not be
    /// locked first. The caller re-verifies the parent id once the entity lock is held.
    /// </summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="entityId">The entity.</param>
    /// <param name="kind">The entity kind.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The row as stored, without its last version, or null when no such row is visible.</returns>
    private async ValueTask<object?> PeekAsync(
        Guid orgId,
        Guid projectId,
        Guid entityId,
        AuthoringEntityKind kind,
        CancellationToken cancellationToken) =>
        await ReadRowAsync(
            AuthoringSql.PeekEntity(kind), orgId, projectId, entityId, kind, cancellationToken).ConfigureAwait(false);

    /// <summary>Runs one entity-row read of <paramref name="kind"/> and materialises it as that kind's row record.</summary>
    private async ValueTask<object?> ReadRowAsync(
        string sql,
        Guid orgId,
        Guid projectId,
        Guid entityId,
        AuthoringEntityKind kind,
        CancellationToken cancellationToken)
    {
        var parameters = new { org_id = orgId, project_id = projectId, id = entityId };

        return kind switch
        {
            AuthoringEntityKind.FeatureNode => await db.QuerySingleOrDefaultAsync<NodeRow>(
                sql, parameters, cancellationToken).ConfigureAwait(false),
            AuthoringEntityKind.ChecklistItem => await db.QuerySingleOrDefaultAsync<ItemRow>(
                sql, parameters, cancellationToken).ConfigureAwait(false),
            AuthoringEntityKind.ManualTestCase => await db.QuerySingleOrDefaultAsync<CaseRow>(
                sql, parameters, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown authoring entity kind"),
        };
    }

    /// <summary>Writes the batch with one statement and reports a short write instead of surfacing a 23505.</summary>
    private async ValueTask<Error?> AppendVersionsAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        AuthoringAppendContext settings,
        List<PendingVersion> rows,
        CancellationToken cancellationToken)
    {
        var source = AuthoringWireValues.SourceName(settings.Source);
        var payload = JsonSerializer.Serialize(
            rows.Select(row => new VersionRowJson(
                row.VersionId,
                AuthoringWireValues.EntityKindName(row.EntityKind),
                row.EntityId,
                row.VersionNo,
                row.ChangeKind,
                row.Content,
                row.ContentHash,
                source,
                row.Reason,
                settings.Source == AuthoringSource.Ai ? settings.AiRunId : null)),
            VersionRowJsonOptions);

        var written = await db.ExecuteAsync(
            AuthoringSql.AppendVersions,
            new
            {
                org_id = orgId,
                project_id = projectId,
                batch_id = batchId,
                author_membership_id = actorMembershipId,
                rows = payload,
            },
            cancellationToken).ConfigureAwait(false);

        return written == rows.Count
            ? null
            : AuthoringErrors.InvalidTransition("the version history of this batch is out of step");
    }

    private async ValueTask<string?> CheckNodePlacementAsync(
        Guid orgId,
        Guid projectId,
        FeatureNodeSnapshot node,
        CancellationToken cancellationToken)
    {
        FeatureNodeParent? parent = null;
        if (node.ParentId is { } parentId)
        {
            // The parent is share-locked: a concurrent soft delete of it cannot commit between this check and the
            // write, so a child never lands under a deleted parent, while two children of one parent do not serialise.
            parent = await LockAsync(orgId, projectId, parentId, AuthoringEntityKind.FeatureNode, cancellationToken, shared: true).ConfigureAwait(false) is NodeRow row
                ? new FeatureNodeParent(row.Id, row.Kind, row.DeletedAt is not null, SameProject: true)
                : null;

            if (parent is null)
            {
                // A named parent that resolves to nothing is absent, not "no parent was named": AC1's reasons separate
                // the two (`parent_not_found` vs. a kind that cannot stand at the root, which is `parent_kind_invalid`),
                // and a parent in another project or tenant is deliberately indistinguishable from an absent one (AC10).
                return PlacementReason.ParentNotFound;
            }
        }

        var placement = FeatureNodeRules.CheckPlacement(node.Kind, parent);

        return placement.IsAccepted ? null : placement.Reason;
    }

    /// <summary>AC2's <c>position_taken</c>: a node never lands on a position a live sibling holds.</summary>
    private async ValueTask<Error?> CheckNodePositionAsync(
        Guid orgId,
        Guid projectId,
        Guid entityId,
        Guid? parentId,
        int position,
        string path,
        CancellationToken cancellationToken)
    {
        // The root and child predicates are separate statements (OI-13-112): `parent_id is not distinct from @parent_id`
        // is not searchable, so it scanned the whole (org, project) prefix of the table on every create and restore.
        if (position < 0 || position > MaximumSiblingPosition)
        {
            return AuthoringErrors.Invalid(path, "out_of_range", "a sibling position is between 0 and 1000000000");
        }

        var siblings = await db.QuerySingleOrDefaultAsync<SiblingPositionRow>(
            AuthoringSql.NodeSiblingPosition(parentId is null),
            new { org_id = orgId, project_id = projectId, id = entityId, parent_id = parentId, position },
            cancellationToken).ConfigureAwait(false);

        return siblings is { Taken: true }
            ? AuthoringErrors.Invalid(path, "position_taken", "a live sibling already holds that position")
            : null;
    }

    private async ValueTask<Error?> CheckItemParentAsync(
        Guid orgId,
        Guid projectId,
        Guid featureNodeId,
        string path,
        CancellationToken cancellationToken)
    {
        var node = await LockAsync(orgId, projectId, featureNodeId, AuthoringEntityKind.FeatureNode, cancellationToken, shared: true).ConfigureAwait(false) as NodeRow;

        if (node is null)
        {
            return AuthoringErrors.ParentInvalid(PlacementReason.ParentNotFound, path);
        }

        if (node.DeletedAt is not null)
        {
            return AuthoringErrors.ParentInvalid(PlacementReason.ParentDeleted, path);
        }

        return FeatureNodeKinds.ItemParentKinds.Contains(node.Kind, StringComparer.Ordinal)
            ? null
            : AuthoringErrors.ParentInvalid(PlacementReason.ParentKindInvalid, path);
    }

    private async ValueTask<Error?> CheckCaseParentAsync(
        Guid orgId,
        Guid projectId,
        Guid checklistItemId,
        string path,
        CancellationToken cancellationToken)
    {
        var item = await LockAsync(orgId, projectId, checklistItemId, AuthoringEntityKind.ChecklistItem, cancellationToken, shared: true).ConfigureAwait(false) as ItemRow;

        if (item is null)
        {
            return AuthoringErrors.ParentInvalid(PlacementReason.ParentNotFound, path);
        }

        if (item.DeletedAt is not null)
        {
            return AuthoringErrors.ParentInvalid(PlacementReason.ParentDeleted, path);
        }

        return string.Equals(item.State, ChecklistItemStateMachine.Retired, StringComparison.Ordinal)
            ? AuthoringErrors.InvalidTransition("a retired checklist item takes no new content; restore it first")
            : null;
    }

    private async ValueTask<Error?> CheckRestoreParentAsync(
        Guid orgId,
        Guid projectId,
        AuthoringSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        return snapshot switch
        {
            FeatureNodeSnapshot node when node.ParentId is { } parentId => await CheckNodePlacementAsync(orgId, projectId, node, cancellationToken).ConfigureAwait(false) is { } reason
                ? AuthoringErrors.ParentInvalid(reason, "/content/parent_id")
                : null,
            ChecklistItemSnapshot item => await CheckItemParentAsync(orgId, projectId, item.FeatureNodeId, "/content/feature_node_id", cancellationToken).ConfigureAwait(false),
            ManualTestCaseSnapshot manualCase => await CheckCaseParentAsync(orgId, projectId, manualCase.ChecklistItemId, "/content/checklist_item_id", cancellationToken).ConfigureAwait(false),
            _ => null,
        };
    }

    /// <summary>AC8's position rule: an occupied old position re-appends at <c>max + 1</c> instead of failing.</summary>
    private async ValueTask<AuthoringSnapshot> ResolveRestorePositionAsync(
        Guid orgId,
        Guid projectId,
        Guid entityId,
        AuthoringSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        switch (snapshot)
        {
            case FeatureNodeSnapshot node:
                {
                    var siblings = await db.QuerySingleOrDefaultAsync<SiblingPositionRow>(
                        AuthoringSql.NodeSiblingPosition(node.ParentId is null),
                        new { org_id = orgId, project_id = projectId, id = entityId, parent_id = node.ParentId, position = node.Position },
                        cancellationToken).ConfigureAwait(false);

                    return siblings is { Taken: true }
                        ? node with { Position = siblings.MaxPosition + 1 }
                        : node;
                }

            case ChecklistItemSnapshot item:
                {
                    var siblings = await db.QuerySingleOrDefaultAsync<SiblingPositionRow>(
                        AuthoringSql.ItemSiblingPosition,
                        new { org_id = orgId, project_id = projectId, id = entityId, feature_node_id = item.FeatureNodeId, position = item.Position },
                        cancellationToken).ConfigureAwait(false);

                    return siblings is { Taken: true }
                        ? item with { Position = siblings.MaxPosition + 1 }
                        : item;
                }

            case ManualTestCaseSnapshot manualCase:
                return manualCase;

            default:
                throw new InvalidOperationException("unknown snapshot");
        }
    }
}
