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
    // ------------------------------------------------------------------ MoveManualTestCaseAsync

    /// <summary>
    /// Re-parents one manual test case (AC22): the target item is locked first and the case second, exactly the order
    /// 13.1.2's apply already holds, so a nested call cannot invert it into a deadlock. The move keeps the code, bumps
    /// the case's version and appends one <c>update</c> row with the new <c>checklist_item_id</c>.
    /// </summary>
    /// <param name="orgId">The tenant; must equal the open scope's organization.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="batchId">The batch of the calling command.</param>
    /// <param name="actorMembershipId">The authoring member, or null.</param>
    /// <param name="caseId">The case to move.</param>
    /// <param name="toItemId">The target checklist item.</param>
    /// <param name="reason">An optional 1..500 character reason.</param>
    /// <param name="context">Source, reason and AI run of the call.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async ValueTask<Result<AuthoringMoveOutcome>> MoveManualTestCaseAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        Guid caseId,
        Guid toItemId,
        string? reason = null,
        AuthoringAppendContext? context = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await MoveManualTestCaseCoreAsync(orgId, projectId, batchId, actorMembershipId, caseId, toItemId, reason, context, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (IsContention(exception))
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(AuthoringErrors.Unavailable());
        }
    }

    private async ValueTask<Result<AuthoringMoveOutcome>> MoveManualTestCaseCoreAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        Guid caseId,
        Guid toItemId,
        string? reason = null,
        AuthoringAppendContext? context = null,
        CancellationToken cancellationToken = default)
    {
        EnsureScope(orgId);

        var settings = context ?? new AuthoringAppendContext();

        if (projectId == Guid.Empty || caseId == Guid.Empty || toItemId == Guid.Empty)
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(AuthoringErrors.Invalid(
                "/checklist_item_id",
                "required",
                "the project, the case and the target item are required"));
        }

        if (batchId == Guid.Empty)
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(AuthoringErrors.Invalid("/batch_id", "required", "the batch id is required"));
        }

        if (CheckContext(settings) is { } contextFailure)
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(contextFailure);
        }

        await db.ExecuteAsync(AuthoringSql.SetLockTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

        var effectiveReason = reason ?? settings.Reason;
        if (effectiveReason is not null && !IsReasonLengthValid(effectiveReason))
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(AuthoringErrors.Invalid("/reason", "length_invalid", "a reason is 1..500 characters"));
        }

        // Lock order: the target item first, the case second (AC22, 13.1.2-AC13).
        var target = await LockAsync(orgId, projectId, toItemId, AuthoringEntityKind.ChecklistItem, cancellationToken).ConfigureAwait(false);
        if (target is not ItemRow item)
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(AuthoringErrors.ParentInvalid(
                PlacementReason.ParentNotFound,
                "/checklist_item_id"));
        }

        if (item.DeletedAt is not null)
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(AuthoringErrors.ParentInvalid(
                PlacementReason.ParentDeleted,
                "/checklist_item_id"));
        }

        if (string.Equals(item.State, ChecklistItemStateMachine.Retired, StringComparison.Ordinal))
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(AuthoringErrors.InvalidTransition(
                "a retired checklist item takes no new content; restore it first"));
        }

        var stored = await LockAsync(orgId, projectId, caseId, AuthoringEntityKind.ManualTestCase, cancellationToken).ConfigureAwait(false);
        if (stored is not CaseRow existing || existing.DeletedAt is not null)
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(AuthoringErrors.EntityNotFound(AuthoringEntityKind.ManualTestCase));
        }

        if (string.Equals(existing.State, ManualTestCaseStateMachine.Retired, StringComparison.Ordinal))
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(AuthoringErrors.InvalidTransition(
                "a retired manual test case takes no edit"));
        }

        if (existing.ChecklistItemId == toItemId)
        {
            return Result.Success(new AuthoringMoveOutcome(existing.Version, null));
        }

        var version = await db.QuerySingleOrDefaultAsync<VersionRow>(
            AuthoringSql.UpdateEntity(AuthoringEntityKind.ManualTestCase),
            new
            {
                id = caseId,
                org_id = orgId,
                project_id = projectId,
                checklist_item_id = toItemId,
                code = existing.Code,
                title = existing.Title,
                description = existing.Description,
                precondition = existing.Precondition,
                steps = existing.Steps,
                priority = existing.Priority,
                state = existing.State,
            },
            cancellationToken).ConfigureAwait(false);

        if (version is null)
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(AuthoringErrors.EntityNotFound(AuthoringEntityKind.ManualTestCase));
        }

        if (version.Version != existing.LastVersionNo + 1)
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(AuthoringErrors.InvalidTransition(
                "the version history of this manual test case is out of step"));
        }

        var snapshot = SnapshotOf(existing) with { ChecklistItemId = toItemId };
        var pending = Pending(AuthoringEntityKind.ManualTestCase, caseId, version.Version, AuthoringWireValues.UpdateChangeKind, snapshot, effectiveReason);

        if (await AppendVersionsAsync(orgId, projectId, batchId, actorMembershipId, settings, [pending], cancellationToken).ConfigureAwait(false) is { } failure)
        {
            return AuthoringErrors.Fail<AuthoringMoveOutcome>(failure);
        }

        return Result.Success(new AuthoringMoveOutcome(
            version.Version,
            new AuthoringVersionRef(pending.VersionId, pending.EntityKind, pending.EntityId, pending.VersionNo, pending.ContentHash, batchId)));
    }
}
