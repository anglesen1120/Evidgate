using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Tenancy.Contracts;

namespace Evidgate.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// The Dapper read-back of one org or project (00 §5.5 C5) that the queries and the
/// commands' responses share, so a command answers exactly the shape the GET returns.
/// </summary>
internal static class TenancyReader
{
    /// <summary>The org row visible to the ambient tenant, or null.</summary>
    internal static Task<OrganizationDto?> OrganizationAsync(ITenantDb db, Guid orgId, CancellationToken cancellationToken) =>
        db.QuerySingleOrDefaultAsync<OrganizationDto>(
            TenancyReadSql.OrganizationById,
            new { org_id = orgId },
            cancellationToken);

    /// <summary>The project row visible to the ambient tenant, or null (another org's id included, AC3).</summary>
    internal static Task<ProjectDto?> ProjectAsync(ITenantDb db, Guid projectId, CancellationToken cancellationToken) =>
        db.QuerySingleOrDefaultAsync<ProjectDto>(
            TenancyReadSql.ProjectById,
            new { project_id = projectId },
            cancellationToken);
}
