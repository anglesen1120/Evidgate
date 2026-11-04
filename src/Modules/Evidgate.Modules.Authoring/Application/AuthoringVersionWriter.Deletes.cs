using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;

namespace Evidgate.Modules.Authoring.Application;

/// <summary>
/// The set-based half of <see cref="AuthoringVersionWriter"/>: a batch that soft-deletes checklist items takes one
/// lock, one last-version read, one non-empty probe and one update for the whole batch instead of four statements per
/// entity, so a 200-change apply stays inside the statement budget.
/// </summary>
/// <remarks>
/// The invariants are the per-entity path's own, only computed for the selection at once: the rows are locked in
/// ascending id order (the writer-wide <c>checklist_item</c> order), a row that is gone or already deleted is a 404, a
/// live manual case on any selected item refuses the whole batch with 409 <c>authoring_not_empty</c> before the update,
/// and every written <c>version</c> must equal its last version row plus one or the call is
/// <c>409 invalid_transition</c>. A caller's own change order decides the order of the appended versions, so the
/// response does not depend on how Dapper returned the update's rows.
/// </remarks>
internal sealed partial class AuthoringVersionWriter
{
    /// <summary>
    /// Appends one soft-delete version per checklist item of <paramref name="changes"/> under one batch. Every entry
    /// has already been validated by <see cref="AppendAsync"/>: an item kind, a soft delete, a non-empty distinct id and
    /// a reason inside the table's bound.
    /// </summary>
    /// <param name="db">The tenant-bound database of the caller's scope.</param>
    /// <param name="orgId">The tenant; already checked against the open scope.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="batchId">The batch every row of the call shares.</param>
    /// <param name="actorMembershipId">The authoring member, or null.</param>
    /// <param name="changes">The validated batch, each entry a checklist-item soft delete.</param>
    /// <param name="settings">Source, reason and AI run of the call.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<Result<AuthoringAppendOutcome>> AppendChecklistItemDeletesAsync(
        ITenantDb db,
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        List<(EntityChange Change, AuthoringSnapshot? Snapshot)> changes,
        AuthoringAppendContext settings,
        CancellationToken cancellationToken)
    {
        var ids = changes.Select(entry => entry.Change.EntityId).OrderBy(id => id).ToArray();
        var indexOf = new Dictionary<Guid, int>(ids.Length);
        for (var index = 0; index < changes.Count; index++)
        {
            indexOf[changes[index].Change.EntityId] = index;
        }

        var parameters = new { org_id = orgId, project_id = projectId, ids };

        var locked = await db.QueryAsync<ItemRow>(
            AuthoringDeleteBatchSql.LockItems, parameters, cancellationToken).ConfigureAwait(false);

        // A row that is absent, in another project or already soft-deleted answers the per-entity 404 of the writer,
        // and the whole batch is refused: a caller cannot prove a version row against a row it cannot see.
        if (locked.Count != ids.Length || locked.Any(row => row.DeletedAt is not null))
        {
            return AuthoringErrors.Fail<AuthoringAppendOutcome>(
                AuthoringErrors.EntityNotFound(AuthoringEntityKind.ChecklistItem));
        }

        var lastVersions = await db.QueryAsync<DeleteLastVersionRow>(
            AuthoringDeleteBatchSql.LastVersions, parameters, cancellationToken).ConfigureAwait(false);
        var lastById = lastVersions.ToDictionary(row => row.EntityId, row => row.VersionNo);

        // The non-empty guard runs before the update, so a refused batch leaves every selected item untouched.
        if (await db.ScalarAsync<Guid?>(
                AuthoringDeleteBatchSql.FirstLiveCase, parameters, cancellationToken).ConfigureAwait(false) is { } busyItem)
        {
            return AuthoringErrors.Fail<AuthoringAppendOutcome>(AuthoringErrors.NotEmpty(
                "item_has_cases",
                "/changes/" + indexOf[busyItem].ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var deleted = await db.QueryAsync<DeletedItemRow>(
            AuthoringDeleteBatchSql.SoftDeleteItems, parameters, cancellationToken).ConfigureAwait(false);
        var deletedById = deleted.ToDictionary(row => row.Id, row => row.Version);

        if (deletedById.Count != ids.Length)
        {
            return AuthoringErrors.Fail<AuthoringAppendOutcome>(
                AuthoringErrors.EntityNotFound(AuthoringEntityKind.ChecklistItem));
        }

        var rows = locked.ToDictionary(row => row.Id);
        var appended = new List<PendingVersion>(changes.Count);

        foreach (var (change, _) in changes)
        {
            if (!rows.TryGetValue(change.EntityId, out var row) || !deletedById.TryGetValue(change.EntityId, out var version))
            {
                return AuthoringErrors.Fail<AuthoringAppendOutcome>(
                    AuthoringErrors.EntityNotFound(AuthoringEntityKind.ChecklistItem));
            }

            var lastVersionNo = lastById.TryGetValue(change.EntityId, out var last) ? last : 0;

            // The same out-of-step guard the per-entity delete applies: the row's new version must extend its history,
            // otherwise the stored past and the row disagree and no honest version row can be written.
            if (version != lastVersionNo + 1)
            {
                return AuthoringErrors.Fail<AuthoringAppendOutcome>(AuthoringErrors.InvalidTransition(
                    "the version history of this entity is out of step"));
            }

            appended.Add(Pending(
                change.EntityKind,
                change.EntityId,
                version,
                AuthoringWireValues.DeleteChangeKind,
                SnapshotOf(row),
                change.Reason ?? settings.Reason));
        }

        if (await AppendVersionsAsync(orgId, projectId, batchId, actorMembershipId, settings, appended, cancellationToken)
                .ConfigureAwait(false) is { } failure)
        {
            return AuthoringErrors.Fail<AuthoringAppendOutcome>(failure);
        }

        return Result.Success(new AuthoringAppendOutcome(
            [
                .. appended.Select(row => new AuthoringVersionRef(
                    row.VersionId, row.EntityKind, row.EntityId, row.VersionNo, row.ContentHash, batchId)),
            ],
            []));
    }
}
