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

/// <summary>Restores an earlier body as a new version (<c>POST …/authoring/requirements/{r}/restore</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="RequirementId">The requirement from the route.</param>
/// <param name="ExpectedVersion">The <c>If-Match</c> row version the caller read.</param>
/// <param name="Body">The strict JSON object the route read: <c>version_id</c> and <c>reason</c>.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record RestoreRequirementCommand(
    Guid ProjectId,
    Guid RequirementId,
    int ExpectedVersion,
    JsonElement Body,
    string IdempotencyKey) : ICommand<Result<RequirementWriteResponse>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="RestoreRequirementCommand"/>. A restore is an append: the chosen version's body becomes the
/// newest version and the head only ever moves forward, so no history row is rewritten and the head number stays the
/// highest version number.
/// </summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, whose credential may be narrowed to one project.</param>
/// <param name="tenant">The request's own tenant context.</param>
/// <param name="writer">The module's requirement write path.</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("authoring.requirement.restore")]
[Permission(Permissions.Authoring.Write)]
internal sealed class RestoreRequirementCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    RequirementWriter writer,
    IAuditTrail audit) : ICommandHandler<RestoreRequirementCommand, Result<RequirementWriteResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<RequirementWriteResponse>> Handle(
        RestoreRequirementCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(RestoreRequirementCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (await RequirementGate.RefusalAsync(db, scopes, principals, tenant, command.ProjectId, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return Result.Failure<RequirementWriteResponse>(refusal);
        }

        var read = RequirementBody.ReadRestore(command.Body);
        if (!read.IsSuccess)
        {
            return Result.Failure<RequirementWriteResponse>(read.Error!);
        }

        var orgId = scopes.Require().OrgId;
        var current = await writer.GetAsync(orgId, command.ProjectId, command.RequirementId, cancellationToken).ConfigureAwait(false);
        if (!current.IsSuccess || current.Value.DeletedAt is not null)
        {
            return Result.Failure<RequirementWriteResponse>(current.Error ?? AuthoringErrors.NotFound("no requirement with that id is visible in this project"));
        }

        // The version is looked up under this requirement's id, so a version of a sibling requirement is a 404 here.
        var target = await writer.GetVersionAsync(orgId, command.ProjectId, command.RequirementId, read.Value.VersionId, cancellationToken).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return Result.Failure<RequirementWriteResponse>(target.Error!);
        }

        if (current.Value.Version != command.ExpectedVersion)
        {
            return Result.Failure<RequirementWriteResponse>(AuthoringRequestScope.Stale("requirement", current.Value.Version));
        }

        var author = await AuthoringRequestScope.ActorMembershipAsync(db, orgId, principals, cancellationToken).ConfigureAwait(false);
        var written = await writer.AppendVersionAsync(
            orgId,
            command.ProjectId,
            command.RequirementId,
            command.ExpectedVersion,
            target.Value.Body,
            "manual",
            author,
            read.Value.Reason,
            cancellationToken).ConfigureAwait(false);

        if (!written.IsSuccess)
        {
            return Result.Failure<RequirementWriteResponse>(written.Error!);
        }

        audit.Describe(new AuditObject("requirement", command.RequirementId.ToString("D")));

        return Result.Success(RequirementResponses.Write(written.Value));
    }
}
