#pragma warning disable CA1861
using Evidgate.Modules.Orchestrator.Application.Queries;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The plans of the attempts page (05.3.3, F52) and of the run detail's status counts: the keyset page must
/// come from <c>case_attempt_run_execution_attempt_key</c> in <c>(execution_key, attempt_no)</c> order, the
/// page's ready-artifact aggregate from <c>artifact_run_slot_ready_idx</c> (0113), and the counts from
/// <c>case_attempt_run_status_idx</c>. Each plan is <c>EXPLAIN (FORMAT JSON)</c> over the handler's own
/// statement — <see cref="ListRunAttemptsSql"/>, <see cref="GetRunQueryHandler.AttemptCountsByStatus"/>,
/// reached through <c>InternalsVisibleTo</c> — bound with the handler's parameters and read as the
/// application role, so the assertion follows the served query and not a copy of it.
/// </summary>
public sealed partial class ListRunAttemptsTests
{
    /// <summary>Attempts seeded per planned execution of the dense run: 300 executions, 1 200 attempts.</summary>
    private const int DenseAttemptsPerExecution = 4;

    /// <summary>Planned executions of the dense run.</summary>
    private const int DenseExecutions = 300;

    /// <summary>Runs of the same org the dense case seeds so the artifact and attempt tables are not tiny.</summary>
    private const int DenseOtherRuns = 8;

    /// <summary>Attempts per seeded other run: 16 000 rows beside the dense run's 1 200.</summary>
    private const int DenseOtherAttemptsPerRun = 2000;

    /// <summary>A well-formed second variant hash, <c>sha256:&lt;64 hex&gt;</c>, for the seeded planned rows.</summary>
    private const string DenseVariantHash = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task ListRunAttempts_PageStatements_UseRunExecutionAttemptKey_NoSeqScanOfTheRun()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var (world, runId) = await StartDenseRunAsync(dedicated.Name);
        await using var lease = world;
        var cursor = await CursorPairAsync(world.Database, world.Org, runId, offset: 100);

        var first = await ExplainAsync(world, ListRunAttemptsSql.FirstPage, ("run_id", runId), ("limit", 51));
        var next = await ExplainAsync(
            world, ListRunAttemptsSql.NextPage,
            ("run_id", runId), ("limit", 51), ("cursor_execution_key", cursor.Key), ("cursor_attempt_no", cursor.AttemptNo));

