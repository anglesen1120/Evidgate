using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application;

/// <summary>A request to pin one target to the current head of a requirement.</summary>
/// <param name="ProjectId">The project the requirement and the target must belong to.</param>
/// <param name="RequirementId">The requirement.</param>
/// <param name="ChecklistItemId">The checklist item to link; exactly one of the two targets is set.</param>
/// <param name="ManualTestCaseId">The manual test case to link.</param>
/// <param name="ExpectedVersionId">Required: the head version the caller read; absent is a 422 and a moved head is refused as stale.</param>
/// <param name="LinkedByMembershipId">The author, or null for a service principal.</param>
internal sealed record RequirementLinkCreate(
    Guid ProjectId,
    Guid RequirementId,
    Guid? ChecklistItemId,
    Guid? ManualTestCaseId,
    Guid? ExpectedVersionId,
    Guid? LinkedByMembershipId);

/// <summary>The link a create produced or found.</summary>
/// <param name="LinkId">The link row.</param>
/// <param name="RequirementId">The linked requirement.</param>
/// <param name="VersionId">The pinned version.</param>
/// <param name="VersionNo">Its number.</param>
/// <param name="ChecklistItemId">The linked item, when the target is an item.</param>
/// <param name="ManualTestCaseId">The linked case, when the target is a case.</param>
/// <param name="Changed">False when the target was already linked at the head and nothing was written.</param>
internal sealed record RequirementLinkRow(
    Guid LinkId,
    Guid RequirementId,
    Guid VersionId,
    int VersionNo,
    Guid? ChecklistItemId,
    Guid? ManualTestCaseId,
    bool Changed);

/// <summary>The link an unlink removed, as the row looked when it was deleted.</summary>
/// <param name="LinkId">The removed link.</param>
/// <param name="VersionId">The version it pinned.</param>
/// <param name="VersionNo">That version's number.</param>
/// <param name="ChecklistItemId">The item it linked, when the target was an item.</param>
/// <param name="ManualTestCaseId">The case it linked, when the target was a case.</param>
internal sealed record RequirementLinkRemoved(
    Guid LinkId,
    Guid VersionId,
    int VersionNo,
    Guid? ChecklistItemId,
    Guid? ManualTestCaseId);

internal sealed partial class RequirementWriter
{
    /// <summary>
    /// Pins a target to the requirement's current head version. The requirement row is share-locked first, so a
    /// concurrent append waits and the pin is the head at commit. A target that is already linked at the head is a
    /// no-op (<see cref="RequirementLinkRow.Changed"/> false); one linked at an older version is a 409, because moving
    /// a pin is a different operation than creating a link.
    /// </summary>
    /// <param name="orgId">The tenant; must equal the open scope's organization.</param>
    /// <param name="create">The request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async ValueTask<Result<RequirementLinkRow>> CreateLinkAsync(
        Guid orgId,
        RequirementLinkCreate create,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(create);
        EnsureScope(orgId);

        if (!RequirementLinkRules.HasExactlyOneTarget(create.ChecklistItemId, create.ManualTestCaseId))
        {
            return Failure<RequirementLinkRow>(AuthoringErrors.Invalid("/target", "one_target", "name exactly one checklist item or one manual test case"));
        }

        if (create.ExpectedVersionId is not { } expected)
        {
            return Failure<RequirementLinkRow>(AuthoringErrors.Invalid("/expected_version_id", "required", "name the head version id the link is based on"));
        }

        try
        {
            await AuthoringLockGuard.SetLockTimeoutAsync(db, cancellationToken).ConfigureAwait(false);

            var head = await LockHeadAsync(orgId, create, cancellationToken).ConfigureAwait(false);
            if (head is null)
            {
                return Failure<RequirementLinkRow>(AuthoringErrors.NotFound("no live requirement with that id is visible in this project"));
            }

            if (expected != head.HeadVersionId)
            {
                return Failure<RequirementLinkRow>(AuthoringRequestScope.StalePin(head.HeadVersionId));
            }

            if (await TargetRefusalAsync(orgId, create, cancellationToken).ConfigureAwait(false) is { } refusal)
            {
                return Failure<RequirementLinkRow>(refusal);
            }

            return await InsertLinkAsync(orgId, create, head, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (AuthoringLockGuard.IsContention(exception))
        {
            return Failure<RequirementLinkRow>(AuthoringErrors.Unavailable());
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // A target deleted for good between the liveness read and the insert; the composite keys refuse it.
            return Failure<RequirementLinkRow>(AuthoringErrors.Invalid("/target", "not_found", "the target is not in this project"));
        }
    }

    /// <summary>
    /// Removes one link. A soft-deleted requirement keeps its links removable, so this reads no liveness; the statement's
    /// project and requirement filters answer a link of another project or requirement as a missing row.
    /// </summary>
    /// <param name="orgId">The tenant; must equal the open scope's organization.</param>
    /// <param name="projectId">The project from the route.</param>
    /// <param name="requirementId">The requirement from the route.</param>
    /// <param name="linkId">The link to remove.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async ValueTask<Result<RequirementLinkRemoved>> RemoveLinkAsync(
        Guid orgId,
        Guid projectId,
        Guid requirementId,
        Guid linkId,
        CancellationToken cancellationToken = default)
    {
        EnsureScope(orgId);

        try
        {
            await AuthoringLockGuard.SetLockTimeoutAsync(db, cancellationToken).ConfigureAwait(false);

            var removed = await db.QuerySingleOrDefaultAsync<RequirementLinkRemoved>(
                RequirementLinkSql.DeleteLink,
                new { org_id = orgId, project_id = projectId, requirement_id = requirementId, id = linkId },
                cancellationToken).ConfigureAwait(false);

            return removed is not null
                ? Result.Success(removed)
                : Failure<RequirementLinkRemoved>(AuthoringErrors.NotFound("no such link on this requirement in this project"));
        }
        catch (PostgresException exception) when (AuthoringLockGuard.IsContention(exception))
        {
            return Failure<RequirementLinkRemoved>(AuthoringErrors.Unavailable());
        }
    }

