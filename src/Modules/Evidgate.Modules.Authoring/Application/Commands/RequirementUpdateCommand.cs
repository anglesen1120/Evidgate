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

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>Edits a requirement and appends one version (<c>PATCH …/authoring/requirements/{r}</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="RequirementId">The requirement from the route.</param>
/// <param name="ExpectedVersion">The <c>If-Match</c> row version the caller read.</param>
/// <param name="Body">The strict JSON object the route read.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record UpdateRequirementCommand(
    Guid ProjectId,
    Guid RequirementId,
    int ExpectedVersion,
    JsonElement Body,
    string IdempotencyKey) : ICommand<Result<RequirementWriteResponse>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="UpdateRequirementCommand"/>. An absent member keeps its value, and a body that is not sent
/// carries the head body into the new version, so history shows every state the requirement was in.
/// </summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, whose credential may be narrowed to one project.</param>
/// <param name="tenant">The request's own tenant context.</param>
/// <param name="writer">The module's requirement write path.</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("authoring.requirement.update")]
[Permission(Permissions.Authoring.Write)]
internal sealed class UpdateRequirementCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    RequirementWriter writer,
    IAuditTrail audit) : ICommandHandler<UpdateRequirementCommand, Result<RequirementWriteResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<RequirementWriteResponse>> Handle(
        UpdateRequirementCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(UpdateRequirementCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (await RequirementGate.RefusalAsync(db, scopes, principals, tenant, command.ProjectId, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return Result.Failure<RequirementWriteResponse>(refusal);
        }

        var read = RequirementBody.Read(command.Body, create: false);
        if (!read.IsSuccess)
        {
            return Result.Failure<RequirementWriteResponse>(read.Error!);
        }

        if (!read.Value.ChangesAnything)
        {
            return Result.Failure<RequirementWriteResponse>(AuthoringErrors.Invalid(
                "/",
                "empty",
                "send at least one of kind, title, body, external_ref, feature_node_id or parent_requirement_id"));
        }

        var orgId = scopes.Require().OrgId;
        var current = await writer.GetAsync(orgId, command.ProjectId, command.RequirementId, cancellationToken).ConfigureAwait(false);
        if (!current.IsSuccess || current.Value.DeletedAt is not null)
        {
            return Result.Failure<RequirementWriteResponse>(current.Error ?? AuthoringErrors.NotFound("no requirement with that id is visible in this project"));
        }

        if (current.Value.Version != command.ExpectedVersion)
        {
            return Result.Failure<RequirementWriteResponse>(AuthoringRequestScope.Stale("requirement", current.Value.Version));
        }

        return await AppendAsync(command, orgId, read.Value, current.Value, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<RequirementWriteResponse>> AppendAsync(
        UpdateRequirementCommand command,
        Guid orgId,
        RequirementFields fields,
        RequirementRow row,
        CancellationToken cancellationToken)
    {
        var before = new RequirementIdentity(row.Kind, row.Title, row.ExternalRef, row.FeatureNodeId, row.ParentRequirementId);
        var after = new RequirementIdentity(
            fields.Kind ?? row.Kind,
            fields.Title ?? row.Title,
            fields.HasExternalRef ? fields.ExternalRef : row.ExternalRef,
            fields.HasFeatureNode ? fields.FeatureNodeId : row.FeatureNodeId,
            fields.HasParent ? fields.ParentRequirementId : row.ParentRequirementId);
        var author = await AuthoringRequestScope.ActorMembershipAsync(db, orgId, principals, cancellationToken).ConfigureAwait(false);
        var written = await writer.UpdateAsync(
            orgId,
            command.ProjectId,
            command.RequirementId,
            command.ExpectedVersion,
            after,
            before,
            fields.Body ?? row.Body,
            "manual",
            author,
            fields.Reason,
            cancellationToken).ConfigureAwait(false);

        if (!written.IsSuccess)
        {
            return Result.Failure<RequirementWriteResponse>(written.Error!);
        }

        audit.Describe(RequirementAudit.Appended(command.ProjectId, command.RequirementId, row, written.Value));

        return Result.Success(RequirementResponses.Write(written.Value));
    }
}
