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
using Mediator;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>Creates a requirement and its first version (<c>POST …/authoring/requirements</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Body">The strict JSON object the route read; the handler reads it member by member.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record CreateRequirementCommand(Guid ProjectId, JsonElement Body, string IdempotencyKey)
    : ICommand<Result<RequirementWriteResponse>>, IIdempotentCommand;

/// <summary>Handler of <see cref="CreateRequirementCommand"/>: the only write path is <see cref="RequirementWriter"/>.</summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, whose credential may be narrowed to one project.</param>
/// <param name="tenant">The request's own tenant context.</param>
/// <param name="writer">The module's requirement write path.</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("authoring.requirement.create")]
[Permission(Permissions.Authoring.Write)]
internal sealed class CreateRequirementCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    RequirementWriter writer,
    IAuditTrail audit) : ICommandHandler<CreateRequirementCommand, Result<RequirementWriteResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<RequirementWriteResponse>> Handle(
        CreateRequirementCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(CreateRequirementCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (await RequirementGate.RefusalAsync(db, scopes, principals, tenant, command.ProjectId, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return Result.Failure<RequirementWriteResponse>(refusal);
        }

        var read = RequirementBody.Read(command.Body, create: true);
        if (!read.IsSuccess)
        {
            return Result.Failure<RequirementWriteResponse>(read.Error!);
        }

        var orgId = scopes.Require().OrgId;
        var fields = read.Value;
        var author = await AuthoringRequestScope.ActorMembershipAsync(db, orgId, principals, cancellationToken).ConfigureAwait(false);
        var created = await writer.CreateAsync(
            orgId,
            new RequirementCreate(
                command.ProjectId,
                fields.Kind!,
                fields.Title!,
                fields.ExternalRef,
                fields.FeatureNodeId,
                fields.ParentRequirementId,
                fields.Body ?? string.Empty,
                "manual",
                author,
                fields.Reason),
            cancellationToken).ConfigureAwait(false);

        if (!created.IsSuccess)
        {
            return Result.Failure<RequirementWriteResponse>(created.Error!);
        }

        audit.Describe(new AuditObject("requirement", created.Value.RequirementId.ToString("D")));

        return Result.Success(RequirementResponses.Write(created.Value), StatusCodes.Status201Created);
    }
}

/// <summary>Maps the writer's head to the wire answer of a write.</summary>
internal static class RequirementResponses
{
    /// <summary>The 200/201 body of create, edit and restore.</summary>
    /// <param name="head">The head the write produced.</param>
    internal static RequirementWriteResponse Write(RequirementHead head) =>
        new(head.RequirementId, head.VersionId, head.VersionNo, head.RowVersion, head.ContentHash);
}