    /// <summary>
    /// Share-locks the requirement row, then reads its head version in a second statement: the lock statement keeps
    /// the snapshot it started with, so a head version inserted by the writer it waited for is only visible to the next one.
    /// </summary>
    private async Task<LinkHead?> LockHeadAsync(Guid orgId, RequirementLinkCreate create, CancellationToken cancellationToken)
    {
        var head = await db.QuerySingleOrDefaultAsync<LinkHead>(
            RequirementLinkSql.LockRequirement,
            new { org_id = orgId, project_id = create.ProjectId, requirement_id = create.RequirementId },
            cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            return null;
        }

        head.HeadVersionId = await db.ScalarAsync<Guid>(
            RequirementLinkSql.HeadVersionId,
            new { org_id = orgId, requirement_id = create.RequirementId, head_version_no = head.HeadVersionNo },
            cancellationToken).ConfigureAwait(false);

        return head;
    }

    private async Task<Error?> TargetRefusalAsync(Guid orgId, RequirementLinkCreate create, CancellationToken cancellationToken)
    {
        var (sql, id, path, message) = create.ChecklistItemId is { } item
            ? (RequirementLinkSql.ChecklistItemExists, item, "/checklist_item_id", "no live checklist item with that id in this project")
            : (RequirementLinkSql.ManualCaseExists, create.ManualTestCaseId!.Value, "/manual_test_case_id", "no live manual test case with that id in this project");

        return await ExistsAsync(sql, orgId, create.ProjectId, id, cancellationToken).ConfigureAwait(false)
            ? null
            : AuthoringErrors.Invalid(path, "not_found", message);
    }

    private async Task<Result<RequirementLinkRow>> InsertLinkAsync(
        Guid orgId,
        RequirementLinkCreate create,
        LinkHead head,
        CancellationToken cancellationToken)
    {
        var inserted = await db.ScalarAsync<Guid?>(
            RequirementLinkSql.InsertLink,
            new
            {
                id = Guid.CreateVersion7(),
                org_id = orgId,
                project_id = create.ProjectId,
                requirement_id = create.RequirementId,
                requirement_version_id = head.HeadVersionId,
                checklist_item_id = create.ChecklistItemId,
                manual_test_case_id = create.ManualTestCaseId,
                membership_id = create.LinkedByMembershipId,
            },
            cancellationToken).ConfigureAwait(false);

        if (inserted is { } linkId)
        {
            return Result.Success(Row(create, linkId, head.HeadVersionId, head.HeadVersionNo, changed: true));
        }

        var existing = await db.QuerySingleOrDefaultAsync<ExistingLinkRow>(
            RequirementLinkSql.ExistingLink,
            new
            {
                org_id = orgId,
                project_id = create.ProjectId,
                requirement_id = create.RequirementId,
                checklist_item_id = create.ChecklistItemId,
                manual_test_case_id = create.ManualTestCaseId,
            },
            cancellationToken).ConfigureAwait(false);

        // The conflicting row can be gone again if an unlink committed in between: the caller retries.
        if (existing is null)
        {
            return Failure<RequirementLinkRow>(AuthoringErrors.Unavailable());
        }

        return existing.VersionId == head.HeadVersionId
            ? Result.Success(Row(create, existing.LinkId, existing.VersionId, existing.VersionNo, changed: false))
            : Failure<RequirementLinkRow>(AuthoringErrors.InvalidTransition("the target is already linked to an older version of this requirement"));
    }

    private static RequirementLinkRow Row(RequirementLinkCreate create, Guid linkId, Guid versionId, int versionNo, bool changed) =>
        new(linkId, create.RequirementId, versionId, versionNo, create.ChecklistItemId, create.ManualTestCaseId, changed);

    private static Result<T> Failure<T>(Error error) => Result.Failure<T>(error);

    private sealed class LinkHead
    {
        public int HeadVersionNo { get; set; }

        public Guid HeadVersionId { get; set; }
    }

    private sealed class ExistingLinkRow
    {
        public Guid LinkId { get; set; }

        public Guid VersionId { get; set; }

        public int VersionNo { get; set; }
    }
}
