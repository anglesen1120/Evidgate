using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class CaseAttemptSchemaTests
{
    // ------------------------------------------------------------------ (f) index coverage (AC6)

    [Fact]
    public async Task CaseAttempt_ListByRunAndStatus_UsesIndex()
    {
        // Dedicated: it seeds 40 rows, ANALYZEs and reads the plan with sequential scans disabled.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(dedicated);
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        await SeedAttemptsAsync(world, parents, runId, count: 40);

        var plan = await ExplainAsync(
            world,
            world.Org,
            $"select id from public.case_attempt where org_id = '{world.Org}' and run_id = '{runId}' and normalized_status = 'passed'");

        plan.ShouldContain("case_attempt_run_status_idx");
    }

    [Fact]
    public async Task CaseAttempt_HistoryByCaseAndTime_UsesIndex()
    {
        // Dedicated: it seeds 40 rows, ANALYZEs and reads the plan with sequential scans disabled.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(dedicated);
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        await SeedAttemptsAsync(world, parents, runId, count: 40);

        var plan = await ExplainAsync(
            world,
            world.Org,
            $"select id from public.case_attempt where org_id = '{world.Org}' and case_id = '{parents.CaseId}' order by started_at");

        plan.ShouldContain("case_attempt_case_started_idx");
    }

    /// <summary>Seeds <paramref name="count"/> attempts for one run, each a distinct attempt_no.</summary>
    private async Task SeedAttemptsAsync(World world, Parents parents, Guid runId, int count)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        await scope.ExecuteAsync(
            $"""
            insert into public.case_attempt
              (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status, started_at)
            select gen_random_uuid(), '{world.Org}', '{runId}', '{ExecutionKey}', '{parents.CaseId}', g,
                   'passed', 'passed', now() + g * interval '1 second'
              from generate_series(1, {count}) g;
            analyze public.case_attempt;
            """);

        await scope.CommitAsync();
    }

    /// <summary>
    /// The EXPLAIN (FORMAT JSON) plan of one query as the app role, with sequential scans disabled on
    /// this connection only so the planner must choose the covering index (the 07.3.1-T2 precedent).
    /// </summary>
    private async Task<string> ExplainAsync(World world, Guid org, string sql)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);
        await scope.SetAsync("enable_seqscan", "off");

        return await scope.ScalarAsync<string>($"explain (format json) {sql}");
    }

    // ------------------------------------------------------------------ (g) the migration re-run

    [Fact]
    public async Task Migration_ReRun_KeepsAValidAttemptRow()
    {
        // Dedicated: it re-applies the migration and counts the table whole, so it owns the database.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(dedicated);
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(InsertCaseAttemptSql(world.Org, runId, parents.CaseId))).ShouldBe(1);
            await scope.CommitAsync();
        }

        // The file is idempotent: the table, policies, grants and the deferred key already exist, the
        // orphan check counts zero and `validate constraint` is a no-op — the row survives.
        await ReRunMigrationAsync(world.Database);

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.case_attempt")).ShouldBe(1);
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            "select convalidated from pg_catalog.pg_constraint where conrelid = 'public.artifact'::regclass and conname = 'artifact_attempt_fk'"))
            .ShouldBeTrue();
    }
}
