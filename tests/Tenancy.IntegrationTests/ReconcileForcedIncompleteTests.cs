// The forced-incomplete closure of an overdue run: a run whose producers never started (`queued`) or never finalized
// (`running`) gets no reconcile from any finalize, so the collection deadline is the only thing that can decide it.
// These cases drive the whole platform path — the schedule runner, the sweep handler, the outbox dispatcher and the
// task consumer — so what they prove is the platform behaviour, not a handler body called by hand.
//
// The clock split is the same as the collecting-deadline cases: the scan and the reconcile attempt both compare
// `collection_deadline_at` with the database's `now()`, so a case writes the past into the column and the fake clock
// only moves the schedule's ticks.
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class ReconcileForcedIncompleteTests
{
    /// <summary>The queue the sweep's own fire lands on (the platform sweeps share it).</summary>
    private const string MaintenanceQueue = "maintenance";

    /// <summary>The queue a reconcile lands on when the dispatcher has no route for its type.</summary>
    private const string DefaultQueue = "default";

    private readonly PostgresTenancyFixture _fixture;

    public ReconcileForcedIncompleteTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ a run past its deadline is closed incomplete

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    public async Task Sweep_RunPastItsDeadline_ClosesIncompleteWithReasons(string state)
    {
        var clock = AlignedClock();
        await using var world = await StartAsync(clock);
        var runId = world.RunId;

        // No finalize will ever arrive for this run: its producer slot stays open and nothing else enqueues an attempt,
        // which is exactly the stall the deadline exists for.
        await SetRunStateAsync(world, runId, state);
        await PassCollectionDeadlineAsync(world, runId);

        await SweepAsync(world, clock);

        (await DeadlineRowsAsync(world, runId)).ShouldBe(1, "one overdue run is one reconcile in the outbox");
        (await StateAsync(world, runId)).ShouldBe(state, "the sweep queues an attempt, it never transitions a run itself");

        await DispatchAndReconcileAsync(world, expectedReconciles: 1);

        (await StateAsync(world, runId)).ShouldBe(
            "incomplete",
            "the run never reached collecting, so its evidence cannot prove a pass");
        (await CompletenessAsync(world, runId)).ShouldBe("incomplete");

        var missing = await MissingAsync(world, runId);
        missing.ShouldNotBeNullOrWhiteSpace("a forced incomplete never carries an empty missing[]");
        missing.Contains("missing_shard", StringComparison.Ordinal).ShouldBeTrue("the seeded slot never closed, so its producer is missing");
        missing.Contains("missing_result", StringComparison.Ordinal).ShouldBeTrue("the seeded plan's executions recorded no attempt");

        (await RevisionCountAsync(world, runId)).ShouldBe(1, "one attempt freezes exactly one revision");
        (await PointerAsync(world, runId)).ShouldNotBeNull("the attempt moves the run's evidence pointer to that revision");
        (await StateChangeAuditCountAsync(world, runId)).ShouldBe(
            1,
            "the queued|running -> incomplete edge is audited in the attempt's own transaction");
        (await TaskStateAsync(world, RunReconcileContract.OutboxTaskType)).ShouldBe(
            "done",
            "the dispatcher moved the trigger to the queue and the consumer completed it");
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    public async Task Sweep_RunBeforeItsDeadline_IsIgnored(string state)
    {
        var clock = AlignedClock();
        await using var world = await StartAsync(clock);
        var runId = world.RunId;

        await SetRunStateAsync(world, runId, state);
        await FutureDeadlineAsync(world, runId);

        await SweepAsync(world, clock);

        (await DeadlineRowsAsync(world, runId)).ShouldBe(0, "a run inside its deadline is not overdue");
        (await StateAsync(world, runId)).ShouldBe(state, "nothing decides a run that is still starting");
        (await RevisionCountAsync(world, runId)).ShouldBe(0, "no attempt, no revision");
    }

    /// <summary>
    /// The scan names the three states that carry a declared edge to <c>incomplete</c> and nothing else: a
    /// <c>created</c> run has no shard plan and no such edge, and a terminal run's verdict is its last word.
    /// </summary>
    /// <param name="state">The state a past-due run must be left in.</param>
    [Theory]
    [InlineData("created")]
    [InlineData("complete")]
    [InlineData("canceled")]
    [InlineData("incomplete")]
    public async Task Sweep_StateWithoutAForcedClose_IsIgnored(string state)
    {
        var clock = AlignedClock();
        await using var world = await StartAsync(clock);
        var runId = world.RunId;

        await SetRunStateAsync(world, runId, state);
        await PassCollectionDeadlineAsync(world, runId);

        await SweepAsync(world, clock);

        (await DeadlineRowsAsync(world, runId)).ShouldBe(
            0,
            $"the scan names queued, running and collecting only: a {state} run is never force-closed");
        (await StateAsync(world, runId)).ShouldBe(state);
        (await RevisionCountAsync(world, runId)).ShouldBe(0, "no attempt, no revision");
    }

    /// <summary>
    /// The scan itself, one overdue run per state of the machine: only <c>queued</c>, <c>running</c> and
    /// <c>collecting</c> come back, whatever their tenant — the sweep is the only caller and it must see exactly the
    /// runs a reconcile attempt can decide.
    /// </summary>
    [Fact]
    public async Task DeadlineScan_OverdueRuns_NamesQueuedRunningAndCollectingOnly()
    {
        await using var world = await StartAsync(AlignedClock());

        var runs = new Dictionary<string, Guid>(StringComparer.Ordinal) { ["created"] = world.RunId };
        foreach (var state in new[] { "queued", "running", "collecting", "complete", "canceled", "incomplete" })
        {
            runs[state] = (await world.SeedOtherRunAsync(world.Org)).RunId;
        }

        foreach (var (state, runId) in runs)
        {
            await _fixture.ExecuteAsync(
                world.Database,
                $"update public.run set state = '{state}', collection_deadline_at = now() - interval '1 second' "
                + $"where id = '{runId:D}'");
        }

        var scanned = ((await world.ScalarAsync<string?>(
                    "select string_agg(run_id::text, ',' order by run_id) from evidgate.reconcile_overdue_runs(null, null, 100)"))
                ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

        scanned.Count.ShouldBe(3, "one overdue run per decidable state, and nothing else");
        foreach (var state in new[] { "queued", "running", "collecting" })
        {
            scanned.ShouldContain(runs[state].ToString("D"), $"an overdue {state} run is named");
        }

        foreach (var state in new[] { "created", "complete", "canceled", "incomplete" })
        {
            scanned.ShouldNotContain(runs[state].ToString("D"), $"a {state} run is never named");
        }
    }

    // ------------------------------------------------------------------ the degenerate plan

    /// <summary>
    /// The floor of the rule: a run with no producer slot and no planned execution gives the comparison nothing to
    /// name, and a forced close still records one closed-list reason — <c>missing_result</c>, because the plan this run
    /// should have carried never reported anything.
    /// </summary>
    [Fact]
    public async Task Sweep_RunWithoutSlotsOrPlan_ClosesIncompleteWithMissingResult()
    {
        var clock = AlignedClock();
        await using var world = await StartAsync(clock, planned: 0, slots: 0);
        var runId = world.RunId;

        await SetRunStateAsync(world, runId, "queued");
        await PassCollectionDeadlineAsync(world, runId);

        await SweepAsync(world, clock);
        (await DeadlineRowsAsync(world, runId)).ShouldBe(1, "a run with no slot is still overdue and still named");
        await DispatchAndReconcileAsync(world, expectedReconciles: 1);

        (await StateAsync(world, runId)).ShouldBe("incomplete", "a run that proved nothing is never complete");
        (await CompletenessAsync(world, runId)).ShouldBe("incomplete");
        (await MissingAsync(world, runId)).ShouldBe(
            "missing_result",
            "an empty comparison still records what is absent: no plan ever reported");
        (await RevisionCountAsync(world, runId)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ world and driving

    /// <summary>
    /// One migrated database with an org, a project, a run and one producer slot, composed with the production queue
    /// mechanism and a <see cref="FakeTimeProvider"/> so a case drives the schedule instead of waiting for it.
    /// </summary>
    /// <param name="clock">The fake clock the schedule's ticks read.</param>
    /// <param name="planned">How many planned executions the seeded run carries.</param>
    /// <param name="slots">How many producer slots the seeded run carries.</param>
    private async Task<IngestTestWorld> StartAsync(FakeTimeProvider clock, int planned = 2, int slots = 1)
    {
        var database = new DatabaseNameHolder();

        var world = await IngestTestWorld.StartAsync(
            _fixture,
            IngestTestWorld.Writer(),
            planned: planned,
            slots: slots,
            services: services =>
            {
                services.AddEvidgateTasks(new ConfigurationBuilder().Build());
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);

                // The fixture mints no login for evidgate_worker_sched: the double opens a superuser connection to this
                // database and switches to the sweep role, so grants and row level security are evaluated as that role.
                services.RemoveAll<IWorkerSchedDataSource>();
                services.AddSingleton<IWorkerSchedDataSource>(_ => new SweepRoleDataSource(_fixture, database));
            });

        database.Name = world.Database;
        return world;
    }

    /// <summary>The database name, filled in once the world exists and read when the sweep first opens a connection.</summary>
    private sealed class DatabaseNameHolder
    {
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>Opens the sweep role's connection: a superuser connection switched with <c>set role</c>.</summary>
    private sealed class SweepRoleDataSource(PostgresTenancyFixture fixture, DatabaseNameHolder database)
        : IWorkerSchedDataSource
    {
        public async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
        {
            var connection = await fixture.OpenSuperuserAsync(database.Name).ConfigureAwait(false);
            try
            {
                await using var switchRole = connection.CreateCommand();
                switchRole.CommandText = "set role evidgate_worker_sched";
                await switchRole.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return connection;
        }
    }

    /// <summary>
    /// A fixed, cron-aligned instant: the runner composes the occurrences due in its lookback window from the schedule's
    /// cron, so a boundary-aligned start puts every fire on an exact ten-minute mark.
    /// </summary>
    private static FakeTimeProvider AlignedClock() =>
        new(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>Runs one schedule pass at the fake instant and waits for its handler to finish (it only enqueues).</summary>
    private static async Task SweepAsync(IngestTestWorld world, FakeTimeProvider clock)
    {
        (await world.Host.Services.GetRequiredService<TaskScheduleRunner>().RunOnceAsync(clock.GetUtcNow(), Token))
            .ShouldBeGreaterThan(0, "the schedule pass fires the registered sweeps");

        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (await TaskStateAsync(world, RunReconcileContract.DeadlineSweepTaskType) == "done")
            {
                return;
            }

            await consumer.PollOnceAsync(MaintenanceQueue, Token);
        }

        (await TaskStateAsync(world, RunReconcileContract.DeadlineSweepTaskType)).ShouldBe(
            "done",
            "the maintenance consumer runs the sweep task to done");
    }

    /// <summary>Dispatches, then claims on the reconcile queue until the expected attempts ran and completed.</summary>
    private static async Task DispatchAndReconcileAsync(IngestTestWorld world, int expectedReconciles)
    {
        (await world.Host.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(Token)).Enqueued
            .ShouldBeGreaterThanOrEqualTo(expectedReconciles, "the sweep's triggers reach the queue");

        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();
        for (var attempt = 0; attempt < 3 * expectedReconciles + 3; attempt++)
        {
            if (await CountDoneReconcilesAsync(world) >= expectedReconciles)
            {
                return;
            }

            await consumer.PollOnceAsync(DefaultQueue, Token);
        }

        (await CountDoneReconcilesAsync(world)).ShouldBeGreaterThanOrEqualTo(
            expectedReconciles,
            "every queued reconcile is claimed and completed by the consumer");
    }

    private static Task<long> CountDoneReconcilesAsync(IngestTestWorld world) =>
        world.CountAsync(
            "task",
            where: $"task_type = '{RunReconcileContract.OutboxTaskType}' and state = 'done'");

    /// <summary>The state of the newest task row of one type, or null when the type never ran.</summary>
    private static Task<string?> TaskStateAsync(IngestTestWorld world, string taskType) =>
        world.ScalarAsync<string?>(
            $"select state from public.task where task_type = '{taskType}' order by created_at desc limit 1");

    private static Task<long> DeadlineRowsAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<long>(
            "select count(*) from public.outbox where idempotency_key like "
            + $"'{RunReconcileContract.DeadlineIdempotencyKey(runId)}%'");

    private static Task<string> StateAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<string>($"select state from public.run where id = '{runId:D}'");

    private static Task<Guid?> PointerAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<Guid?>($"select current_evidence_revision_id from public.run where id = '{runId:D}'");

    private static Task<string> CompletenessAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<string>(
            "select completeness from public.evidence_revision "
            + $"where org_id = '{world.Org}' and run_id = '{runId:D}' order by revision_no desc limit 1");

    private static Task<string> MissingAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<string>(
            "select coalesce(array_to_string(missing, ','), '') from public.evidence_revision "
            + $"where org_id = '{world.Org}' and run_id = '{runId:D}' order by revision_no desc limit 1");

    private static Task<long> RevisionCountAsync(IngestTestWorld world, Guid runId) =>
        world.CountAsync("evidence_revision", where: $"org_id = '{world.Org}' and run_id = '{runId:D}'");

    private static Task<long> StateChangeAuditCountAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<long>(
            $"select count(*) from public.audit_log where org_id = '{world.Org}' "
            + $"and action = '{RunStateTransitionContract.AuditAction}' and object_type = 'run' "
            + $"and object_id = '{runId:D}' and actor_type = 'system'");

    private Task SetRunStateAsync(IngestTestWorld world, Guid runId, string state) =>
        _fixture.ExecuteAsync(world.Database, $"update public.run set state = '{state}' where id = '{runId:D}'");

    /// <summary>Moves the deadline into the database's past, the only clock the scan and the attempt compare against.</summary>
    private Task PassCollectionDeadlineAsync(IngestTestWorld world, Guid runId) =>
        _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set collection_deadline_at = now() - interval '1 second' where id = '{runId:D}'");

    /// <summary>Moves the deadline a day ahead, for the cases that must stay inside it.</summary>
    private Task FutureDeadlineAsync(IngestTestWorld world, Guid runId) =>
        _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set collection_deadline_at = now() + interval '1 day' where id = '{runId:D}'");
}
