using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Contracts;

namespace Evidgate.Modules.Policy.Application.Queries;

/// <summary>
/// Resolves the policy revision a new run pins (07.1.1-T4.2, FR-07-2, ruling T93). This is the in-module
/// reader behind <see cref="IPolicyContracts"/>; the endpoint layer never calls it (the public read surface
/// is the policy-revision routes).
/// <para>
/// It takes the <c>public.project</c> row <c>FOR SHARE</c> first — the lock quarantine creation already
/// takes, and the one activation contends on with <c>FOR NO KEY UPDATE</c> — so a concurrent activation
/// cannot move the pointer between this read and the caller's commit. A missing or soft-deleted project
/// reads no row and answers null, exactly like a project with no activation (owner decision Q1: run
/// creation keeps its defaults, there is no error code for "no policy").
/// </para>
/// <para>
/// One statement, index-led by <c>policy_activation_project_key</c> <c>(org_id, project_id)</c>, joined to
/// the revision it points at; the two body fields run creation needs come out of the stored <c>jsonb</c>.
/// </para>
/// </summary>
internal sealed class GetActivePolicyQuery(ITenantDb db, ITenantScopeAccessor scopes)
{
    /// <summary>The project's active policy, or null when it has none (or the project is gone).</summary>
    /// <param name="projectId">The project the run is created in.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<Result<ActivePolicy?>> GetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(GetActivePolicyQuery));

        var orgId = scopes.Require().OrgId;

        var live = await db.ScalarAsync<int?>(
            GetActivePolicySql.ProjectForShare,
            new { project_id = projectId },
            cancellationToken).ConfigureAwait(false);
        if (live is null)
        {
            return Result.Success<ActivePolicy?>(null);
        }

        var row = await db.QuerySingleOrDefaultAsync<ActivePolicyRow>(
            GetActivePolicySql.ActivePolicy,
            new { org_id = orgId, project_id = projectId },
            cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return Result.Success<ActivePolicy?>(null);
        }

        return Result.Success<ActivePolicy?>(new ActivePolicy(
            row.PolicyRevisionId,
            row.Hash,
            row.CollectionTimeoutMinutes,
            row.LateWindowMinutes));
    }

    private sealed class ActivePolicyRow
    {
        public Guid PolicyRevisionId { get; set; }

        public string Hash { get; set; } = string.Empty;

        public int? CollectionTimeoutMinutes { get; set; }

        public int? LateWindowMinutes { get; set; }
    }
}

/// <summary>The statements of <see cref="GetActivePolicyQuery"/>; both run in the caller's tenant transaction.</summary>
internal static class GetActivePolicySql
{
    /// <summary>
    /// The project row <c>FOR SHARE</c>: the lock quarantine creation takes (07.3.1 D4), held against the
    /// activation's <c>FOR NO KEY UPDATE</c> so the pin cannot be stale. A missing or soft-deleted project
    /// reads no row.
    /// </summary>
    internal const string ProjectForShare = """
        select 1
          from public.project p
         where p.id = @project_id and p.status <> 'deleted'
           for share of p
        """;

    /// <summary>
    /// The project's activation joined to the revision it points at: the pinned id and hash, plus the two
    /// body fields run creation copies. Absent body fields read as SQL null, so the caller keeps its
    /// defaults (owner decision Q1).
    /// </summary>
    internal const string ActivePolicy = """
        select pa.policy_revision_id,
               pr.hash,
               (pr.body ->> 'collection_timeout_minutes')::int as collection_timeout_minutes,
               (pr.body ->> 'late_window_minutes')::int as late_window_minutes
          from public.policy_activation pa
          join public.policy_revision pr
            on pr.org_id = pa.org_id
           and pr.id = pa.policy_revision_id
         where pa.org_id = @org_id
           and pa.project_id = @project_id
        """;
}
