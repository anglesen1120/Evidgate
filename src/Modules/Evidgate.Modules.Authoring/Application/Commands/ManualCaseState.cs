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
using Evidgate.Modules.Authoring.Domain;
using Mediator;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>
/// Moves one manual test case through §6's state matrix (route 17, 13.1.1 §5, AC21): <c>draft → approved</c> with an
/// optional reason, <c>{draft, approved} → retired</c> with a required 1..500 character reason, <c>retired</c> terminal.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="CaseId">The case from the route.</param>
/// <param name="ExpectedVersion">The <c>If-Match</c> version the caller read.</param>
/// <param name="Body">The parsed request body (<c>{state, reason?}</c>).</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record SetManualCaseStateCommand(Guid ProjectId, Guid CaseId, int ExpectedVersion, JsonElement Body, string IdempotencyKey)
    : ICommand<Result<SetManualCaseStateResponse>>, IIdempotentCommand;

/// <summary>Handler of <see cref="SetManualCaseStateCommand"/> (§6's manual test case matrix, AC21).</summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, for the version row's authoring member.</param>
/// <param name="writer">The module's one write path (§5).</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("authoring.manual_case.state")]
[Permission(Permissions.Authoring.Write)]
internal sealed class SetManualCaseStateCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuthoringVersionWriter writer,
    IAuditTrail audit) : ICommandHandler<SetManualCaseStateCommand, Result<SetManualCaseStateResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<SetManualCaseStateResponse>> Handle(
        SetManualCaseStateCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(SetManualCaseStateCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<SetManualCaseStateResponse>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        // AC10: a soft-deleted project hides its cases, so a state move of one is 404 before the row lock and before
        // its body is read.
        if (await AuthoringProjectProbe.MissingAsync(db, orgId, command.ProjectId, cancellationToken).ConfigureAwait(false) is { } missingProject)
        {
            return Result.Failure<SetManualCaseStateResponse>(missingProject);
        }

        if (ManualCaseBody.RequiredState(command.Body, out var state) is { } stateError)
        {
            return Result.Failure<SetManualCaseStateResponse>(stateError);
        }

        if (ManualCaseBody.OptionalReason(command.Body, out var reason) is { } reasonError)
        {
            return Result.Failure<SetManualCaseStateResponse>(reasonError);
        }

        var locked = await ManualCaseCommands.LockAsync(db, orgId, command.ProjectId, command.CaseId, cancellationToken)
            .ConfigureAwait(false);

        if (locked.Row is not { } row)
        {
            return Result.Failure<SetManualCaseStateResponse>(locked.Error!);
        }

        if (row.Version != command.ExpectedVersion)
        {
            return Result.Failure<SetManualCaseStateResponse>(AuthoringRequestScope.Stale("manual test case", row.Version));
        }

        var content = JsonSerializer.SerializeToElement(AuthoringContent.ManualTestCase(
            row.ChecklistItemId,
            row.Code,
            row.Title,
            row.Description,
            row.Precondition,
            ManualCaseBody.ParseDocument(row.Steps),
            row.Priority,
            state));

        var actor = await ManualCaseActor.MembershipIdAsync(db, principals, orgId, cancellationToken).ConfigureAwait(false);
        var written = await writer.AppendAsync(
            orgId,
            command.ProjectId,
            Guid.CreateVersion7(),
            actor,
            [EntityChange.Update(AuthoringEntityKind.ManualTestCase, command.CaseId, content)],
            reason is null ? null : new AuthoringAppendContext { Reason = reason },
            cancellationToken).ConfigureAwait(false);

        if (!written.IsSuccess)
        {
            // The writer owns §6's matrix: a refused edge is 409 invalid_transition and a retire without a reason is
            // 422 with details[].path = /reason (AC21).
            return Result.Failure<SetManualCaseStateResponse>(ManualCaseRefusal.ToError(written.Refusal!));
        }

        var unchanged = written.Value!.UnchangedEntityIds.Contains(command.CaseId);

        if (unchanged)
        {
            audit.Suppress();
        }
        else
        {
            audit.Describe(new AuditObject("manual_test_case", command.CaseId.ToString("D")));
        }

        return Result.Success(new SetManualCaseStateResponse(
            state,
            unchanged ? row.Version : written.Value.Versions[0].VersionNo));
    }
}
