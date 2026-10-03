using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Tenancy.Contracts;

namespace Evidgate.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// <see cref="ITenantLimits"/> over the ambient tenant scope (02.1.1-T3.1): one Dapper
/// statement per read, RLS-scoped, so a project of another org reads as null.
/// </summary>
internal sealed class TenantLimits(ITenantDb db, ITenantScopeAccessor scopes) : ITenantLimits
{
    /// <inheritdoc />
    public async ValueTask<OrganizationLimits?> ForOrganizationAsync(CancellationToken cancellationToken) =>
        await db.QuerySingleOrDefaultAsync<OrganizationLimits>(
            TenancyReadSql.OrganizationLimits,
            new { org_id = scopes.Require().OrgId },
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<ProjectLimits?> ForProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        await db.QuerySingleOrDefaultAsync<ProjectLimits>(
            TenancyReadSql.ProjectLimits,
            new { project_id = projectId },
            cancellationToken).ConfigureAwait(false);
}
