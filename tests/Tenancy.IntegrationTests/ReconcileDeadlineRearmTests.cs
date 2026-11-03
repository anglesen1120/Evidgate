// The collection-deadline sweep's re-arm, against a real PostgreSQL: a deadline trigger that ends without a verdict
// would otherwise leave its run open past the deadline for ever, because the sweep's probe used to treat "the key
// exists" as "the attempt happened". These cases drive the real schedule runner (fake clock for the ticks), the real
// task consumer and lease writer, the real handler and the real outbox. The dead letter is produced by the queue's own
// retry exhaustion — the attempt sits at its cap and its lease expires, so the reclaim takes it to `dead_letter` — and
// never by writing the state from a test.
//
// An attempt is spent in three ways: a dead letter, a completion that left the run open, or a dispatched task that
// retention purged. Each case drives one of those, and the control cases show what is *not* spent — an attempt still
// queued, or one not yet dispatched — because those must keep the sweep off the run.
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class ReconcileDeadlineRearmTests
{
    /// <summary>The queue the sweep's own fire lands on (the platform sweeps share it).</summary>
    private const string MaintenanceQueue = "maintenance";

    /// <summary>The queue a reconcile lands on when the dispatcher has no route for its type.</summary>
    private const string DefaultQueue = "default";

    private readonly PostgresTenancyFixture _fixture;

    public ReconcileDeadlineRearmTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ------------------------------------------------------- an attempt without a verdict is what re-arms

    [Fact]
    public async Task Sweep_DeadLetteredDeadlineTask_QueuesTheNextAttempt_AndTheRunReachesIncomplete()
    {
        var clock = AlignedClock();
        await using var world = await StartAsync(clock);
        var runId = world.RunId;

        await MarkOverdueAsync(world, runId);

        await SweepAsync(world, clock);
        (await DeadlineRowsAsync(world, runId)).ShouldBe(1, customMessage: "the first fire queues the first attempt");

        await DispatchAsync(world);
        await DeadLetterAttemptAsync(world, runId);

        await SweepAsync(world, clock);

        (await DeadlineRowsAsync(world, runId)).ShouldBe(
            2,
            customMessage: "the attempt dead-lettered, so the run is still overdue and the sweep spends the next attempt");
        (await AttemptRowsAsync(world, runId)).ShouldBe(1, customMessage: "exactly one re-arm, never two in a fire");
        (await ReArmKeyAsync(world, runId, 2)).ShouldBe(1, customMessage: "the re-arm is the second attempt's key");
        (await OutboxOrgAsync(world, runId, 2)).ShouldBe(
            world.Org,
            customMessage: "the re-arm lands in the run's own tenant, like the first attempt");

        await DispatchAndReconcileAsync(world);

        (await StateAsync(world, runId)).ShouldBe(
            "incomplete",
            customMessage: "the re-armed attempt decides the run instead of leaving it collecting for ever");
        (await MissingAsync(world, runId)).ShouldContain(
            "missing_shard",
            customMessage: "the open shard is the missing producer, so missing evidence never becomes a pass");
        (await RevisionCountAsync(world)).ShouldBe(1, customMessage: "one attempt decided the run, so one revision");
    }

    [Fact]
    public async Task Sweep_EveryAttemptDeadLettered_QueuesNothingMore()
    {
        var clock = AlignedClock();
        var sink = new IngestLogSink();
        await using var world = await StartAsync(clock, sink);
        var runId = world.RunId;

        await MarkOverdueAsync(world, runId);

        for (var attempt = 1; attempt <= RunReconcileContract.DeadlineMaxAttempts; attempt++)
        {
            await SweepAsync(world, clock);
            (await DeadlineRowsAsync(world, runId)).ShouldBe(
                attempt,
                customMessage: $"attempt {attempt} is queued once the attempt before it dead-lettered");

            await DispatchAsync(world);
            await DeadLetterAttemptAsync(world, runId);
        }

        sink.Clear();
        await SweepAsync(world, clock);

        (await DeadlineRowsAsync(world, runId)).ShouldBe(
            RunReconcileContract.DeadlineMaxAttempts,
            customMessage: "a poison run stops at its bound instead of queueing a trigger on every fire");
        (await CountQueueRowsAsync(world, runId)).ShouldBe(
            RunReconcileContract.DeadlineMaxAttempts,
            customMessage: "no fourth task row reached the queue either");
        (await StateAsync(world, runId)).ShouldBe(
            "collecting",
            customMessage: "the sweep queues attempts, it never decides a run itself");

        var warnings = sink.Lines
            .Where(line => line.Contains("overdue run(s) alone", StringComparison.Ordinal))
            .ToList();
        warnings.ShouldHaveSingleItem().ShouldContain(
            $"{RunReconcileContract.DeadlineMaxAttempts} collection-deadline attempt(s)",
            customMessage: "one warning per fire names the bound that stopped the run");
    }

    [Fact]
    public async Task Sweep_OneDeadlineTaskStillQueued_ReArmsOnlyTheDeadLetteredRun()
    {
        var clock = AlignedClock();
        await using var world = await StartAsync(clock);
        var deadLettered = world.RunId;
        var stillQueued = (await world.SeedOtherRunAsync(world.Org)).RunId;

        await MarkOverdueAsync(world, deadLettered);
        await MarkOverdueAsync(world, stillQueued);

        await SweepAsync(world, clock);
        (await DeadlineRowsAsync(world, deadLettered)).ShouldBe(1);
        (await DeadlineRowsAsync(world, stillQueued)).ShouldBe(1);

        await DispatchAsync(world);
        var claims = await ClaimReconcilesAsync(world, expected: 2);
        foreach (var claim in claims)
        {
            if (RunIdOf(claim) == deadLettered)
            {
                await ForceDeadLetterAsync(world, claim);
            }
            else
            {
                // The other attempt is still pending work: released back to the queue, not failed.
                (await world.Host.Services.GetRequiredService<ITaskLeaseWriter>()
                        .ReleaseAsync(claim, Token))
                    .ShouldBeTrue(customMessage: "the control attempt goes back to queued, unspent");
            }
        }

        await SweepAsync(world, clock);

        (await DeadlineRowsAsync(world, deadLettered)).ShouldBe(
            2,
            customMessage: "the dead letter spent this run's first attempt, so it gets a second");
        (await DeadlineRowsAsync(world, stillQueued)).ShouldBe(
            1,
            customMessage: "a pending attempt is still work in progress, so the sweep must not spend a second one");
        (await ReArmKeyAsync(world, stillQueued, 2)).ShouldBe(0);
    }

    // ------------------------------------------------------- done and purged attempts are spent too

    [Fact]
    public async Task Sweep_AttemptThatFinishedDone_IsReArmedUpToTheBound()
    {
        var clock = AlignedClock();
        var sink = new IngestLogSink();
        await using var world = await StartAsync(clock, sink);
        var runId = world.RunId;

        await MarkOverdueAsync(world, runId);

        // An attempt can finish without deciding anything — an app that answered "nothing to decide" for this run,
        // or a no-op that lost the race — and the run then stays overdue. Each of those is spent, not pending.
        for (var attempt = 1; attempt <= RunReconcileContract.DeadlineMaxAttempts; attempt++)
        {
            await SweepAsync(world, clock);
            (await DeadlineRowsAsync(world, runId)).ShouldBe(
                attempt,
                customMessage: $"attempt {attempt} is queued once the attempt before it finished without a verdict");

            await DispatchAsync(world);
            var claim = (await ClaimReconcilesAsync(world, expected: 1)).Single();
            (await world.Host.Services.GetRequiredService<ITaskLeaseWriter>().MarkDoneAsync(claim, Token))
                .ShouldBeTrue(customMessage: "the attempt ran and wrote nothing the run could be decided by");
        }

        (await StateAsync(world, runId)).ShouldBe(
            "collecting",
            customMessage: "a done attempt left the run open, so the sweep had to spend the next attempt");

        sink.Clear();
        await SweepAsync(world, clock);

        (await DeadlineRowsAsync(world, runId)).ShouldBe(
            RunReconcileContract.DeadlineMaxAttempts,
            customMessage: "done attempts are bounded too: the run stops at the attempt bound, not at the first done");
        sink.Lines.ShouldContain(
            line => line.Contains("overdue run(s) alone", StringComparison.Ordinal),
            customMessage: "the fire names the run that spent its attempts without a verdict");
    }

    [Fact]
    public async Task Sweep_PurgedAttemptTask_IsReArmed()
    {
        var clock = AlignedClock();
        await using var world = await StartAsync(clock);
        var runId = world.RunId;

        await MarkOverdueAsync(world, runId);

        await SweepAsync(world, clock);
        await DispatchAsync(world);

        // Retention purges finished tasks and leaves the outbox row behind: the trigger still spent a slot, and the
        // left join then has nothing to read a state from.
        var claim = (await ClaimReconcilesAsync(world, expected: 1)).Single();
        (await world.Host.Services.GetRequiredService<ITaskLeaseWriter>().MarkDoneAsync(claim, Token)).ShouldBeTrue();
        await _fixture.ExecuteAsync(world.Database, $"delete from public.task where id = '{claim.Id:D}'");

        await SweepAsync(world, clock);

        (await DeadlineRowsAsync(world, runId)).ShouldBe(
            2,
            customMessage: "a dispatched attempt with no task row left is spent, so the sweep re-arms the run");
        (await ReArmKeyAsync(world, runId, 2)).ShouldBe(1, customMessage: "the re-arm is the second attempt's key");
    }

    [Fact]
    public async Task Sweep_MixedSpentAttempts_StopAtTheBound()
    {
        var clock = AlignedClock();
        var sink = new IngestLogSink();
        await using var world = await StartAsync(clock, sink);
        var runId = world.RunId;

        await MarkOverdueAsync(world, runId);

        // One attempt of each kind of spent: a dead letter, a completion without a verdict, and a task purged after
        // dispatch. The chain must advance through all three and then stop.
        await SweepAsync(world, clock);
        await DispatchAsync(world);
        await DeadLetterAttemptAsync(world, runId);

        await SweepAsync(world, clock);
        await DispatchAsync(world);
        var second = (await ClaimReconcilesAsync(world, expected: 1)).Single();
        (await world.Host.Services.GetRequiredService<ITaskLeaseWriter>().MarkDoneAsync(second, Token)).ShouldBeTrue();
        await _fixture.ExecuteAsync(world.Database, $"delete from public.task where id = '{second.Id:D}'");

        await SweepAsync(world, clock);
        await DispatchAsync(world);
        var third = (await ClaimReconcilesAsync(world, expected: 1)).Single();
        (await world.Host.Services.GetRequiredService<ITaskLeaseWriter>().MarkDoneAsync(third, Token)).ShouldBeTrue();

        sink.Clear();
        await SweepAsync(world, clock);

        (await DeadlineRowsAsync(world, runId)).ShouldBe(
            RunReconcileContract.DeadlineMaxAttempts,
            customMessage: "dead letter, done and purged all count as spent, so the bound holds across the mix");
        (await CountQueueRowsAsync(world, runId)).ShouldBe(
            RunReconcileContract.DeadlineMaxAttempts - 1,
            customMessage: "no fourth task row reached the queue either, and the purged one is gone from it");
        (await StateAsync(world, runId)).ShouldBe(
            "collecting",
            customMessage: "the sweep queues attempts, it never decides a run itself");
        sink.Lines.ShouldContain(
            line => line.Contains("overdue run(s) alone", StringComparison.Ordinal),
            customMessage: "one warning names the run that spent its attempts");
    }

    // ------------------------------------------------------------------ world and driving

    /// <summary>
    /// One migrated database with an org, a project, a run and one producer slot, composed with the production queue
    /// mechanism and a <see cref="FakeTimeProvider"/> so a case drives the schedule instead of waiting for it.
    /// </summary>
    private async Task<IngestTestWorld> StartAsync(FakeTimeProvider clock, IngestLogSink? sink = null)
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

                if (sink is not null)
                {
                    services.AddLogging(logging => logging.AddProvider(sink));
                }

                // The fixture mints no login for evidgate_worker_sched and the database name is only known once the
                // world has started: the double opens a superuser connection and switches to the sweep role, the way
                // the sweep suite exercises the same role.
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

    /// <summary>A fixed, cron-aligned instant, so each advance lands on the next ten-minute mark.</summary>
    private static FakeTimeProvider AlignedClock() =>
        new(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>Advances to the next tick, fires the schedule, and runs this fire's sweep handler to done.</summary>
    private static async Task SweepAsync(IngestTestWorld world, FakeTimeProvider clock)
    {
        clock.Advance(TimeSpan.FromMinutes(10));

        (await world.Host.Services.GetRequiredService<TaskScheduleRunner>().RunOnceAsync(clock.GetUtcNow(), Token))
            .ShouldBeGreaterThan(0, customMessage: "the schedule pass fires the registered sweeps");

        var completed = await CountDoneAsync(world, RunReconcileContract.DeadlineSweepTaskType);
        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();
        for (var attempt = 0; attempt < 10; attempt++)
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

    /// <summary>Moves the outbox rows into the task queue, which is what a reconcile attempt needs to exist.</summary>
    private static async Task DispatchAsync(IngestTestWorld world)
    {
        await world.Host.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(Token);
    }

    /// <summary>
    /// Spends the run's deadline attempt the way a crashing consumer does: the attempt is at its cap and its lease
    /// has run out, so the queue's own reclaim — not a writer call — takes it to <c>dead_letter</c>, leaving the run
    /// untouched exactly as a failing attempt would.
    /// </summary>
    private async Task DeadLetterAttemptAsync(IngestTestWorld world, Guid runId)
    {
        var claims = await ClaimReconcilesAsync(world, expected: 1);
        var claim = claims.Single(row => RunIdOf(row) == runId);

        await ForceDeadLetterAsync(world, claim);
    }

    /// <summary>
    /// Drives one claimed attempt through retry exhaustion: one try allowed and already spent, lease expired, then the
    /// reclaim that gives up at the cap. Writing those two columns is the only shortcut; the transition stays the
    /// queue's.
    /// </summary>
    private async Task ForceDeadLetterAsync(IngestTestWorld world, TaskClaim claim)
    {
        await _fixture.ExecuteAsync(world.Database, $"""
            update public.task
               set max_attempts = 1,
                   locked_until = now() - interval '1 second'
             where id = '{claim.Id:D}'
            """);

        // The return value counts rows requeued with attempts left, not the ones that gave up, so the evidence is
        // the row itself.
        await world.Host.Services.GetRequiredService<ITaskClaimer>().ReclaimExpiredAsync(DefaultQueue, Token);

        (await TaskRowAsync(world, claim.Id)).ShouldBe(
            "dead_letter 1/1",
            customMessage: "the attempt is at its cap and its lease expired, so the reclaim dead-letters it");
    }

    /// <summary>One persisted task row, as <c>state attempts/max_attempts</c>.</summary>
    private static async Task<string> TaskRowAsync(IngestTestWorld world, Guid taskId) =>
        await world.ScalarAsync<string>(
            $"select state || ' ' || attempts || '/' || max_attempts from public.task where id = '{taskId:D}'");

    /// <summary>Claims every due reconcile on the default queue, asserting the batch holds what the case expects.</summary>
    private static async Task<IReadOnlyList<TaskClaim>> ClaimReconcilesAsync(IngestTestWorld world, int expected)
    {
        var claims = await world.Host.Services.GetRequiredService<ITaskClaimer>()
            .ClaimAsync(DefaultQueue, [RunReconcileContract.OutboxTaskType], expected, Token);

        claims.Count.ShouldBe(expected, customMessage: "the queue holds exactly the attempts the case queued");
        return claims;
    }

    /// <summary>Dispatches, then claims on the reconcile queue until the attempt ran and completed.</summary>
    private static async Task DispatchAndReconcileAsync(IngestTestWorld world)
    {
        await DispatchAsync(world);

        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (await CountDoneAsync(world, RunReconcileContract.OutboxTaskType) >= 1)
            {
                return;
            }

            await consumer.PollOnceAsync(DefaultQueue, Token);
        }

        (await CountDoneAsync(world, RunReconcileContract.OutboxTaskType)).ShouldBeGreaterThanOrEqualTo(
            1,
            customMessage: "the re-armed reconcile is claimed and completed by the consumer");
    }

    /// <summary>The run a claim's payload names: a reconcile payload always carries its run id.</summary>
    private static Guid RunIdOf(TaskClaim claim)
    {
        using var payload = JsonDocument.Parse(claim.Payload);
        return payload.RootElement.GetProperty("run_id").GetGuid();
    }

    private static Task<long> CountDoneAsync(IngestTestWorld world, string taskType) =>
        world.CountAsync("task", where: $"task_type = '{taskType}' and state = 'done'");

    /// <summary>How many reconcile task rows the queue holds, whatever their state.</summary>
    private static Task<long> CountQueueRowsAsync(IngestTestWorld world, Guid runId) =>
        world.CountAsync(
            "task",
            where: $"task_type = '{RunReconcileContract.OutboxTaskType}' and payload ->> 'run_id' = '{runId:D}'");

    private static Task<long> DeadlineRowsAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<long>(
            "select count(*) from public.outbox where idempotency_key like "
            + $"'orchestrator:run:{runId:D}:reconcile:deadline%'");

    /// <summary>How many outbox rows carry a re-arm key (anything past the first attempt).</summary>
    private static Task<long> AttemptRowsAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<long>(
            "select count(*) from public.outbox where idempotency_key like "
            + $"'orchestrator:run:{runId:D}:reconcile:deadline:%'");

    private static Task<long> ReArmKeyAsync(IngestTestWorld world, Guid runId, int attempt) =>
        world.ScalarAsync<long>(
            "select count(*) from public.outbox where idempotency_key = "
            + $"'{RunReconcileContract.DeadlineIdempotencyKey(runId, attempt)}'");

    private static Task<Guid?> OutboxOrgAsync(IngestTestWorld world, Guid runId, int attempt) =>
        world.ScalarAsync<Guid?>(
            "select org_id from public.outbox where idempotency_key = "
            + $"'{RunReconcileContract.DeadlineIdempotencyKey(runId, attempt)}'");

    private static Task<string> StateAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<string>($"select state from public.run where id = '{runId:D}'");

    private static Task<string> MissingAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<string>(
            "select coalesce(array_to_string(missing, ','), '') from public.evidence_revision "
            + $"where org_id = '{world.Org}' and run_id = '{runId:D}' order by revision_no desc limit 1");

    private static Task<long> RevisionCountAsync(IngestTestWorld world) => world.CountAsync("evidence_revision");

    /// <summary>Puts one run in the state the scan names — collectable and past its deadline — in one statement.</summary>
    private Task MarkOverdueAsync(IngestTestWorld world, Guid runId) =>
        _fixture.ExecuteAsync(
            world.Database,
            "update public.run set state = 'collecting', collection_deadline_at = now() - interval '1 minute' "
            + $"where id = '{runId:D}'");
}
