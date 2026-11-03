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

        // The next fire does not even see this run any more: the attempt decided it, so the scan no longer names it.
        // A done attempt is spent, and a spent attempt is only re-armed while its run is still overdue — nothing
        // re-arms a run that already has a verdict.
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
    public async Task Sweep_RunningRunPastItsDeadline_IsQueued_AndTheAttemptClosesItIncomplete()
    {
        var clock = AlignedClock();
        await using var world = await StartAsync(clock);
        var runId = world.RunId;

        // A run whose events may still be arriving, past its deadline. The deadline is a verdict, not a wait: the scan
        // names every decidable state, and the attempt closes the run incomplete with the producers that never
        // reported — a run nobody can finish must not stay running for ever.
        await SetRunStateAsync(world, runId, "running");
        await PassCollectionDeadlineAsync(world, runId);

        await SweepAsync(world, clock);

        (await DeadlineRowsAsync(world, runId)).ShouldBe(
            1,
            customMessage: "the scan names the run past its deadline whatever decidable state it is in");

        await DispatchAndReconcileAsync(world, expectedReconciles: 1);

        (await StateAsync(world, runId)).ShouldBe(
            "incomplete",
            customMessage: "the attempt closes the overdue run instead of leaving it running");
        (await MissingAsync(world, runId)).ShouldNotBeEmpty(
            customMessage: "a run closed without complete evidence names what was missing");
        (await RevisionCountAsync(world)).ShouldBe(1, customMessage: "the forced verdict freezes one revision");
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
    public async Task Sweep_SmallPageAcrossTwoTenants_QueuesEveryRunExactlyOnce()
    {
        var clock = AlignedClock();
        await using var world = await StartAsync(clock);
        var (otherOrg, _) = await world.CreateOtherOrgAsync("sweep-paging-b");

        // Five overdue runs split 3/2 with a page of two puts a page boundary INSIDE a tenant: the page that
        // straddles carries the last run of the first org and the first run of the second, so the handler opens two
        // tenant scopes for one page and must still queue neither run twice.
        var firstOrg = world.Org.CompareTo(otherOrg) < 0 ? world.Org : otherOrg;
        var lastOrg = firstOrg == world.Org ? otherOrg : world.Org;

        var firstRuns = new List<Guid>();
        var lastRuns = new List<Guid>();
        (firstOrg == world.Org ? firstRuns : lastRuns).Add(world.RunId);

        while (firstRuns.Count < 3 || lastRuns.Count < 2)
        {
            var org = firstRuns.Count < 3 ? firstOrg : lastOrg;
            (org == firstOrg ? firstRuns : lastRuns).Add((await world.SeedOtherRunAsync(org)).RunId);
        }

        var overdue = firstRuns.Concat(lastRuns).ToList();
        foreach (var runId in overdue)
        {
            await MarkOverdueAsync(world, runId);
        }

        await SweepOnceAsync(world, clock, pageLimit: 2);

        foreach (var runId in overdue)
        {
            (await DeadlineRowsAsync(world, runId)).ShouldBe(
                1,
                customMessage: $"run {runId:D} is queued once, whether it sat whole in a page or on a boundary");
        }

        (await OutboxOrgAsync(world, firstRuns[0])).ShouldBe(
            firstOrg,
            customMessage: "each row lands in the tenant of its own run");
        (await OutboxOrgAsync(world, lastRuns[0])).ShouldBe(
            lastOrg,
            customMessage: "the page that straddles writes into both tenants");

        await DispatchAndReconcileAsync(world, expectedReconciles: overdue.Count);

        (await RevisionCountAsync(world)).ShouldBe(
            overdue.Count,
            customMessage: "one attempt per queued run, so a page boundary neither skipped nor repeated an attempt");
    }

    [Fact]
    public async Task Sweep_TwiceBeforeDispatch_LeavesOneOutboxRow_AndOneRevisionAfterIt()
    {
        var clock = AlignedClock();
        await using var world = await StartAsync(clock);
        var runId = world.RunId;

        await SetRunStateAsync(world, runId, "collecting");
        await PassCollectionDeadlineAsync(world, runId);

        // The repeat case above dispatches between the fires, so the second sweep measures the queue's key. Here
        // nothing dispatches: the second handler's probe finds the first fire's row still pending in the outbox,
        // which is the branch that keeps two fires of the same key from writing two triggers.
        await SweepOnceAsync(world, clock);
        clock.Advance(TimeSpan.FromMinutes(10));
        await SweepOnceAsync(world, clock);

        (await CountDoneAsync(world, RunReconcileContract.DeadlineSweepTaskType)).ShouldBe(
            2,
            customMessage: "both fires ran their handler to done, so the second one really reached the probe");
        (await DeadlineRowsAsync(world, runId)).ShouldBe(
            1,
            customMessage: "the still-pending key keeps the second fire from writing a second trigger");
        (await CountQueueRowsAsync(world)).ShouldBe(0, customMessage: "nothing dispatched yet, so no task row exists");

        await DispatchAndReconcileAsync(world, expectedReconciles: 1);

        (await RevisionCountAsync(world)).ShouldBe(1, customMessage: "one trigger, one attempt, one revision");
        (await CountQueueRowsAsync(world)).ShouldBe(1, customMessage: "the pending trigger produced one task row");
    }

    [Fact]
    public async Task Sweep_PageBoundCutAFullLastPage_AndTheNextFireQueuesTheRemainder()
    {
        var clock = AlignedClock();
        await using var world = await StartAsync(clock);

        // Eleven overdue runs at one row per page is one more than the ten-page bound, so the fire's last page is
        // still full and it cannot tell the backlog ended. The run past the bound must reach the next fire, never be
        // dropped: the bound delays a run, it never loses one.
        var runs = new List<Guid> { world.RunId };
        while (runs.Count < 11)
        {
            runs.Add((await world.SeedOtherRunAsync(world.Org)).RunId);
        }

        foreach (var runId in runs)
        {
            await MarkOverdueAsync(world, runId);
        }

        await SweepOnceAsync(world, clock, pageLimit: 1);

        (await QueuedRunCountAsync(world)).ShouldBe(
            10,
            customMessage: "one fire drains at most its bound of ten pages, each a run here, so one of the eleven waits");

        clock.Advance(TimeSpan.FromMinutes(10));
        await SweepOnceAsync(world, clock);

        (await QueuedRunCountAsync(world)).ShouldBe(
            11,
            customMessage: "the next fire queues the run the bound cut off, and re-probes the ten it already queued");
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

    /// <summary>
    /// One schedule fire and its handler, observed by the count of completed sweep rows: a case that fires twice must
    /// prove the second handler ran, which a "the newest row is done" read cannot — the first fire's row already is.
    /// <paramref name="pageLimit"/> narrows the queued fire's payload, the operator-replayable field a paging case
    /// needs, before the consumer claims it.
    /// </summary>
    private async Task SweepOnceAsync(IngestTestWorld world, FakeTimeProvider clock, int? pageLimit = null)
    {
        var completed = await CountDoneAsync(world, RunReconcileContract.DeadlineSweepTaskType);

        (await FireAsync(world, clock)).ShouldBeGreaterThan(
            0,
            customMessage: "the schedule pass fires the registered sweeps");

        if (pageLimit is { } limit)
        {
            await _fixture.ExecuteAsync(
                world.Database,
                $"update public.task set payload = '{{\"limit\":{limit}}}'::jsonb "
                + $"where task_type = '{RunReconcileContract.DeadlineSweepTaskType}' and state <> 'done'");

            (await world.ScalarAsync<int>(
                "select (payload ->> 'limit')::int from public.task "
                + $"where task_type = '{RunReconcileContract.DeadlineSweepTaskType}' and state <> 'done' "
                + "order by created_at desc limit 1"))
                .ShouldBe(limit, customMessage: "the queued fire carries the narrowed page size");
        }

        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (await CountDoneAsync(world, RunReconcileContract.DeadlineSweepTaskType) > completed)
            {
                return;
            }

            await consumer.PollOnceAsync(MaintenanceQueue, Token);
        }

        (await CountDoneAsync(world, RunReconcileContract.DeadlineSweepTaskType)).ShouldBeGreaterThan(
            completed,
            customMessage: "the maintenance consumer runs this fire's sweep task to done");
    }

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

    /// <summary>How many runs carry a collection-deadline trigger in the outbox, whatever their tenant.</summary>
    private static Task<long> QueuedRunCountAsync(IngestTestWorld world) =>
        world.CountAsync("outbox", where: "idempotency_key like 'orchestrator:run:%:reconcile:deadline'");

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

    /// <summary>Puts one run in the state the scan names — collectable and past its deadline — in one statement.</summary>
    private Task MarkOverdueAsync(IngestTestWorld world, Guid runId) =>
        _fixture.ExecuteAsync(
            world.Database,
            "update public.run set state = 'collecting', collection_deadline_at = now() - interval '1 minute' "
            + $"where id = '{runId:D}'");

    /// <summary>Moves the deadline a day ahead, for the case that must stay before it.</summary>
    private Task FutureDeadlineAsync(IngestTestWorld world, Guid runId) =>
        _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set collection_deadline_at = now() + interval '1 day' where id = '{runId:D}'");
}
