using System.Text.Json.Serialization;
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
    private async ValueTask<Result<PendingVersion?>> SoftDeleteAsync(
        Guid orgId,
        Guid projectId,
        int index,
        EntityChange change,
        AuthoringAppendContext settings,
        CancellationToken cancellationToken)
    {
        var path = "/changes/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var stored = await LockAsync(orgId, projectId, change.EntityId, change.EntityKind, cancellationToken).ConfigureAwait(false);
        if (stored is null || IsDeleted(stored))
        {
            return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.EntityNotFound(change.EntityKind));
        }

        // AC7's non-empty guard lives here, inside the writer, so 13.1.2's delete and merge compose from SoftDelete
        // without restating it.
        switch (stored)
        {
            case NodeRow:
                {
                    var contents = await db.QuerySingleOrDefaultAsync<NodeContentsRow>(
                        AuthoringSql.NodeContents,
                        new { org_id = orgId, project_id = projectId, id = change.EntityId },
                        cancellationToken).ConfigureAwait(false);

                    if (contents is null)
                    {
                        throw new InvalidOperationException("the non-empty probe of a feature node returned no row");
                    }

                    if (contents.HasChildren)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.NotEmpty("node_has_children", path));
                    }

                    if (contents.HasItems)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.NotEmpty("node_has_items", path));
                    }

                    break;
                }

            case ItemRow:
                {
                    var contents = await db.QuerySingleOrDefaultAsync<ItemContentsRow>(
                        AuthoringSql.ItemContents,
                        new { org_id = orgId, project_id = projectId, id = change.EntityId },
                        cancellationToken).ConfigureAwait(false);

                    if (contents is null)
                    {
                        throw new InvalidOperationException("the non-empty probe of a checklist item returned no row");
                    }

                    if (contents.HasCases)
                    {
                        return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.NotEmpty("item_has_cases", path));
                    }

                    break;
                }

            case CaseRow:
                break;

            default:
                throw new InvalidOperationException("the locked row is not an authoring entity");
        }

        var deleted = await db.QuerySingleOrDefaultAsync<VersionRow>(
            AuthoringSql.SoftDeleteEntity(change.EntityKind),
            new { org_id = orgId, project_id = projectId, id = change.EntityId },
            cancellationToken).ConfigureAwait(false);

        if (deleted is null)
        {
            return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.EntityNotFound(change.EntityKind));
        }

        var lastVersionNo = stored switch
        {
            NodeRow node => node.LastVersionNo,
            ItemRow item => item.LastVersionNo,
            CaseRow manualCase => manualCase.LastVersionNo,
            _ => 0,
        };

        if (deleted.Version != lastVersionNo + 1)
        {
            return AuthoringErrors.Fail<PendingVersion?>(AuthoringErrors.InvalidTransition(
                "the version history of this entity is out of step"));
        }

        AuthoringSnapshot snapshot = stored switch
        {
            NodeRow node => SnapshotOf(node),
            ItemRow item => SnapshotOf(item),
            CaseRow manualCase => SnapshotOf(manualCase),
            _ => throw new InvalidOperationException("the locked row is not an authoring entity"),
        };

        return Result.Success<PendingVersion?>(Pending(
            change.EntityKind,
            change.EntityId,
            deleted.Version,
            AuthoringWireValues.DeleteChangeKind,
            snapshot,
            change.Reason ?? settings.Reason));
    }
}
