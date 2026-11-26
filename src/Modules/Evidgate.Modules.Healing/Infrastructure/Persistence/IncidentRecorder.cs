using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Orchestrator.Contracts;

namespace Evidgate.Modules.Healing.Infrastructure.Persistence;

internal sealed class IncidentRecorder(ITenantDb db, ITenantScopeAccessor scopes) : IIncidentRecorder
{
    // `distinct on` keeps the first failing attempt of an execution; the unique key makes redelivery a no-op, and the
    // stable key order makes concurrent deliveries take their row locks in the same order. Every array is a text[]
    // parameter cast in SQL: the tenant Dapper path refuses a client array, and ids and numbers need no other type.
    private const string InsertIncidents = """
        insert into public.incident (id, org_id, project_id, run_id, attempt_id, case_id, case_revision_id, execution_key, engine)
        select distinct on (x.key) x.id, @org, @project, @run, x.attempt, x.case_id, x.revision, x.key, x.engine
          from unnest(@ids::text[]::uuid[], @keys::text[], @attempts::text[]::uuid[], @cases::text[]::uuid[],
                      @revisions::text[]::uuid[], @engines::text[], @numbers::text[]::int[])
               as x(id, key, attempt, case_id, revision, engine, num)
         order by x.key, x.num
        on conflict (org_id, run_id, execution_key) do nothing
        returning id
        """;

    // A separate statement: when another delivery won the incident insert, this one still sees its committed row.
    private const string AttachRetries = """
        insert into public.incident_attempt (id, org_id, incident_id, attempt_id, attempt_no, result)
        select x.id, i.org_id, i.id, x.attempt, x.num, x.result
          from unnest(@ids::text[]::uuid[], @keys::text[], @attempts::text[]::uuid[], @numbers::text[]::int[], @results::text[])
               as x(id, key, attempt, num, result)
          join public.incident i on i.org_id = @org and i.run_id = @run and i.execution_key = x.key
         where i.attempt_id <> x.attempt
         order by x.key, x.num
        on conflict (org_id, incident_id, attempt_id) do nothing
        returning id
        """;

    public async Task<IncidentWrites> InsertAsync(Guid runId, FrozenIncidentFailures failures, CancellationToken cancellationToken)
    {
        var rows = failures.Attempts;
        var parameters = new
        {
            org = scopes.Require().OrgId,
            project = failures.ProjectId,
            run = runId,
            ids = Text(rows.Select(_ => Guid.CreateVersion7().ToString("D"))),
            keys = Text(rows.Select(row => row.ExecutionKey)),
            attempts = Text(rows.Select(row => row.AttemptId.ToString("D"))),
            cases = Text(rows.Select(row => row.CaseId.ToString("D"))),
            revisions = Text(rows.Select(row => row.CaseRevisionId.ToString("D"))),
            engines = Text(rows.Select(row => failures.Frameworks[row.CaseRevisionId])),
            numbers = Text(rows.Select(row => row.AttemptNo.ToString(CultureInfo.InvariantCulture))),
            results = Text(rows.Select(row => row.Result)),
        };

        var created = await db.QueryAsync<Guid>(InsertIncidents, parameters, cancellationToken).ConfigureAwait(false);
        var attached = await db.QueryAsync<Guid>(AttachRetries, parameters, cancellationToken).ConfigureAwait(false);
        return new(created.ToList(), attached.ToList());
    }

    private static TextArrayParameter Text(IEnumerable<string> values) => new(values.ToArray());
}
