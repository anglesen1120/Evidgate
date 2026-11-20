using System.Text.Json;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Mediator;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>
/// Re-parents one manual test case (route 19, AC22): the target item is locked first, the case's <c>If-Match</c> is
/// compared second, and the move itself runs through <see cref="IAuthoringVersionWriter.MoveManualTestCaseAsync"/>,
/// which owns every guard (missing/deleted/other-project target, retired case or target).
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="CaseId">The case from the route.</param>
/// <param name="ExpectedVersion">The <c>If-Match</c> version the caller read.</param>
/// <param name="Body">The parsed request body (<c>{checklist_item_id, reason?}</c>).</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record MoveManualTestCaseCommand(Guid ProjectId, Guid CaseId, int ExpectedVersion, JsonElement Body, string IdempotencyKey)
    : ICommand<Result<MoveManualCaseResponse>>, IIdempotentCommand;

/// <summary>Handler of <see cref="MoveManualTestCaseCommand"/> (AC22).</summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, for the version row's authoring member.</param>
/// <param name="writer">The module's one write path, whose move owns the guards of AC22.</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("authoring.manual_case.move")]
[Permission(Permissions.Authoring.Write)]
internal sealed class MoveManualTestCaseCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuthoringVersionWriter writer,
    IAuditTrail audit) : ICommandHandler<MoveManualTestCaseCommand, Result<MoveManualCaseResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<MoveManualCaseResponse>> Handle(
        MoveManualTestCaseCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(MoveManualTestCaseCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<MoveManualCaseResponse>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        // AC10: a soft-deleted project hides its cases and its items, so the move is 404 before either lock.
        if (await AuthoringProjectProbe.MissingAsync(db, orgId, command.ProjectId, cancellationToken).ConfigureAwait(false) is { } missingProject)
        {
            return Result.Failure<MoveManualCaseResponse>(missingProject);
        }

        if (ManualCaseBody.RequiredId(command.Body, "checklist_item_id", out var toItemId) is { } itemError)
        {
            return Result.Failure<MoveManualCaseResponse>(itemError);
        }

        if (ManualCaseBody.OptionalReason(command.Body, out var reason) is { } reasonError)
        {
            return Result.Failure<MoveManualCaseResponse>(reasonError);
        }

        // Lock order of the writer's own move: the target item first, the case second (OI-13-127). Taking them here in
        // that order is what makes two concurrent moves deadlock-free; the version comparison under the case lock turns
        // the loser into a 412 rather than a second version row (AC19). The 5 s lock_timeout is set before the first of
        // the two locks and covers both (AuthoringLockGuard): the writer's own bound only starts inside its append, so
        // a target row another transaction holds would otherwise wait here for as long as that holder lives.
        await AuthoringLockGuard.SetLockTimeoutAsync(db, cancellationToken).ConfigureAwait(false);

        try
        {
            await db.ScalarAsync<Guid?>(
                AuthoringManualCaseReadSql.LockItem,
                new { org_id = orgId, project_id = command.ProjectId, item_id = toItemId },
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (AuthoringLockGuard.IsContention(exception))
        {
            // The writer answers 55P03/40P01 with the same retryable 503; a lock this command takes first must not
            // surface as a 500 instead (13.1.1 §5, 00 §9.2).
            return Result.Failure<MoveManualCaseResponse>(AuthoringErrors.Unavailable());
        }

        var locked = await ManualCaseCommands.LockAsync(db, orgId, command.ProjectId, command.CaseId, cancellationToken)
            .ConfigureAwait(false);

        if (locked.Row is not { } row)
        {
            return Result.Failure<MoveManualCaseResponse>(locked.Error!);
        }

        if (row.Version != command.ExpectedVersion)
        {
            return Result.Failure<MoveManualCaseResponse>(AuthoringRequestScope.Stale("manual test case", row.Version));
        }

        var actor = await ManualCaseActor.MembershipIdAsync(db, principals, orgId, cancellationToken).ConfigureAwait(false);
        var moved = await writer.MoveManualTestCaseAsync(
            orgId,
            command.ProjectId,
            Guid.CreateVersion7(),
            actor,
            command.CaseId,
            toItemId,
            reason,
            null,
            cancellationToken).ConfigureAwait(false);

        if (!moved.IsSuccess)
        {
            return Result.Failure<MoveManualCaseResponse>(ManualCaseRefusal.ToError(moved.Refusal!));
        }

        if (moved.Value!.Moved)
        {
            audit.Describe(new AuditObject("manual_test_case", command.CaseId.ToString("D")));
        }
        else
        {
            // A move onto the item the case already sits under writes nothing (AC5/AC22), so there is nothing to audit.
            audit.Suppress();
        }

        return Result.Success(new MoveManualCaseResponse(moved.Value.EntityVersion, toItemId));
    }
}
