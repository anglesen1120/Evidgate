// The collection-deadline sweep against a real PostgreSQL: a run whose shard never finalizes is otherwise never
// reconciled, because a reconcile is enqueued only by the finalize that closes a producer slot. These cases drive the
// real schedule runner (with a fake clock for the ticks), the real task consumer, the real handler and the real outbox,
// so what they prove is the platform path and not a handler body called by hand.
//
// The clock split matters: the sweep's scan compares `collection_deadline_at` with the database's `now()` (inside
// `evidgate.reconcile_overdue_runs`), exactly like the reconcile attempt it triggers. The FakeTimeProvider only moves the
// schedule's ticks, so a case that wants an overdue run writes the past into the column (`PassCollectionDeadlineAsync`)
// and a case that wants a run still collecting leaves the seeded future deadline alone.
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
public sealed class ReconcileDeadlineSweepTests
{
    /// <summary>The queue the sweep's own fire lands on (the platform sweeps share it).</summary>
    private const string MaintenanceQueue = "maintenance";

    /// <summary>The queue a reconcile lands on when the dispatcher has no route for its type.</summary>
    private const string DefaultQueue = "default";

    private readonly PostgresTenancyFixture _fixture;

    public ReconcileDeadlineSweepTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ one sweep, one overdue run

    [Fact]
    public async Task Sweep_PastDeadlineWithAnOpenShard_QueuesOneReconcile_AndTheAttemptFinishesIncomplete()
    {
        var clock = AlignedClock();
        await using var world = await StartAsync(clock);
        var runId = world.RunId;

        // The shard of the seeded run never closes: the slot stays open, and the deadline is in the past, which is the
        // state a run is stuck in when its producer died before finalizing.
        await SetRunStateAsync(world, runId, "collecting");
        await PassCollectionDeadlineAsync(world, runId);

        await SweepAsync(world, clock);

        (await DeadlineRowsAsync(world, runId)).ShouldBe(1, customMessage: "one overdue run is one reconcile in the outbox");
        (await OutboxOrgAsync(world, runId)).ShouldBe(world.Org, customMessage: "the row lands in the run's own tenant");

        await DispatchAndReconcileAsync(world, expectedReconciles: 1);

        (await StateAsync(world, runId)).ShouldBe(
            "incomplete",
            customMessage: "the attempt found the deadline passed and an open shard, so the run leaves collecting");
        (await MissingAsync(world, runId)).ShouldContain("missing_shard", customMessage: "the open shard is the missing producer");
        (await RevisionCountAsync(world)).ShouldBe(1, customMessage: "the attempt freezes exactly one revision");

        (await TaskStateAsync(world, RunReconcileContract.DeadlineSweepTaskType)).ShouldBe("done");
        (await TaskStateAsync(world, RunReconcileContract.OutboxTaskType)).ShouldBe(
            "done",
            customMessage: "the dispatcher moved the trigger to the queue and the consumer completed it");
    }

    [Fact]
    public async Task Sweep_BeforeTheDeadline_LeavesTheRunCollecting()
    {
        var clock = AlignedClock();
        await using var world = await StartAsync(clock);
        var runId = world.RunId;

        await SetRunStateAsync(world, runId, "collecting");
        await FutureDeadlineAsync(world, runId);

        await SweepAsync(world, clock);

        (await DeadlineRowsAsync(world, runId)).ShouldBe(0, customMessage: "a run before its deadline is not overdue");
        (await StateAsync(world, runId)).ShouldBe("collecting", customMessage: "nothing decided the run early");
        (await RevisionCountAsync(world)).ShouldBe(0, customMessage: "no attempt, no revision");
    }

    [Fact]
    public async Task Sweep_Twice_QueuesTheRunOnce_AndYieldsOneRevision()
    {
        var clock = AlignedClock();
        await using var world = await StartAsync(clock);
        var runId = world.RunId;

        await SetRunStateAsync(world, runId, "collecting");
        await PassCollectionDeadlineAsync(world, runId);

        await SweepAsync(world, clock);
        await DispatchAndReconcileAsync(world, expectedReconciles: 1);
        (await StateAsync(world, runId)).ShouldBe("incomplete");

        // The attempt's own transition enqueued one `run.state_changed` row AFTER the dispatch above (the transition
        // writes it in the same tenant transaction as the revision), so drain it first: the next dispatch must measure
        // only what the second sweep adds, not a row the first attempt left behind.
        await DispatchAsync(world);

        // The next fire finds the same run (it is still past its deadline), and the key keeps it from queueing a second
        // attempt: one run is one deadline attempt, whatever the sweep does next.
        clock.Advance(TimeSpan.FromMinutes(10));
        await SweepAsync(world, clock);
        (await DispatchAsync(world)).Enqueued.ShouldBe(
            0,
            customMessage: "the second sweep adds no outbox row at all, so the dispatcher has nothing to move");

        (await DeadlineRowsAsync(world, runId)).ShouldBe(1, customMessage: "the second sweep enqueues nothing");
        (await RevisionCountAsync(world)).ShouldBe(1, customMessage: "one attempt, one revision");
        (await TaskStateAsync(world, RunReconcileContract.OutboxTaskType)).ShouldBe("done");
        (await CountQueueRowsAsync(world)).ShouldBe(1, customMessage: "one trigger, one task row, however often the sweep runs");
    }

