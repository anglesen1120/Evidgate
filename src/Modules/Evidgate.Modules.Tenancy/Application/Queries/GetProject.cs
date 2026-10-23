using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Tenancy.Contracts;
using Evidgate.Modules.Tenancy.Domain;
using Evidgate.Modules.Tenancy.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Tenancy.Application.Queries;

/// <summary>One project (<c>GET /v1/projects/{p}</c>, 02.1.1 §5).</summary>
/// <param name="ProjectId">The project from the route.</param>
internal sealed record GetProjectQuery(Guid ProjectId) : IQuery<Result<ProjectDto>>;

/// <summary>
/// Handler of <see cref="GetProjectQuery"/>: the scope guard first (a project-scoped
/// token reads only its project, AC4), then one RLS-scoped statement — another org's
/// project id is the same 404 as a missing one (AC3).
/// </summary>
/// <remarks>
/// AC3 also asks for an <c>audit_log</c> row on the denied lookup; that table is
/// 02.7.1-T1's and does not exist yet, so the 404 alone is asserted until it lands
/// (the AC's own wording; feature plan §3).
/// </remarks>
[Permission(Permissions.Project.Read)]
internal sealed class GetProjectQueryHandler(
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetProjectQuery, Result<ProjectDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<ProjectDto>> Handle(
        GetProjectQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Tenancy, nameof(GetProjectQuery));

        if (ScopeGuard.Project(query.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<ProjectDto>(mismatch);
        }

        var project = await TenancyReader.ProjectAsync(db, query.ProjectId, cancellationToken)
            .ConfigureAwait(false);

        return project is null || project.Status == TenancyStatus.Deleted
            ? Result.Failure<ProjectDto>(TenancyErrors.ProjectNotFound)
            : Result.Success(project);
    }
}
