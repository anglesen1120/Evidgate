using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Orchestrator.Contracts;

namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

internal sealed class FrozenIncidentFailuresReader(ITenantDb db, ITenantScopeAccessor scopes, IIncidentRevisionFrameworks catalog)
    : IFrozenIncidentFailures
{
    private static readonly FrozenFailureAttempt[] NoAttempts = [];
    private static readonly IReadOnlyDictionary<Guid, string> NoFrameworks = System.Collections.Frozen.FrozenDictionary<Guid, string>.Empty;

    public async ValueTask<FrozenIncidentFailures?> ReadAsync(Guid runId, int revisionNo, CancellationToken cancellationToken)
    {
        var scope = scopes.Require();
        var header = await db.QuerySingleOrDefaultAsync<Header>("""
            select r.project_id as "ProjectId", r.purpose as "Purpose"
              from public.evidence_revision er
              join public.run r on r.org_id=er.org_id and r.id=er.run_id
             where er.org_id=@org and er.run_id=@run and er.revision_no=@revision
               and (@project::uuid is null or r.project_id=@project)
            """, new { org = scope.OrgId, run = runId, revision = revisionNo, project = scope.ProjectId }, cancellationToken)
            .ConfigureAwait(false);
        if (header is null)
        {
            return null;
        }

        if (header.Purpose == "replay")
        {
            return new(header.ProjectId, NoAttempts, NoFrameworks);
        }

        var attempts = await db.QueryAsync<FrozenFailureAttempt>(FailingRequiredAttempts,
            new { org = scope.OrgId, run = runId, revision = revisionNo }, cancellationToken).ConfigureAwait(false);
        if (attempts.Count == 0)
        {
            return new(header.ProjectId, attempts, NoFrameworks);
        }

        var revisions = attempts.Select(row => row.CaseRevisionId).Distinct().ToArray();
        var frameworks = await catalog.ReadAsync(header.ProjectId, revisions, cancellationToken).ConfigureAwait(false);
        if (frameworks.Count != revisions.Length)
        {
            throw new InvalidOperationException("A frozen failure does not have a test revision in the run's project.");
        }

        return new(header.ProjectId, attempts, frameworks);
    }

    // Same boundary as the evaluator's FrozenEvidenceSql (created_at <= revision created_at, a transaction-start stamp):
    // a late commit with an earlier stamp joins the revision, costing a retry row, never a second incident per key.
    // Classify by the latest admitted attempt first, or a recovered execution would reopen.
    private const string FailingRequiredAttempts = """
        select execution_key as "ExecutionKey", id as "AttemptId", attempt_no as "AttemptNo",
               normalized_status as "Result", case_id as "CaseId", case_revision_id as "CaseRevisionId"
          from (
            select a.*, p.case_revision_id, first_value(a.normalized_status) over
                     (partition by a.execution_key order by a.attempt_no desc) as latest_status
              from public.case_attempt a
              join public.planned_execution p on p.org_id=a.org_id and p.run_id=a.run_id and p.execution_key=a.execution_key
              join public.evidence_revision er on er.org_id=a.org_id and er.run_id=a.run_id and er.revision_no=@revision
             where a.org_id=@org and a.run_id=@run and p.required and a.created_at<=er.created_at
          ) admitted
         where latest_status in ('failed','timed_out','error') and normalized_status in ('failed','timed_out','error')
         order by execution_key, attempt_no
        """;

    private sealed record Header(Guid ProjectId, string Purpose);
}
