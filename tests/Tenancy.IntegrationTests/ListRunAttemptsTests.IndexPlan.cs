#pragma warning disable CA1861
using System.Net;
using Evidgate.Modules.Orchestrator.Application.Queries;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

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
            PlanJson.SortsScanOf(plan, "case_attempt").ShouldBeFalse(plan);
        }
    }

    [Fact]
    public async Task ListRunAttempts_ReadyArtifactAggregate_UsesReadySlotIndex_NoSeqScanOnArtifact()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var (world, runId) = await StartDenseRunAsync(dedicated.Name);
        await using var lease = world;
        var cursor = await CursorPairAsync(world.Database, world.Org, runId, offset: 100);

        // The aggregate is a subplan of both page statements and the same query serves the count, so the plan is
        // asserted on the statements themselves rather than on a copy of the aggregate's SQL — both page shapes,
        // because a cursor predicate can move which side of the semi-join drives it.
        var first = await ExplainAsync(world, ListRunAttemptsSql.FirstPage, ("run_id", runId), ("limit", 51));
        var next = await ExplainAsync(
            world, ListRunAttemptsSql.NextPage,
            ("run_id", runId), ("limit", 51), ("cursor_execution_key", cursor.Key), ("cursor_attempt_no", cursor.AttemptNo));

        foreach (var plan in new[] { first, next })
        {
            var nodes = PlanJson.Nodes(plan);
            PlanJson.UsesIndex(nodes, "artifact_run_slot_ready_idx").ShouldBeTrue(plan);
            PlanJson.ScansSequentially(nodes, "artifact").ShouldBeFalse(plan);
        }
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

    /// <summary>Execution keys of the own run that the colliding sibling run repeats.</summary>
    private const int CollidingKeys = 20;

    /// <summary>Ready artifacts the colliding sibling run holds on each of the own run's slots.</summary>
    private const int CollidingReadyPerSlot = 3;

    /// <summary>
    /// A sibling run of the same org repeats the own run's first execution keys with three ready artifacts on
    /// every one of their slots: the aggregate counts by run as well as by slot, so the sibling's rows on the same
    /// <c>(execution_key, attempt_no)</c> pairs must leave the page's <c>artifact_count</c> at the own run's value —
    /// zero where the own run has no ready artifact, never the sibling's three.
    /// </summary>
    [Fact]
    public async Task ListRunAttempts_ReadyArtifactCounts_IgnoreAnotherRunsRowsOnTheSameSlots()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        var (world, runId) = await StartDenseRunAsync(dedicated.Name);
        await using var lease = world;

        var collidedKeys = await SeedCollidingRunAsync(
            world.Database, world.Org, world.Project, runId, keys: CollidingKeys, perSlot: CollidingReadyPerSlot);
        collidedKeys.Length.ShouldBe(CollidingKeys, "the seed must return the keys it copied");
        // A positive control first: the sibling's rows are committed and visible on every one of the own run's
        // slots on those keys, so a zero below would mean a missing arrangement rather than the ownership filter
        // the case is about. Slots come from the own run, never from the sibling's rows, so a slot the seed
        // failed to copy counts as zero instead of disappearing from the check.
        var siblingSlots = await SiblingReadyPerSlotAsync(world.Database, world.Org, runId, collidedKeys);
        siblingSlots.Length.ShouldBe(
            CollidingKeys * DenseAttemptsPerExecution,
            "the sibling must repeat every attempt slot of the copied keys");
        siblingSlots.ShouldAllBe(
            count => count == CollidingReadyPerSlot,
            "every collided slot must hold the sibling run's ready artifacts before the page is read");
        var own = await ReadyCountsAsync(world.Database, world.Org, runId);

        var answer = await GetAsync(world, runId, query: "?limit=100");
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        var collided = Items(answer).EnumerateArray().Select(Row)
            .Where(row => collidedKeys.Contains(row.ExecutionKey))
            .ToList();
        collided.ShouldNotBeEmpty("the page must carry the slots the sibling run also holds");
        foreach (var row in collided)
        {
            row.ArtifactCount.ShouldBe(
                own.GetValueOrDefault((row.ExecutionKey, row.AttemptNo)),
                $"{row.ExecutionKey}/{row.AttemptNo} must count only its own run's ready artifacts");
        }

        collided.Any(row => !own.ContainsKey((row.ExecutionKey, row.AttemptNo))).ShouldBeTrue(
            "a slot with no ready artifact of its own must still read 0 beside the sibling's three");
    }

    /// <summary>
    /// One org on <paramref name="database"/> with a run of <see cref="DenseExecutions"/> planned executions and
    /// <see cref="DenseAttemptsPerExecution"/> attempts each, ready artifacts on a hash-chosen subset of the slots,
    /// beside <see cref="DenseOtherRuns"/> other runs of the same org. The database's statistics are refreshed so
    /// the planner chooses from selectivity, never from <c>enable_seqscan = off</c>.
    /// <para>
    /// No world-level environment is created: <see cref="PlannedExecutionSeeder.SeedPlanAsync"/> mints the
    /// environment, its revision and the run's suite in its own transaction, so one from the world would take the
    /// project's environment key from the seeded one.
    /// </para>
    /// </summary>
    private async Task<(World World, Guid RunId)> StartDenseRunAsync(string database)
    {
        var world = await StartAsync(database);

        try
        {
            var plan = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, database, world.Org, DenseExecutions);

            await SeedExecutionAttemptsAsync(database, world.Org, plan.RunId, DenseAttemptsPerExecution);
            await SeedOtherRunsAsync(database, world.Org, plan.RunId);
            await SeedReadyArtifactsAsync(database, world.Org, world.Project, plan.RunId);
            await SeedOtherRunArtifactsAsync(database, world.Org, world.Project, plan.RunId);
            await _fixture.ExecuteAsync(
                database,
                "vacuum (analyze) public.artifact, public.case_attempt, public.planned_execution, public.run");

            return (world, plan.RunId);
        }
        catch
        {
            // A world that never returns owns nothing: a failed arrange must not leave the host's connections
            // behind for the assembly's database cleanup.
            await world.DisposeAsync();
            throw;
        }
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
    /// Each sibling copies the own run's manifest, suite and environment revisions, so no world-level catalogue is
    /// needed here; its planned rows reuse one of the own run's case revisions.
    /// </summary>
    private async Task SeedOtherRunsAsync(string database, Guid org, Guid ownRunId)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        (await scope.ExecuteAsync(
            """
            with own as (
                select r.org_id, r.project_id, r.manifest_id, r.suite_revision_id, r.environment_revision_id
                  from public.run r
                 where r.org_id = @org_id and r.id = @own_run_id
            ), other as (
                insert into public.run
                  (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id,
                   collection_deadline_at, late_window_minutes)
                select gen_random_uuid(), o.org_id, o.project_id, o.manifest_id, o.suite_revision_id,
                       o.environment_revision_id, now() + interval '2 hours', 1440
                  from own o cross join generate_series(1, @runs) g
                returning id
            ), planned as (
                insert into public.planned_execution
                  (id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id)
                select gen_random_uuid(), @org_id, o.id,
                       md5(o.id::text || ':' || g::text) || md5(g::text || ':' || o.id::text),
                       (select p.case_revision_id from public.planned_execution p
                         where p.org_id = @org_id and p.run_id = @own_run_id limit 1),
                       @variant_hash, 'staging', 'shard-1'
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
            ("org_id", org), ("own_run_id", ownRunId),
            ("runs", DenseOtherRuns), ("attempts", DenseOtherAttemptsPerRun),
            ("variant_hash", DenseVariantHash)))
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

    /// <summary>
    /// <see cref="SeedCollidingRunAsync"/>'s statement: one sibling run of the own run's parents, its planned rows
    /// and attempts copied from the own run's first <c>@keys</c> execution keys, and <c>@per_slot</c> ready
    /// artifacts on every copied slot.
    /// </summary>
    private const string CollidingRunSql = """
        with own as (
            select r.org_id, r.project_id, r.manifest_id, r.suite_revision_id, r.environment_revision_id
              from public.run r
             where r.org_id = @org_id and r.id = @own_run_id
        ), copied as (
            select distinct a.execution_key
              from public.case_attempt a
             where a.org_id = @org_id and a.run_id = @own_run_id
             order by a.execution_key
             limit @keys
        ), sibling as (
            insert into public.run
              (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id,
               collection_deadline_at, late_window_minutes)
            select gen_random_uuid(), o.org_id, o.project_id, o.manifest_id, o.suite_revision_id,
                   o.environment_revision_id, now() + interval '2 hours', 1440
              from own o
            returning id, org_id
        ), planned as (
            insert into public.planned_execution
              (id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id)
            select gen_random_uuid(), s.org_id, s.id, c.execution_key, p.case_revision_id, p.variant_hash,
                   p.target_env, p.shard_id
              from sibling s
              cross join copied c
              join public.planned_execution p
                on p.org_id = @org_id and p.run_id = @own_run_id and p.execution_key = c.execution_key
            returning run_id, execution_key
        ), attempts as (
            insert into public.case_attempt
              (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status, started_at)
            select gen_random_uuid(), @org_id, pl.run_id, pl.execution_key, a.case_id, a.attempt_no,
                   a.raw_status, a.normalized_status, a.started_at
              from planned pl
              join public.case_attempt a
                on a.org_id = @org_id and a.run_id = @own_run_id and a.execution_key = pl.execution_key
            returning run_id, execution_key, attempt_no
        ), ready as (
            insert into public.artifact
              (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type,
               checksum, sensitivity, scan_state, state)
            select a.id, @org_id, @project_id, a.run_id, a.execution_key, a.attempt_no, 'trace',
                   'pending/' || @org_id::text || '/' || a.id::text, 'application/zip', @checksum,
                   'normal', 'pending', 'ready'
              from (
                  select gen_random_uuid() as id, t.run_id, t.execution_key, t.attempt_no
                    from attempts t cross join generate_series(1, @per_slot) g
              ) a
            returning execution_key
        )
        select array_agg(distinct execution_key)
          from ready
        """;

    /// <summary>
    /// A sibling run of the same org that repeats the own run's first <paramref name="keys"/> execution keys with
    /// <paramref name="perSlot"/> ready artifacts on every one of their slots — the same slot pairs, a larger count —
    /// and returns the keys it copied. Its parents come from the own run's row, so no world-level environment is
    /// involved and the sibling only exists as the collision case.
    /// </summary>
    private async Task<string[]> SeedCollidingRunAsync(
        string database, Guid org, Guid project, Guid runId, int keys, int perSlot)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        var copied = await scope.ScalarAsync<string[]>(
            CollidingRunSql,
            ("org_id", org), ("project_id", project), ("own_run_id", runId),
            ("keys", keys), ("per_slot", perSlot), ("checksum", Checksum));

        await scope.CommitAsync();

        return copied;
    }

    /// <summary>
    /// One count per <em>expected</em> slot of the own run on <paramref name="keys"/> — its
    /// <c>(execution_key, attempt_no)</c> pairs — of the ready artifacts any other run of the org holds there, and
    /// zero for a slot it holds none on: the collision case's positive control, read on the owner connection after
    /// the seed committed. The left join keeps a slot the sibling never copied in the result, so a missing slot
    /// cannot pass as no evidence.
    /// </summary>
    private async Task<int[]> SiblingReadyPerSlotAsync(string database, Guid org, Guid runId, string[] keys)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));

        return await scope.ScalarAsync<int[]>(
            """
            with expected as (
                select a.execution_key, a.attempt_no
                  from public.case_attempt a
                 where a.org_id = @org_id and a.run_id = @own_run_id
                   and a.execution_key = any(@keys)
            )
            select coalesce(array_agg(ready_count order by execution_key, attempt_no), '{}'::int[])
              from (
                  select e.execution_key, e.attempt_no, count(r.id)::int as ready_count
                    from expected e
                    left join public.artifact r
                      on r.org_id = @org_id and r.run_id <> @own_run_id and r.state = 'ready'
                     and r.execution_key = e.execution_key and r.attempt_no = e.attempt_no
                   group by e.execution_key, e.attempt_no
              ) slots
            """,
            ("org_id", org), ("own_run_id", runId), ("keys", keys));
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
