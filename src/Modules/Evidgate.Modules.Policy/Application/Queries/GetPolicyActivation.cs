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

/// <summary>Read a project's activation pointer (<c>GET /v1/projects/{p}/policy-activation</c>, F18).</summary>
/// <param name="ProjectId">The project from the route.</param>
internal sealed record GetPolicyActivationQuery(Guid ProjectId) : IQuery<Result<PolicyActivationStateDto>>;

/// <summary>
/// Handler of <see cref="GetPolicyActivationQuery"/>: the scope rule first (a credential narrowed to another
/// project is 403 <c>project_scope_mismatch</c>, exactly as on the append and activation routes), then one statement
/// that reads the live project, its pointer and the revision the pointer names. A project that was never activated,
/// an unknown, soft-deleted or other-tenant project and a pointer naming another project's revision all read no row
/// and answer the same 404, so the read cannot be used to probe for any of them (F18).
/// </summary>
[Permission(Permissions.Policy.Read)]
internal sealed class GetPolicyActivationQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetPolicyActivationQuery, Result<PolicyActivationStateDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<PolicyActivationStateDto>> Handle(GetPolicyActivationQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(GetPolicyActivationQuery));

        if (PolicyScope.Project(query.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<PolicyActivationStateDto>(mismatch);
        }

        var orgId = scopes.Require().OrgId;

        var row = await db.QuerySingleOrDefaultAsync<ActivationStateRow>(
            GetPolicyActivationSql.Activation,
            new { org_id = orgId, project_id = query.ProjectId },
            cancellationToken).ConfigureAwait(false);

        if (row is null)
        {
            return Result.Failure<PolicyActivationStateDto>(PolicyErrors.ActivationNotFound);
        }

        return Result.Success(new PolicyActivationStateDto(
            row.ProjectId,
            row.PolicyRevisionId,
            row.Hash,
            row.Mode,
            row.ActivatedBy,
            row.ActivatedAt));
    }

    private sealed class ActivationStateRow
    {
        public Guid ProjectId { get; set; }

        public Guid PolicyRevisionId { get; set; }

        public string Hash { get; set; } = string.Empty;

        public string Mode { get; set; } = string.Empty;

        public Guid ActivatedBy { get; set; }

        public DateTimeOffset ActivatedAt { get; set; }
    }
}

/// <summary>The statement of <see cref="GetPolicyActivationQuery"/>; it runs in the request's read-only tenant transaction.</summary>
internal static class GetPolicyActivationSql
{
    /// <summary>
    /// The live project inner-joined to its activation pointer and the revision that pointer names, so every
    /// "not there" case reads no row: no live project (unknown, soft-deleted, or hidden by RLS for another tenant),
    /// no pointer (never activated), or a pointer whose revision is not the pointer's own project. The last join
    /// repeats the project predicate because the foreign key is org-scoped, so a pointer to another project's
    /// revision is representable and must never be served (the <c>GetActivePolicySql.ActivePolicy</c> precedent).
    /// </summary>
    internal const string Activation = """
        select pa.project_id, pa.policy_revision_id, pr.hash, pa.mode, pa.activated_by, pa.activated_at
          from public.project p
          join public.policy_activation pa
            on pa.org_id = p.org_id
           and pa.project_id = p.id
          join public.policy_revision pr
            on pr.org_id = pa.org_id
           and pr.project_id = pa.project_id
           and pr.id = pa.policy_revision_id
         where p.id = @project_id
           and p.org_id = @org_id
           and p.status <> 'deleted'
        """;
}
