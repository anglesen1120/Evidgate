using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Contracts;

namespace Evidgate.Modules.Catalog.Infrastructure.Persistence;

internal sealed class IncidentRevisionFrameworks(ITenantDb db, ITenantScopeAccessor scopes) : IIncidentRevisionFrameworks
{
    public async ValueTask<IReadOnlyDictionary<Guid, string>> ReadAsync(
        Guid projectId, Guid[] revisionIds, CancellationToken cancellationToken)
    {
        var scope = scopes.Require();
        var rows = await db.QueryAsync<Row>("""
            select v.id as "Id", v.framework as "Framework"
              from public.test_revision v
              join public.test_case c on c.org_id=v.org_id and c.id=v.case_id
             where v.org_id=@org and c.project_id=@project and v.id=any(@ids::uuid[])
               and (@scope_project::uuid is null or c.project_id=@scope_project)
            """, new { org = scope.OrgId, project = projectId, ids = revisionIds, scope_project = scope.ProjectId }, cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(row => row.Id, row => row.Framework);
    }

    private sealed record Row(Guid Id, string Framework);
}
