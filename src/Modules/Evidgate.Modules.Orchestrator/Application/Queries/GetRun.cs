using System.Text.Json;
using Dapper;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Application.Manifests;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Orchestrator.Application.Queries;

/// <summary>Read one run's state machine position (<c>GET /v1/runs/{r}</c>, 05.3.1 §5, AC7).</summary>
/// <param name="RunId">The run from the route (<c>{r}</c>).</param>
internal sealed record GetRunQuery(Guid RunId) : IQuery<Result<RunStateDto>>;

/// <summary>
/// Handler of <see cref="GetRunQuery"/>: one Dapper read over the ambient tenant scope (00 §5.5 C1/C3/C5), so RLS
/// scopes the row and another tenant's run is the same <c>404 not_found</c> as an unknown id. The body reports the
/// run's state machine position and the identity it records (05.2.2-T1: the <c>ref</c> block and
/// <c>unverified_ref</c>; <c>advisory_only</c> is 05.2.2-T3.2's). The state transitions the write trigger recorded are
/// selected by the same statement as the run row, so the state and the history reported beside it share one snapshot;
/// the releases the run is bound to are one more correlated subquery of it. The route names the run
/// by id alone, so a project-scoped credential asking for a sibling project's run of the same org is that same
/// <c>404 not_found</c>: <see cref="OrchestratorScope.Project"/>'s scope mismatch is mapped to it below, never to
/// <c>403 project_scope_mismatch</c> — the same shape as the module's other read route
/// (<c>GetEnvironmentRevisionQueryHandler</c>, EnvironmentRevisions.cs:246). The only 403 here is the permission
/// stage's: <c>run:read</c> is outside the service-role ceiling (<c>PermissionEvaluator.Service</c>), so a
/// <c>ci_producer</c>/service credential can never hold it and is refused <c>403 forbidden</c>. Reads never write.
/// </summary>
[Permission(Permissions.Run.Read)]
internal sealed class GetRunQueryHandler(
    ITenantDb db,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetRunQuery, Result<RunStateDto>>
{
    /// <summary>
    /// The one statement of the read: the run's own columns by id, including the identity block 05.2.2-T1 adds
    /// (<c>ref_kind</c>, <c>ref_name</c>, <c>pr_number</c>, <c>head_sha</c>, <c>base_sha</c>, <c>trigger_event</c>,
    /// <c>is_fork</c>, <c>unverified_ref</c>), and the release inputs F44 pins to the same body: the manifest's
    /// <c>manifest_hash</c> through <c>run.manifest_id</c>, the run's own <c>suite_revision_id</c> and
    /// <c>policy_revision_id</c>, the environment revision it locked (<c>environment_revision</c> +
    /// <c>environment</c> through <c>run.environment_revision_id</c>), and the one <c>producer_slot</c> aggregate the
    /// run registered — its count and its ids in <c>shard_index</c> order, both in the same statement, so the detail
    /// read is never N+1 and its producer order never depends on the heap order the slots happen to have. The same
    /// statement counts the run's late events. Only a canceled run can hold one (admission flags a write as
    /// <c>after_cancel</c> solely against a canceled run, and that state is terminal), so every other state skips the
    /// heap scan the flag column would otherwise cost on each read. The state history is selected here too, as two
    /// correlated subqueries of this one statement: a history read by a statement of its own could see a cancel commit
    /// after the run row was read, and serve the old state beside a history that ends canceled. The release bindings are
    /// selected the same way, bounded by <see cref="ReleaseSql.MaxBoundReleases"/>.
    /// <para>
    /// <c>org_id</c> is left to RLS (the row is readable only inside the caller's tenant scope, and the policy is
    /// <c>FORCE</c>d, 0031), so an unknown id and another tenant's id take the same branch. Every join is on the
    /// composite tenant key its foreign key already uses (0031), so a join can neither widen the row set nor read
    /// another tenant's parent. The <c>project</c> join of the environment-revision read is deliberately absent: the
    /// run's other readers (<c>RunPlanReader</c>, <c>RunEventScopeReader</c>) resolve a run without filtering a
    /// deleted project either.
    /// </para>
    /// </summary>
    internal const string RunStateById = $$"""
        select r.id as run_id, r.project_id, r.state, r.current_evidence_revision_id, r.version, r.purpose,
               r.ref_kind, r.ref_name, r.pr_number, r.head_sha, r.base_sha, r.trigger_event, r.is_fork, r.unverified_ref,
               m.manifest_hash,
               r.suite_revision_id, r.policy_revision_id,
               er.id as environment_revision_id, er.environment_id, e.environment_key, er.revision_no, er.revision_hash,
               coalesce(slots.shard_count, 0) as shard_count,
               coalesce(slots.declared_producers, '{}') as declared_producers,
               case when r.state = 'canceled'
                    then (select count(*)::integer
                            from public.event ev
                           where ev.org_id = r.org_id and ev.run_id = r.id and ev.after_cancel)
                    else 0 end as after_cancel_events,
               {{RunStateHistorySql.HistoryColumns}},
               {{ReleaseSql.BoundReleasesColumn}}
          from public.run r
          join public.manifest m on m.org_id = r.org_id and m.id = r.manifest_id
          join public.environment_revision er on er.org_id = r.org_id and er.id = r.environment_revision_id
          join public.environment e on e.org_id = er.org_id and e.id = er.environment_id
          left join lateral (
              select count(*)::integer as shard_count,
                     array_agg(p.producer_id order by p.shard_index) as declared_producers
                from public.producer_slot p
               where p.org_id = r.org_id and p.run_id = r.id
          ) slots on true
         where r.id = @run_id
        """;

    /// <inheritdoc />
    public async ValueTask<Result<RunStateDto>> Handle(GetRunQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Orchestrator, nameof(GetRunQuery));

        var row = await db.QuerySingleOrDefaultAsync<RunStateRow>(
                RunStateById,
                new { run_id = query.RunId, bound_release_limit = ReleaseSql.MaxBoundReleases },
                cancellationToken)
            .ConfigureAwait(false);
        if (row is null || OrchestratorScope.Project(row.ProjectId, principals, tenant) is not null)
        {
            return Result.Failure<RunStateDto>(RunNotFound);
        }

        var releases = BoundReleasesFromRow(row.BoundReleases);
        var stateHistory = RunStateHistoryReader.FromRow(row.StateHistoryKnown, row.StateHistory);

        return Result.Success(new RunStateDto(
            row.RunId,
            row.State,
            row.CurrentEvidenceRevisionId,
            row.Version,
            row.Purpose,
            new RunRef(
                row.RefKind,
                row.RefName,
                row.PrNumber,
                row.HeadSha,
                row.BaseSha,
                row.TriggerEvent,
                row.IsFork),
            row.UnverifiedRef,
            row.ManifestHash,
            row.SuiteRevisionId,
            row.PolicyRevisionId,
            new RunEnvironmentRef(
                row.EnvironmentRevisionId,
                row.EnvironmentId,
                row.EnvironmentKey,
                row.RevisionNo,
                row.RevisionHash),
            row.ShardCount,
            [.. row.DeclaredProducers],
            releases,
            row.AfterCancelEvents,
            stateHistory));
    }

    /// <summary>The bindings column of <see cref="RunStateById"/>: one item per stored binding, already ordered and bounded.</summary>
    private static List<RunReleaseRef> BoundReleasesFromRow(string bound)
    {
        var releases = new List<RunReleaseRef>();
        using var document = JsonDocument.Parse(bound);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            releases.Add(new RunReleaseRef(
                item.GetProperty("release_id").GetGuid(),
                item.GetProperty("release_name").GetString()!,
                item.GetProperty("component_key").GetString()!));
        }

        return releases;
    }

    /// <summary>
    /// 404 <c>not_found</c>: no run with that id is visible to this tenant — the same code, message and body for an
    /// unknown id, for another tenant's run (the RLS branch above) and for a sibling project's run of the same org
    /// (the <see cref="OrchestratorScope.Project"/> mismatch, mapped here), so the answer is no existence oracle
    /// (00 §5.4).
    /// </summary>
    private static Error RunNotFound => Error.NotFound("no run with that id is visible to this tenant");

    /// <summary>One row as <see cref="RunStateById"/> returns it (snake_case columns mapped by Dapper).</summary>
    private sealed class RunStateRow
    {
        public Guid RunId { get; set; }

        public Guid ProjectId { get; set; }

        public string State { get; set; } = string.Empty;

        public Guid? CurrentEvidenceRevisionId { get; set; }

        public int Version { get; set; }

        public string Purpose { get; set; } = string.Empty;

        public string? RefKind { get; set; }

        public string? RefName { get; set; }

        public int? PrNumber { get; set; }

        public string? HeadSha { get; set; }

        public string? BaseSha { get; set; }

        public string? TriggerEvent { get; set; }

        public bool IsFork { get; set; }

        public bool UnverifiedRef { get; set; }

        public string ManifestHash { get; set; } = string.Empty;

        public Guid SuiteRevisionId { get; set; }

        public Guid? PolicyRevisionId { get; set; }

        public Guid EnvironmentRevisionId { get; set; }

        public Guid EnvironmentId { get; set; }

        public string EnvironmentKey { get; set; } = string.Empty;

        public int RevisionNo { get; set; }

        public string RevisionHash { get; set; } = string.Empty;

        public int ShardCount { get; set; }

        public string[] DeclaredProducers { get; set; } = [];

        public int AfterCancelEvents { get; set; }

        /// <summary>The marker column of <see cref="RunStateHistorySql.HistoryColumns"/>: the run's history is knowable.</summary>
        public bool StateHistoryKnown { get; set; }

        /// <summary>The transitions column of <see cref="RunStateHistorySql.HistoryColumns"/>, oldest first.</summary>
        public string StateHistory { get; set; } = "[]";

        /// <summary>The bindings column of <see cref="ReleaseSql.BoundReleasesColumn"/>.</summary>
        public string BoundReleases { get; set; } = "[]";
    }
}