    [Fact]
    public async Task Sweep_CompletedRun_IsIgnored()
    {
        var clock = AlignedClock();
        await using var world = await StartAsync(clock);
        var runId = world.RunId;

        // The run already answered: its deadline is in the past, but its state is terminal, so the scan cannot name it.
        await SetRunStateAsync(world, runId, "complete");
        await PassCollectionDeadlineAsync(world, runId);

        await SweepAsync(world, clock);

        (await DeadlineRowsAsync(world, runId)).ShouldBe(0, customMessage: "a decided run is not collectable");
        (await StateAsync(world, runId)).ShouldBe("complete");
        (await RevisionCountAsync(world)).ShouldBe(0, customMessage: "a completed run is never re-frozen by the sweep");
    }

    [Fact]
    public async Task Sweep_RunningRunPastItsDeadline_IsNotQueued()
    {
        var clock = AlignedClock();
        await using var world = await StartAsync(clock);
        var runId = world.RunId;

        // A run whose events may still be arriving. It is past its deadline, but the reconcile refuses every state
        // but `collecting` and only a finalize moves `running -> collecting`, so queueing it here would spend the
        // run's single deadline key on an attempt that can only answer NotCollectable.
        await SetRunStateAsync(world, runId, "running");
        await PassCollectionDeadlineAsync(world, runId);

        await SweepAsync(world, clock);

        (await DeadlineRowsAsync(world, runId)).ShouldBe(
            0,
            customMessage: "the scan names collecting runs only: a running run is not collectable");
        (await StateAsync(world, runId)).ShouldBe(
            "running",
            customMessage: "the sweep queues an attempt, it never transitions a run itself");
    }


    [Fact]
    public async Task Sweep_TwoTenants_QueuesAndReconcilesEachRunInItsOwnScope()
    {
        var clock = AlignedClock();
        await using var world = await StartAsync(clock);
        var (otherOrg, _) = await world.CreateOtherOrgAsync("sweep-tenant-b");
        var otherRun = await world.SeedOtherRunAsync(otherOrg);

        // Both tenants have a run stuck past its deadline. The scan crosses tenants on purpose — it cannot run inside
        // one scope — so this case is what proves the write does not.
        await SetRunStateAsync(world, world.RunId, "collecting");
        await PassCollectionDeadlineAsync(world, world.RunId);
        await _fixture.ExecuteAsync(
            world.Database,
            "update public.run set state = 'collecting', collection_deadline_at = now() - interval '1 second' "
            + $"where id = '{otherRun.RunId:D}'");

        await SweepAsync(world, clock);

        (await DeadlineRowsAsync(world, world.RunId)).ShouldBe(1, customMessage: "the first tenant's run is queued once");
        (await DeadlineRowsAsync(world, otherRun.RunId)).ShouldBe(1, customMessage: "the second tenant's run is queued once");
        (await OutboxOrgAsync(world, world.RunId)).ShouldBe(
            world.Org,
            customMessage: "each row lands in the tenant of its own run, never in another's");
        (await OutboxOrgAsync(world, otherRun.RunId)).ShouldBe(
            otherOrg,
            customMessage: "the second tenant's row carries the second tenant");

        await DispatchAndReconcileAsync(world, expectedReconciles: 2);

        (await RevisionCountForAsync(world, world.Org)).ShouldBe(
            1,
            customMessage: "one attempt, one revision for the first tenant");
        (await RevisionCountForAsync(world, otherOrg)).ShouldBe(
            1,
            customMessage: "one attempt, one revision for the second tenant, written in its own scope");
    }

