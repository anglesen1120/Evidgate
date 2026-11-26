#pragma warning disable CA1861
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Evidgate.Modules.Policy.Infrastructure.Persistence;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The decision-count reads must stay index range scans: the run-id chunks follow <c>run_project_created_idx</c>
/// with no Sort and no table scan, and the per-decision aggregate probes <c>evaluation_live_key</c> once per run id
/// instead of scanning the evaluations. Checked by plan shape on a database with realistic statistics.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RunDecisionCountsIndexPlanTests
{
    private const string PolicyHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string PolicyBody = "{}";

    private readonly PostgresTenancyFixture _fixture;

    public RunDecisionCountsIndexPlanTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task DecisionCounts_ChunkAndAggregateStatements_UseIndexesWithoutScansOrSorts()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await RunCreationTests.StartAsync(_fixture, database: database.Name);
        await SeedAsync(world);
        var since = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var midRange = since.AddSeconds(20_000);

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("org", world.Org.ToString());
            await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        var first = await PlanAsync(connection, transaction, RunDecisionCountsSql.FirstChunk, world, since, null, null);
        var next = await PlanAsync(connection, transaction, RunDecisionCountsSql.NextChunk, world, since, midRange, Guid.CreateVersion7());
        var windowed = await PlanAsync(connection, transaction, RunDecisionCountsSql.FirstChunk, world, midRange, null, null);

        foreach (var plan in new[] { first, next, windowed })
        {
            var (nodeTypes, indexNames) = ExplainedNodes(plan);
            indexNames.ShouldContain("run_project_created_idx", plan);
            nodeTypes.ShouldNotContain("Sort", plan);
            nodeTypes.ShouldNotContain("Seq Scan", plan);
        }

        // A chunk of 200 ids probes each run's newest live row through the partial unique index. The grouped result
        // may use a Sort or a hash, so only the table scan is ruled out here.
        var runIds = await ChunkIdsAsync(connection, transaction, world);
        await using var aggregate = new NpgsqlCommand("explain (format json) " + RunDecisionSql.LatestLiveCounts, connection, transaction);
        aggregate.Parameters.AddWithValue("org_id", world.Org);
        aggregate.Parameters.AddWithValue("ids", runIds);
        var aggregatePlan = (string)(await aggregate.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        var (aggregateNodes, aggregateIndexes) = ExplainedNodes(aggregatePlan);

        aggregateIndexes.ShouldContain("evaluation_live_key", aggregatePlan);
        aggregateNodes.ShouldNotContain("Seq Scan", aggregatePlan);
    }

    private async Task SeedAsync(RunCreationTests.World world)
    {
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.run
              (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id,
               created_at, collection_deadline_at, late_window_minutes, ref_kind, ref_name, pr_number)
            select gen_random_uuid(), '{world.Org:D}', '{world.Project:D}', '{world.ManifestId:D}',
                   '{world.SuiteRevision:D}', '{world.EnvironmentRevision:D}',
                   timestamptz '2026-01-01T00:00:00Z' + g * interval '1 second',
                   now() + interval '1 hour' + g * interval '1 second', 1440, 'branch', 'ref-' || g, null
              from generate_series(1, 40000) g;
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by)
            values (gen_random_uuid(), '{world.Org:D}', '{world.Project:D}', 90, '{PolicyHash}', '{PolicyBody}', '{world.User:D}');
            -- Half the runs hold a live verdict, a quarter a second live revision, and one in eight a shadow row.
            insert into public.evaluation
              (id, org_id, run_id, evidence_revision, policy_revision_id, policy_hash, manifest_hash, completeness,
               missing, outcome, decision, reasons, evaluator_version, mode, exceptions_hash, exceptions_as_of)
            select gen_random_uuid(), x.org_id, x.id, x.rev, x.policy_id, '{PolicyHash}', 'sha256:{new string('c', 64)}',
                   case when x.verdict = 'PASS' then 'complete' else 'incomplete' end,
                   array[]::text[],
                   case when x.verdict = 'PASS' then 'all_passed' else 'has_failures' end,
                   x.verdict, array[]::text[], 'v1', case when x.rev = 3 then 'shadow' else 'live' end,
                   'sha256:{new string('d', 64)}', now()
              from (select r.org_id, r.id, rev.n as rev, p.id as policy_id,
                           (array['PASS', 'FAIL', 'INCONCLUSIVE'])[1 + ((r.n + rev.n) % 3)::int] as verdict
                      from (select run.org_id, run.id, run.project_id,
                                   row_number() over (order by run.created_at) as n
                              from public.run run where run.project_id = '{world.Project:D}') r
                      cross join public.policy_revision p
                      cross join generate_series(1, 3) as rev(n)
                     where p.project_id = r.project_id and p.revision_no = 90
                       and (rev.n = 1 and r.n % 2 = 0 or rev.n = 2 and r.n % 4 = 0 or rev.n = 3 and r.n % 8 = 0)) x;
            analyze public.run;
            analyze public.evaluation;
            """);
    }

    /// <summary>Explains one chunk statement with its real parameters and a limit of one chunk plus the lookahead row.</summary>
    private static async Task<string> PlanAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        RunCreationTests.World world,
        DateTimeOffset since,
        DateTimeOffset? cursorCreatedAt,
        Guid? cursorId)
    {
        await using var command = new NpgsqlCommand("explain (format json) " + sql, connection, transaction);
        command.Parameters.AddWithValue("org_id", world.Org);
        command.Parameters.AddWithValue("project_id", world.Project);
        command.Parameters.AddWithValue("since", since);
        command.Parameters.AddWithValue("limit", 201);
        if (cursorCreatedAt is not null && cursorId is not null)
        {
            command.Parameters.AddWithValue("cursor_created_at", cursorCreatedAt.Value);
            command.Parameters.AddWithValue("cursor_id", cursorId.Value);
        }

        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<Guid[]> ChunkIdsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RunCreationTests.World world)
    {
        await using var command = new NpgsqlCommand(
            "select array_agg(id) from (select id from public.run where project_id = @project order by created_at desc, id limit 200) r",
            connection,
            transaction);
        command.Parameters.AddWithValue("project", world.Project);

        return (Guid[])(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static (List<string> NodeTypes, List<string> IndexNames) ExplainedNodes(string plan) =>
        ListRunsIndexPlanTests.PlanNodes(plan);
}
