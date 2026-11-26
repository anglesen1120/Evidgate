using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Application.Quarantine;
using Evidgate.Modules.Policy.Domain;
using Mediator;

namespace Evidgate.Modules.Policy.Application.DeployTargets;

/// <summary>A project's deploy targets in display order (<c>GET /v1/projects/{p}/deploy-targets</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
internal sealed record ListDeployTargetsQuery(Guid ProjectId) : IQuery<Result<DeployTargetPage>>;

/// <summary>One deploy target (<c>GET /v1/projects/{p}/deploy-targets/{t}</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="TargetId">The target from the route.</param>
internal sealed record GetDeployTargetQuery(Guid ProjectId, Guid TargetId) : IQuery<Result<DeployTargetDto>>;

/// <summary>Handler of <see cref="ListDeployTargetsQuery"/>; disabled targets are listed too, with their state.</summary>
[Permission(Permissions.Project.Read)]
internal sealed class ListDeployTargetsQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<ListDeployTargetsQuery, Result<DeployTargetPage>>
{
    /// <inheritdoc />
    public async ValueTask<Result<DeployTargetPage>> Handle(ListDeployTargetsQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(ListDeployTargetsQuery));

        if (QuarantineScopeGuard.Project(query.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<DeployTargetPage>(mismatch);
        }

        if (await db.ScalarAsync<int?>(DeployTargetSql.ProjectExists, new { project_id = query.ProjectId }, cancellationToken).ConfigureAwait(false) is null)
        {
            return Result.Failure<DeployTargetPage>(DeployTargetErrors.ProjectNotFound);
        }

        var rows = await db.QueryAsync<DeployTargetDto>(
            DeployTargetSql.List,
            new { org_id = scopes.Require().OrgId, project_id = query.ProjectId, limit = DeployTargetRules.MaxPerProject },
            cancellationToken).ConfigureAwait(false);

        return Result.Success(new DeployTargetPage(rows, null));
    }
}

/// <summary>Handler of <see cref="GetDeployTargetQuery"/>.</summary>
[Permission(Permissions.Project.Read)]
internal sealed class GetDeployTargetQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetDeployTargetQuery, Result<DeployTargetDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<DeployTargetDto>> Handle(GetDeployTargetQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(GetDeployTargetQuery));

        if (QuarantineScopeGuard.Project(query.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<DeployTargetDto>(mismatch);
        }

        var row = await db.QuerySingleOrDefaultAsync<DeployTargetDto>(
            DeployTargetSql.Get,
            new { org_id = scopes.Require().OrgId, project_id = query.ProjectId, id = query.TargetId },
            cancellationToken).ConfigureAwait(false);

        return row is null ? Result.Failure<DeployTargetDto>(DeployTargetErrors.NotFound) : Result.Success(row);
    }
}
