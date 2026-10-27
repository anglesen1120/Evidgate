using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Contracts;
using Mediator;

namespace Evidgate.Modules.Policy.Application.Queries;

/// <summary>Read a project's newest policy revision (<c>GET /v1/projects/{p}/policies/head</c>, F18).</summary>
/// <param name="ProjectId">The project from the route.</param>
internal sealed record GetPolicyHeadQuery(Guid ProjectId) : IQuery<Result<PolicyRevisionSummaryDto>>;

/// <summary>
/// Handler of <see cref="GetPolicyHeadQuery"/>: the scope rule first (a credential narrowed to another project is
/// 403 <c>project_scope_mismatch</c>), then one statement — the live project's revision with the highest
/// <c>revision_no</c>, whatever its state, because the head is the append-only sequence's last row and not the
/// active one (activation is <c>GET …/policy-activation</c>). A project with no revision, and an unknown,
/// soft-deleted or other-tenant project, read no row and answer the same 404. The endpoint sets the revision id as
/// the <c>ETag</c>, the same value <c>GET /v1/policies/{id}</c> issues for the row.
/// </summary>
[Permission(Permissions.Policy.Read)]
internal sealed class GetPolicyHeadQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetPolicyHeadQuery, Result<PolicyRevisionSummaryDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<PolicyRevisionSummaryDto>> Handle(GetPolicyHeadQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(GetPolicyHeadQuery));

        if (PolicyScope.Project(query.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<PolicyRevisionSummaryDto>(mismatch);
        }

        var orgId = scopes.Require().OrgId;

        var row = await db.QuerySingleOrDefaultAsync<PolicyRevisionSummaryDto>(
            GetPolicyHeadSql.Head,
            new { org_id = orgId, project_id = query.ProjectId },
            cancellationToken).ConfigureAwait(false);

        return row is null
            ? Result.Failure<PolicyRevisionSummaryDto>(PolicyErrors.RevisionHeadNotFound)
            : Result.Success(row);
    }
}

/// <summary>The statement of <see cref="GetPolicyHeadQuery"/>; it runs in the request's read-only tenant transaction.</summary>
internal static class GetPolicyHeadSql
{
    /// <summary>
    /// The live project joined to its newest revision. RLS scopes both to the request's tenant, and the project join
    /// hides a soft-deleted project, so an unreadable project and a project without revisions are the same empty
    /// result. The unique btree on <c>(org_id, project_id, revision_no)</c> serves the backward scan (07.1.1-T1
    /// deviation), and the pair is unique, so the order needs no tie-break.
    /// </summary>
    internal const string Head = """
        select r.id, r.revision_no, r.hash, r.state, r.created_by, r.created_at, r.version
          from public.project p
          join public.policy_revision r
            on r.org_id = p.org_id
           and r.project_id = p.id
         where p.id = @project_id
           and p.org_id = @org_id
           and p.status <> 'deleted'
         order by r.revision_no desc
         limit 1
        """;
}