    [Fact]
    public async Task DeadlineScan_IsExecutableByTheSweepRoleAlone()
    {
        await using var world = await StartAsync(AlignedClock());

        var privileges = await _fixture.ColumnAsync<string>(
            world.Database,
            """
            select r.rolname || ':' || has_function_privilege(
                     r.rolname,
                     'evidgate.reconcile_overdue_runs(uuid, uuid, integer)',
                     'execute')::text
              from pg_catalog.pg_roles r
             where r.rolname in ('evidgate_app', 'evidgate_operator', 'evidgate_worker_sched')
             order by r.rolname
            """);

        privileges.ShouldBe(
            ["evidgate_app:false", "evidgate_operator:false", "evidgate_worker_sched:true"],
            customMessage: "the scan is the sweep role's alone: no tenant role can read across tenants through it");

        var definer = await _fixture.ScalarAsync<string>(
            world.Database,
            """
            select r.rolname || '|' || p.prosecdef::text || '|' || coalesce(array_to_string(p.proconfig, ','), '')
              from pg_catalog.pg_proc p
              join pg_catalog.pg_roles r on r.oid = p.proowner
             where p.pronamespace = 'evidgate'::regnamespace
               and p.proname = 'reconcile_overdue_runs'
            """);

        definer.ShouldStartWith(
            "evidgate_owner|true|",
            customMessage: "an owner definer is the only role with a policy-free view, and the body is what the lint audits");
        definer.ShouldContain("search_path=", customMessage: "an empty search_path keeps the body immune to a caller's path");

        // The ACL is read entry by entry (`aclexplode`), never as text: the owner's own entry
        // `evidgate_owner=X/evidgate_owner` contains the substring a textual search for a PUBLIC grant would
        // look for, so a text match cannot tell the two apart. `grantee = 0` is PUBLIC and nothing else.
        var acl = await _fixture.ScalarAsync<string>(
            world.Database,
            """
            select count(*) filter (where acl.grantee = 0)::text
                   || ':' ||
                   count(*) filter (where acl.grantee = 'evidgate_worker_sched'::regrole
                                      and acl.privilege_type = 'EXECUTE')::text
              from pg_catalog.pg_proc p
              cross join lateral aclexplode(p.proacl) acl
             where p.pronamespace = 'evidgate'::regnamespace
               and p.proname = 'reconcile_overdue_runs'
            """);

        acl.ShouldBe(
            "0:1",
            customMessage: "no entry grants PUBLIC, and the sweep role holds EXECUTE exactly once");
    }

    // ------------------------------------------------------------------ world and driving

