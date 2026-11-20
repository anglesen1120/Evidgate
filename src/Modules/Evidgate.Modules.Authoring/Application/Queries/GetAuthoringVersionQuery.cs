using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Authoring.Application.Queries;

/// <summary>One stored version with its <c>content</c> (13.1.1 §5 route 23, AC9).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="VersionId">The version id the route names.</param>
internal sealed record GetAuthoringVersionQuery(Guid ProjectId, Guid VersionId) : IQuery<Result<AuthoringVersionDetailDto>>;

/// <summary>
/// Handler of <see cref="GetAuthoringVersionQuery"/>: one statement, no separate project probe — the statement carries
/// the project and the project's liveness, so a version of another project, another tenant or a random id is one 404
/// the tenant policy decides (AC10), and the content of a row that is not this version can never be answered.
/// </summary>
/// <param name="db">The tenant-scoped Dapper seam.</param>
/// <param name="scopes">The open tenant scope.</param>
[Permission(Permissions.Authoring.Read)]
internal sealed class GetAuthoringVersionQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant) : IQueryHandler<GetAuthoringVersionQuery, Result<AuthoringVersionDetailDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<AuthoringVersionDetailDto>> Handle(
        GetAuthoringVersionQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(GetAuthoringVersionQuery));

        ArgumentNullException.ThrowIfNull(query);

        if (AuthoringRules.Project(query.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure<AuthoringVersionDetailDto>(scopeError);
        }

        var orgId = scopes.Require().OrgId;

        var rows = await db.QueryAsync<AuthoringVersionRow>(
            AuthoringVersionReadSql.VersionDetail,
            new { org_id = orgId, project_id = query.ProjectId, version_id = query.VersionId },
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0
            ? Result.Failure<AuthoringVersionDetailDto>(AuthoringErrors.NotFound(
                "no authoring version with that id is visible in this project"))
            : Result.Success(rows[0].ToDetail());
    }
}
