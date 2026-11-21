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
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed partial class ReconcileDeadlineRearmTests
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
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database);
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
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database, sink);
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
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database);
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
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database, sink);
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
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database);
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
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database, sink);
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
}