    /// <summary>
    /// One migrated database with an org, a project, a run and one producer slot, composed with the production queue
    /// mechanism and a <see cref="FakeTimeProvider"/> so a case drives the schedule instead of waiting for it.
    /// </summary>
    private async Task<IngestTestWorld> StartAsync(FakeTimeProvider clock)
    {
        var database = new DatabaseNameHolder();

        var world = await IngestTestWorld.StartAsync(
            _fixture,
            IngestTestWorld.Writer(),
            services: services =>
            {
                services.AddEvidgateTasks(new ConfigurationBuilder().Build());
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);

                // The fixture mints no login for evidgate_worker_sched, and the database name is only known once the
                // world has started: the double opens a superuser connection to it and switches to the sweep role, the
                // way InviteSchemaTests exercises the same role. Grants and row level security are evaluated against the
                // switched role, so the scan really runs with the sweep role's privileges.
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
    /// A fixed, cron-aligned instant. The runner composes the occurrences due in its lookback window (300 s) from the
    /// schedule's cron, so a boundary-aligned start puts every fire on an exact ten-minute mark and makes a second
    /// advance land on the next one — with `UtcNow` neither property holds.
    /// </summary>
    private static FakeTimeProvider AlignedClock() =>
        new(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>Runs one schedule pass at the fake instant (which only ENQUEUES the sweep's task row).</summary>
    private static Task<int> FireAsync(IngestTestWorld world, FakeTimeProvider clock) =>
        world.Host.Services.GetRequiredService<TaskScheduleRunner>().RunOnceAsync(clock.GetUtcNow(), Token);

    /// <summary>
    /// The whole platform path up to the outbox: one schedule pass, then the maintenance consumer claims the sweep row
    /// and runs its handler, which is what writes the reconcile rows.
    /// </summary>
    private static async Task SweepAsync(IngestTestWorld world, FakeTimeProvider clock)
    {
        (await FireAsync(world, clock)).ShouldBeGreaterThan(
            0,
            customMessage: "the schedule pass fires the registered sweeps");

        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (await IsDoneAsync(world, RunReconcileContract.DeadlineSweepTaskType))
            {
                return;
            }

            await consumer.PollOnceAsync(MaintenanceQueue, Token);
        }

        (await IsDoneAsync(world, RunReconcileContract.DeadlineSweepTaskType)).ShouldBeTrue(
            customMessage: "the maintenance consumer runs the sweep task to done");
    }

    /// <summary>Moves the outbox rows into the task queue: the dispatcher is what a reconcile task needs to exist.</summary>
    private static Task<OutboxDispatchResult> DispatchAsync(IngestTestWorld world) =>
        world.Host.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(Token);

    /// <summary>Dispatches, then claims on the reconcile queue until the expected tasks ran and completed.</summary>
    private static async Task DispatchAndReconcileAsync(IngestTestWorld world, int expectedReconciles)
    {
        (await DispatchAsync(world)).Enqueued.ShouldBeGreaterThanOrEqualTo(
            expectedReconciles,
            customMessage: "the sweep's triggers reach the queue");

        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();
        for (var attempt = 0; attempt < 3 * expectedReconciles + 3; attempt++)
        {
            if (await CountDoneAsync(world, RunReconcileContract.OutboxTaskType) >= expectedReconciles)
            {
                return;
            }

            await consumer.PollOnceAsync(DefaultQueue, Token);
        }

        (await CountDoneAsync(world, RunReconcileContract.OutboxTaskType)).ShouldBeGreaterThanOrEqualTo(
            expectedReconciles,
            customMessage: "every queued reconcile is claimed and completed by the consumer");
    }

    /// <summary>The state of the newest task row of one type, or null when the type never ran.</summary>
    private static async Task<string?> TaskStateAsync(IngestTestWorld world, string taskType) =>
        await world.ScalarAsync<string?>(
            $"select state from public.task where task_type = '{taskType}' order by created_at desc limit 1");

    private static async Task<bool> IsDoneAsync(IngestTestWorld world, string taskType) =>
        await TaskStateAsync(world, taskType) == "done";

    /// <summary>How many rows of one type ever reached <c>done</c>.</summary>
    private static Task<long> CountDoneAsync(IngestTestWorld world, string taskType) =>
        world.CountAsync("task", where: $"task_type = '{taskType}' and state = 'done'");

    /// <summary>How many reconcile task rows the dispatcher created (the queue's side of the one-key contract).</summary>
    private static Task<long> CountQueueRowsAsync(IngestTestWorld world) =>
        world.CountAsync("task", where: $"task_type = '{RunReconcileContract.OutboxTaskType}'");

    private static Task<long> DeadlineRowsAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<long>(
            "select count(*) from public.outbox where idempotency_key = "
            + $"'{RunReconcileContract.DeadlineIdempotencyKey(runId)}'");

    private static Task<Guid?> OutboxOrgAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<Guid?>(
            "select org_id from public.outbox where idempotency_key = "
            + $"'{RunReconcileContract.DeadlineIdempotencyKey(runId)}'");

    private static Task<string> StateAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<string>($"select state from public.run where id = '{runId:D}'");

    private static Task<string> MissingAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<string>(
            "select coalesce(array_to_string(missing, ','), '') from public.evidence_revision "
            + $"where org_id = '{world.Org}' and run_id = '{runId:D}' order by revision_no desc limit 1");

    private static Task<long> RevisionCountAsync(IngestTestWorld world) => world.CountAsync("evidence_revision");

    /// <summary>How many revisions one tenant owns: a cross-tenant write would show up on the wrong org.</summary>
    private static Task<long> RevisionCountForAsync(IngestTestWorld world, Guid orgId) =>
        world.CountAsync("evidence_revision", where: $"org_id = '{orgId:D}'");

    private Task SetRunStateAsync(IngestTestWorld world, Guid runId, string state) =>
        _fixture.ExecuteAsync(world.Database, $"update public.run set state = '{state}' where id = '{runId:D}'");

    /// <summary>
    /// Moves the deadline into the DB past. The scan compares it with <c>now()</c> in the database, so this column is
    /// the only clock a case can set — the fake clock moves the ticks, never this comparison.
    /// </summary>
    private Task PassCollectionDeadlineAsync(IngestTestWorld world, Guid runId) =>
        _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set collection_deadline_at = now() - interval '1 second' where id = '{runId:D}'");

    /// <summary>Moves the deadline a day ahead, for the case that must stay before it.</summary>
    private Task FutureDeadlineAsync(IngestTestWorld world, Guid runId) =>
        _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set collection_deadline_at = now() + interval '1 day' where id = '{runId:D}'");
}
