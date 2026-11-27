using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
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
/// Pins one checklist item or one manual test case to the current head of a requirement. A target already linked at the
/// head is a 200 <c>changed:false</c> and writes nothing; a repeat never doubles coverage.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="RequirementId">The requirement from the route.</param>
/// <param name="ChecklistItemId">The item to link; exactly one of the two targets is set.</param>
/// <param name="ManualTestCaseId">The manual case to link.</param>
/// <param name="ExpectedVersionId">The head version the caller read; absent is a 422 and a moved head is a 412.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record LinkRequirementCommand(
    Guid ProjectId,
    Guid RequirementId,
    Guid? ChecklistItemId,
    Guid? ManualTestCaseId,
    Guid? ExpectedVersionId,
    string IdempotencyKey) : ICommand<Result<RequirementLinkResponse>>, IIdempotentCommand;

/// <summary>Handler of <see cref="LinkRequirementCommand"/>.</summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller, for the link's author.</param>
/// <param name="tenant">The request's own tenant context.</param>
/// <param name="writer">The one code path of the requirement tables.</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("authoring.requirement_link.create")]
[Permission(Permissions.Authoring.Write)]
internal sealed class LinkRequirementCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    RequirementWriter writer,
    IAuditTrail audit) : ICommandHandler<LinkRequirementCommand, Result<RequirementLinkResponse>>
{
    /// <inheritdoc />
    public async ValueTask<Result<RequirementLinkResponse>> Handle(
        LinkRequirementCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(LinkRequirementCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<RequirementLinkResponse>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        if (await AuthoringProjectProbe.MissingAsync(db, orgId, command.ProjectId, cancellationToken).ConfigureAwait(false) is { } missingProject)
        {
            return Result.Failure<RequirementLinkResponse>(missingProject);
        }

        var actor = await AuthoringRequestScope.ActorMembershipAsync(db, orgId, principals, cancellationToken).ConfigureAwait(false);
        var created = await writer.CreateLinkAsync(
            orgId,
            new RequirementLinkCreate(
                command.ProjectId,
                command.RequirementId,
                command.ChecklistItemId,
                command.ManualTestCaseId,
                command.ExpectedVersionId,
                actor),
            cancellationToken).ConfigureAwait(false);

        if (!created.IsSuccess)
        {
            return Result.Failure<RequirementLinkResponse>(created.Error!);
        }

        var link = created.Value;
        var response = new RequirementLinkResponse(
            link.LinkId,
            link.RequirementId,
            link.VersionId,
            link.VersionNo,
            link.ChecklistItemId,
            link.ManualTestCaseId,
            link.Changed);

        if (!link.Changed)
        {
            // Nothing was written, so there is nothing to audit; the answer is the documented no-op.
            audit.Suppress();

            return Result.Success(response);
        }

        audit.Describe(RequirementAudit.Linked(command.ProjectId, link));

        return Result.Success(response, StatusCodes.Status201Created);
    }
}

/// <summary>Removes one link of a requirement; an unknown link is a 404, not a silent 204.</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="RequirementId">The requirement from the route.</param>
/// <param name="LinkId">The link from the route.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record UnlinkRequirementCommand(Guid ProjectId, Guid RequirementId, Guid LinkId, string IdempotencyKey)
    : ICommand<Result>, IIdempotentCommand;

/// <summary>Handler of <see cref="UnlinkRequirementCommand"/>.</summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
/// <param name="principals">The caller.</param>
/// <param name="tenant">The request's own tenant context.</param>
/// <param name="writer">The one code path of the requirement tables.</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("authoring.requirement_link.remove")]
[Permission(Permissions.Authoring.Write)]
internal sealed class UnlinkRequirementCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    RequirementWriter writer,
    IAuditTrail audit) : ICommandHandler<UnlinkRequirementCommand, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(UnlinkRequirementCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(UnlinkRequirementCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        if (await AuthoringProjectProbe.MissingAsync(db, orgId, command.ProjectId, cancellationToken).ConfigureAwait(false) is { } missingProject)
        {
            return Result.Failure(missingProject);
        }

        var removed = await writer.RemoveLinkAsync(orgId, command.ProjectId, command.RequirementId, command.LinkId, cancellationToken).ConfigureAwait(false);
        if (!removed.IsSuccess)
        {
            return Result.Failure(removed.Error!);
        }

        audit.Describe(RequirementAudit.Unlinked(command.ProjectId, command.RequirementId, removed.Value));

        return Result.Success(StatusCodes.Status204NoContent);
    }
}
