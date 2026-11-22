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
public sealed partial class ReconcileDeadlineSweepTests
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
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database.Name);
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
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database.Name);
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
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database.Name);
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
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database.Name);
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
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database.Name);
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
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database.Name);
        var (otherOrg, _) = await world.CreateOtherOrgAsync(Unique.Slug("sweep-tenant-b"));
        var otherRun = await world.SeedOtherRunAsync(otherOrg);

        // Both tenants have a run stuck past its deadline. The scan crosses tenants on purpose — it cannot run inside
        // one scope — so this case is what proves the write does not.
        await SetRunStateAsync(world, world.RunId, "collecting");
        await PassCollectionDeadlineAsync(world, world.RunId);
        await MarkOverdueAsync(world, otherRun.RunId);

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
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database.Name);
        var (otherOrg, _) = await world.CreateOtherOrgAsync(Unique.Slug("sweep-paging-b"));

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
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database.Name);
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
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database.Name);

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
}
