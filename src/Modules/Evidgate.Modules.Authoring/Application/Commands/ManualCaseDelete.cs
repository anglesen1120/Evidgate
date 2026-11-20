using System.Text.Json.Nodes;
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
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>
/// Soft-deletes one manual test case (route 16, 13.1.1 §5): <c>If-Match</c> on the row, an optional bounded
/// <c>reason</c>, and the writer's own non-empty guard behind it (AC7).
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="CaseId">The case from the route.</param>
/// <param name="ExpectedVersion">The <c>If-Match</c> version the caller read.</param>
/// <param name="Reason">The optional reason from the body, already bounded by the route's strict shape.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record DeleteManualCaseCommand(Guid ProjectId, Guid CaseId, int ExpectedVersion, string? Reason, string IdempotencyKey)
    : ICommand<Result>, IIdempotentCommand;

/// <summary>Handler of <see cref="DeleteManualCaseCommand"/>.</summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, for the version row's authoring member.</param>
/// <param name="writer">The module's one write path (§5).</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("authoring.manual_case.delete")]
[Permission(Permissions.Authoring.Write)]
internal sealed class DeleteManualCaseCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuthoringVersionWriter writer,
    IAuditTrail audit) : ICommandHandler<DeleteManualCaseCommand, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(DeleteManualCaseCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(DeleteManualCaseCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        // AC10: a soft-deleted project hides its cases, so the liveness probe answers before the case row lock and no
        // delete of a deleted project's case is versioned.
        if (await AuthoringProjectProbe.MissingAsync(db, orgId, command.ProjectId, cancellationToken).ConfigureAwait(false) is { } missingProject)
        {
            return Result.Failure(missingProject);
        }

        var locked = await ManualCaseCommands.LockAsync(db, orgId, command.ProjectId, command.CaseId, cancellationToken)
            .ConfigureAwait(false);

        if (locked.Row is not { } row)
        {
            return Result.Failure(locked.Error!);
        }

        if (row.Version != command.ExpectedVersion)
        {
            return Result.Failure(AuthoringRequestScope.Stale("manual test case", row.Version));
        }

        var actor = await ManualCaseActor.MembershipIdAsync(db, principals, orgId, cancellationToken).ConfigureAwait(false);
        var written = await writer.AppendAsync(
            orgId,
            command.ProjectId,
            Guid.CreateVersion7(),
            actor,
            [EntityChange.SoftDelete(AuthoringEntityKind.ManualTestCase, command.CaseId, command.Reason)],
            command.Reason is { } reason ? new AuthoringAppendContext { Reason = reason } : null,
            cancellationToken).ConfigureAwait(false);

        if (!written.IsSuccess)
        {
            return Result.Failure(ManualCaseRefusal.ToError(written.Refusal!));
        }

        audit.Describe(new AuditObject("manual_test_case", command.CaseId.ToString("D")));

        return Result.Success(StatusCodes.Status204NoContent);
    }
}
