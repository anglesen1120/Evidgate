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
using Mediator;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>Hides a requirement from the live list (<c>DELETE …/authoring/requirements/{r}</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="RequirementId">The requirement from the route.</param>
/// <param name="ExpectedVersion">The <c>If-Match</c> row version the caller read.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record DeleteRequirementCommand(Guid ProjectId, Guid RequirementId, int ExpectedVersion, string IdempotencyKey)
    : ICommand<Result>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="DeleteRequirementCommand"/>. The requirement is soft-deleted: its versions, its head pointer
/// and every row that names it stay, so a pin recorded against it still resolves.
/// </summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, whose credential may be narrowed to one project.</param>
/// <param name="tenant">The request's own tenant context.</param>
/// <param name="writer">The module's requirement write path.</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("authoring.requirement.delete")]
[Permission(Permissions.Authoring.Write)]
internal sealed class DeleteRequirementCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    RequirementWriter writer,
    IAuditTrail audit) : ICommandHandler<DeleteRequirementCommand, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(DeleteRequirementCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(DeleteRequirementCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (await RequirementGate.RefusalAsync(db, scopes, principals, tenant, command.ProjectId, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return Result.Failure(refusal);
        }

        var deleted = await writer.DeleteAsync(
            scopes.Require().OrgId,
            command.ProjectId,
            command.RequirementId,
            command.ExpectedVersion,
            cancellationToken).ConfigureAwait(false);

        if (!deleted.IsSuccess)
        {
            return deleted;
        }

        audit.Describe(new AuditObject("requirement", command.RequirementId.ToString("D")));

        return Result.Success(StatusCodes.Status204NoContent);
    }
}
