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
/// One lock-free statement: the live project left-joined to its activation pointer and the revision that
/// pointer names, read in a single round trip inside the caller's tenant transaction. It takes no row lock
/// (finding F4): a run that overlaps an activation and pins the previous revision is a legitimate READ
/// COMMITTED ordering, and holding the project row here would let a run create's batch insert, outbox write
/// and audit keep the activation's own <c>FOR NO KEY UPDATE</c> waiting. A missing or soft-deleted project
/// reads no row and answers null, exactly like a project with no activation (owner decision Q1: run creation
/// keeps its defaults, there is no error code for "no policy").
/// </para>
/// <para>
/// The revision is joined to the pointer only when it belongs to the pointer's own project
/// (<c>pr.project_id = pa.project_id</c>): the foreign key is org-scoped, so a pointer to another project's
/// revision is representable and must never be returned (finding F3, defence in depth).
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

        var row = await db.QuerySingleOrDefaultAsync<ActivePolicyRow>(
            GetActivePolicySql.ActivePolicy,
            new { org_id = orgId, project_id = projectId },
            cancellationToken).ConfigureAwait(false);

        // No live project, no pointer, or a pointer whose revision is not this project's: no active policy.
        if (row?.PolicyRevisionId is not { } revisionId || row.Hash is not { } hash)
        {
            return Result.Success<ActivePolicy?>(null);
        }

        return Result.Success<ActivePolicy?>(new ActivePolicy(
            revisionId,
            hash,
            row.CollectionTimeoutMinutes,
            row.LateWindowMinutes));
    }

    private sealed class ActivePolicyRow
    {
        public Guid? PolicyRevisionId { get; set; }

        public string? Hash { get; set; }

        public int? CollectionTimeoutMinutes { get; set; }

        public int? LateWindowMinutes { get; set; }
    }
}

/// <summary>The statement of <see cref="GetActivePolicyQuery"/>; it runs in the caller's tenant transaction.</summary>
internal static class GetActivePolicySql
{
    /// <summary>
    /// The live project left-joined to its activation pointer and the revision that pointer names: the pinned
    /// id and hash, plus the two body fields run creation copies. The revision join also requires the pointer's
    /// project (<c>pr.project_id = pa.project_id</c>), so a pointer to another project's revision (representable
    /// by the org-scoped foreign key) reads no hash and the caller answers null. The two body fields are cast
    /// through <c>numeric</c> first: <c>jsonb</c> keeps the literal's scale, so a body that wrote an integer as
    /// <c>30.0</c> would make a direct <c>::int</c> raise 22P02 and turn every run create after that activation
    /// into a 500 (finding F1); the schema already bounds the values. Absent body fields read as SQL null, so
    /// the caller keeps its defaults (owner decision Q1).
    /// </summary>
    internal const string ActivePolicy = """
        select pa.policy_revision_id,
               pr.hash,
               (pr.body ->> 'collection_timeout_minutes')::numeric::int as collection_timeout_minutes,
               (pr.body ->> 'late_window_minutes')::numeric::int as late_window_minutes
          from public.project p
          left join public.policy_activation pa
            on pa.org_id = p.org_id
           and pa.project_id = p.id
          left join public.policy_revision pr
            on pr.org_id = pa.org_id
           and pr.project_id = pa.project_id
           and pr.id = pa.policy_revision_id
         where p.id = @project_id
           and p.org_id = @org_id
           and p.status <> 'deleted'
        """;
}