        foreach (var plan in new[] { first, next })
        {
            var nodes = PlanJson.Nodes(plan);
            PlanJson.UsesIndex(nodes, "case_attempt_run_execution_attempt_key").ShouldBeTrue(plan);
            PlanJson.ScansSequentially(nodes, "case_attempt").ShouldBeFalse(plan);
            // The page CTE is referenced twice (page rows and the aggregate's slot list), so the planner may
            // sort its materialised result; what must not happen is a sort of the run's own attempt rows.
            PlanJson.SortsScanOf(nodes, "case_attempt").ShouldBeFalse(plan);
        }
    }

    [Fact]
    public async Task ListRunAttempts_ReadyArtifactAggregate_UsesReadySlotIndex_NoSeqScanOnArtifact()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var (world, runId) = await StartDenseRunAsync(dedicated.Name);
        await using var lease = world;

        // The aggregate is a subplan of the page statement: the same query serves the count, so its plan is
        // asserted there rather than on a copy of the aggregate's SQL.
        var plan = await ExplainAsync(world, ListRunAttemptsSql.FirstPage, ("run_id", runId), ("limit", 51));
        var nodes = PlanJson.Nodes(plan);

        PlanJson.UsesIndex(nodes, "artifact_run_slot_ready_idx").ShouldBeTrue(plan);
        PlanJson.ScansSequentially(nodes, "artifact").ShouldBeFalse(plan);
    }

    [Fact]
    public async Task GetRun_StatusCounts_UseRunStatusIndex_NoSeqScanOnCaseAttempt()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var (world, runId) = await StartDenseRunAsync(dedicated.Name);
        await using var lease = world;

        var plan = await ExplainAsync(world, GetRunQueryHandler.AttemptCountsByStatus, ("run_id", runId));
        var nodes = PlanJson.Nodes(plan);

        PlanJson.UsesIndex(nodes, "case_attempt_run_status_idx").ShouldBeTrue(plan);
        PlanJson.ScansSequentially(nodes, "case_attempt").ShouldBeFalse(plan);
    }

    /// <summary>
    /// One org on <paramref name="database"/> with a run of <see cref="DenseExecutions"/> planned executions and
    /// <see cref="DenseAttemptsPerExecution"/> attempts each, ready artifacts on a hash-chosen subset of the slots,
    /// beside <see cref="DenseOtherRuns"/> other runs of the same org. The database's statistics are refreshed so
    /// the planner chooses from selectivity, never from <c>enable_seqscan = off</c>.
    /// </summary>
    private async Task<(World World, Guid RunId)> StartDenseRunAsync(string database)
    {
        var dense = await RunCreationTests.StartAsync(_fixture, database: database);
        var world = new World(dense.Api, dense.Client, dense.Database, dense.User, dense.Org, dense.Project);
        var plan = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, database, dense.Org, DenseExecutions);

        await SeedExecutionAttemptsAsync(database, dense.Org, plan.RunId, DenseAttemptsPerExecution);
        await SeedOtherRunsAsync(database, dense, plan.RunId);
        await SeedReadyArtifactsAsync(database, dense.Org, dense.Project, plan.RunId);
        await SeedOtherRunArtifactsAsync(database, dense.Org, dense.Project, plan.RunId);
        await _fixture.ExecuteAsync(
            database,
            "vacuum (analyze) public.artifact, public.case_attempt, public.planned_execution, public.run");

        return (world, plan.RunId);
    }

    /// <summary>
    /// <paramref name="perExecution"/> attempts per planned execution of one run, each carrying the execution's
    /// own case: the row the page orders and the counts statement groups.
    /// </summary>
    private async Task SeedExecutionAttemptsAsync(string database, Guid org, Guid runId, int perExecution)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        (await scope.ExecuteAsync(
            """
            insert into public.case_attempt
              (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status, started_at)
            select gen_random_uuid(), p.org_id, p.run_id, p.execution_key, tr.case_id, g,
                   case g when 1 then 'passed' when 2 then 'failed' when 3 then 'skipped' else 'error' end,
                   case g when 1 then 'passed' when 2 then 'failed' when 3 then 'skipped' else 'error' end,
                   now() + g * interval '1 second'
              from public.planned_execution p
              join public.test_revision tr on tr.org_id = p.org_id and tr.id = p.case_revision_id
              cross join generate_series(1, @per_execution) g
             where p.org_id = @org_id and p.run_id = @run_id
            """,
            ("org_id", org), ("run_id", runId), ("per_execution", perExecution)))
            .ShouldBe(DenseExecutions * perExecution);
        await scope.CommitAsync();
    }

    /// <summary>
    /// <see cref="DenseOtherRuns"/> further runs of the same org with <see cref="DenseOtherAttemptsPerRun"/>
    /// attempts each, one status per attempt cycled from its execution key, so neither table is a single tiny run.
    /// </summary>
    private async Task SeedOtherRunsAsync(string database, RunCreationTests.World dense, Guid ownRunId)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        (await scope.ExecuteAsync(
            """
            with other as (
                insert into public.run
                  (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id,
                   collection_deadline_at, late_window_minutes)
                select gen_random_uuid(), @org_id, @project_id, @manifest_id, @suite_revision_id,
                       @environment_revision_id, now() + interval '2 hours', 1440
                  from generate_series(1, @runs) g
                returning id
            ), planned as (
                insert into public.planned_execution
                  (id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id)
                select gen_random_uuid(), @org_id, o.id,
                       md5(o.id::text || ':' || g::text) || md5(g::text || ':' || o.id::text),
                       @case_revision_id, @variant_hash, 'staging', 'shard-1'
                  from other o cross join generate_series(1, @attempts) g
                returning run_id, execution_key
            )
            insert into public.case_attempt
              (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status, started_at)
            select gen_random_uuid(), @org_id, p.run_id, p.execution_key, a.case_id, 1, 'passed',
                   (array['passed', 'failed', 'skipped', 'timed_out', 'error', 'blocked'])[1 + ((hashtext(p.execution_key) & 2147483647) % 6)],
                   now()
              from planned p
              cross join (select case_id from public.case_attempt where org_id = @org_id and run_id = @own_run_id limit 1) a
            """,
            ("org_id", dense.Org), ("project_id", dense.Project), ("manifest_id", dense.ManifestId),
            ("suite_revision_id", dense.SuiteRevision), ("environment_revision_id", dense.EnvironmentRevision),
            ("runs", DenseOtherRuns), ("attempts", DenseOtherAttemptsPerRun),
            ("case_revision_id", dense.Cases[0]), ("variant_hash", DenseVariantHash), ("own_run_id", ownRunId)))
            .ShouldBe(DenseOtherRuns * DenseOtherAttemptsPerRun);
        await scope.CommitAsync();
    }

    /// <summary>
    /// The run's <c>ready</c> artifacts: none, one or two per slot by a hash of the slot pair, so the count is
    /// neither uniform nor always present, plus one <c>pending_upload</c> row per slot that the index must exclude.
    /// </summary>
    private async Task SeedReadyArtifactsAsync(string database, Guid org, Guid project, Guid runId)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await scope.ExecuteAsync(
            """
            insert into public.artifact
              (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type,
               checksum, sensitivity, scan_state, state)
            select s.id, s.org_id, @project_id, s.run_id, s.execution_key, s.attempt_no, 'trace',
                   'pending/' || s.org_id::text || '/' || s.id::text, 'application/zip', @checksum,
                   'normal', 'pending', 'ready'
              from (
                  select gen_random_uuid() as id, a.org_id, a.run_id, a.execution_key, a.attempt_no
                    from public.case_attempt a
                    cross join lateral generate_series(
                        1,
                        get_byte(decode(substr(md5(a.execution_key || a.attempt_no::text), 1, 2), 'hex'), 0) % 3) g
                   where a.org_id = @org_id and a.run_id = @run_id
              ) s
            """,
            ("org_id", org), ("project_id", project), ("run_id", runId), ("checksum", Checksum));
        await scope.ExecuteAsync(
            """
            insert into public.artifact
              (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type,
               checksum, sensitivity, scan_state, state)
            select s.id, s.org_id, @project_id, s.run_id, s.execution_key, s.attempt_no, 'trace',
                   'pending/' || s.org_id::text || '/' || s.id::text, 'application/zip', @checksum,
                   'normal', 'pending', 'pending_upload'
              from (
                  select gen_random_uuid() as id, a.org_id, a.run_id, a.execution_key, a.attempt_no
                    from public.case_attempt a
                   where a.org_id = @org_id and a.run_id = @run_id
              ) s
            """,
            ("org_id", org), ("project_id", project), ("run_id", runId), ("checksum", Checksum));
        await scope.CommitAsync();
    }

    /// <summary>One artifact per attempt of every other run, half of them <c>ready</c>, so the partial index has a selective target.</summary>
    private async Task SeedOtherRunArtifactsAsync(string database, Guid org, Guid project, Guid ownRunId)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await scope.ExecuteAsync(
            """
            insert into public.artifact
              (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type,
               checksum, sensitivity, scan_state, state)
            select s.id, s.org_id, @project_id, s.run_id, s.execution_key, s.attempt_no, 'trace',
                   'pending/' || s.org_id::text || '/' || s.id::text, 'application/zip', @checksum,
                   'normal', 'pending',
                   case when get_byte(decode(substr(md5(s.execution_key), 1, 2), 'hex'), 0) % 2 = 0
                        then 'ready' else 'pending_upload' end
              from (
                  select gen_random_uuid() as id, a.org_id, a.run_id, a.execution_key, a.attempt_no
                    from public.case_attempt a
                   where a.org_id = @org_id and a.run_id <> @own_run_id
              ) s
            """,
            ("org_id", org), ("project_id", project), ("own_run_id", ownRunId), ("checksum", Checksum));
        await scope.CommitAsync();
    }

    /// <summary>The plan of one handler statement bound with its parameters, read as the application role.</summary>
    private async Task<string> ExplainAsync(World world, string sql, params (string Name, object Value)[] parameters)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        return await scope.ScalarAsync<string>("explain (format json) " + sql, parameters);
    }

    /// <summary>The <c>(execution_key, attempt_no)</c> pair at <paramref name="offset"/> in the run's page order.</summary>
    private async Task<(string Key, int AttemptNo)> CursorPairAsync(string database, Guid org, Guid runId, int offset)
    {
        const string tail = """
             from public.case_attempt
             where org_id = @org_id and run_id = @run_id
             order by execution_key, attempt_no
            offset @offset limit 1
            """;
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));

        return (await scope.ScalarAsync<string>(
                "select execution_key" + tail, ("org_id", org), ("run_id", runId), ("offset", offset)),
            await scope.ScalarAsync<int>(
                "select attempt_no" + tail, ("org_id", org), ("run_id", runId), ("offset", offset)));
    }
}
