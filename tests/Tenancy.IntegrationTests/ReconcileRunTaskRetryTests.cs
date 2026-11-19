// Crash-and-retry half of the reconcile suite, a partial file of ReconcileRunTests so it reuses that suite's worlds and
// count helpers. These cases go through the real queue (claim, handler, commit, acknowledge) and break it at the two
// points where an at-least-once consumer can lose track of an attempt.
using System.Net;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReconcileRunTests
{
    /// <summary>
    /// Window (a): the attempt wrote its revision, pointer and frozen event and then died before the commit. Nothing
    /// survives, the row is retried, and the retry freezes the run exactly once.
    /// </summary>
    [Fact]
    public async Task ReconcileRunTask_CrashBeforeCommit_RetryWritesOneRevision()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        var faults = new RetryFaults { FailFinishes = 1 };
        await using var world = await StartRetryWorldAsync(faults, owned.Name);
        var taskId = await SeedCollectedRunTaskAsync(world);
        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();

        (await consumer.PollOnceAsync(ReconcileQueue, Token)).ShouldBe(1, "the claim ran the handler once");

        faults.FinishCalls.ShouldBe(1, "the finisher ran to its end inside the attempt before the crash");
        var ledger = await LedgerAsync(world);
        ledger.Revisions.ShouldBe(0, "the rolled-back attempt leaves no revision");
        ledger.FreezeRows.ShouldBe(0, "and no frozen event");
        (await PointerAsync(world, world.RunId)).ShouldBeNull("and no pointer");
        (await StateAsync(world, world.RunId)).ShouldBe("collecting", "the state edge rolled back too");
        (await TaskStateAsync(world, taskId)).ShouldBe("retry", "the failed attempt is queued for another try");
        (await TaskAttemptsAsync(world, taskId)).ShouldBe(1);

        await MakeTaskDueAsync(world, taskId);
        (await consumer.PollOnceAsync(ReconcileQueue, Token)).ShouldBe(1, "the retried row is claimable again");

        await AssertSingleRevisionAsync(world);
        (await TaskStateAsync(world, taskId)).ShouldBe("done", "the retry is acknowledged");
        (await TaskAttemptsAsync(world, taskId)).ShouldBe(2);
        world.Host.Services.GetRequiredService<ReconcileCallCounter>().Calls.ShouldBe(2);
    }

    /// <summary>
    /// Window (b): the attempt committed but the consumer never acknowledged. The lease expires, the row is
    /// redelivered, and the replay finds the run settled and writes nothing.
    /// </summary>
    [Fact]
    public async Task ReconcileRunTask_CommittedButNotAcknowledged_RedeliveryWritesNothing()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        var faults = new RetryFaults { FailAcks = 1 };
        await using var world = await StartRetryWorldAsync(faults, owned.Name);
        var taskId = await SeedCollectedRunTaskAsync(world);
        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();

        (await consumer.PollOnceAsync(ReconcileQueue, Token)).ShouldBe(1);

        var committed = await AssertSingleRevisionAsync(world);
        var row = await RevisionRowAsync(world, world.RunId, 1);
        row.ShouldNotBeNullOrEmpty("the snapshot must exist or the later equality proves nothing");
        (await TaskStateAsync(world, taskId)).ShouldBe("active", "the acknowledgement failed, so the lease is still held");

        await ExpireTaskLeaseAsync(world, taskId);
        (await consumer.PollOnceAsync(ReconcileQueue, Token)).ShouldBe(1, "the expired lease is reclaimed and redelivered");

        (await AssertSingleRevisionAsync(world)).ShouldBe(committed, "the redelivery names the same revision");
        (await RevisionRowAsync(world, world.RunId, 1)).ShouldBe(row, "the committed revision is untouched");
        (await TaskStateAsync(world, taskId)).ShouldBe("done", "the redelivery is acknowledged");
        (await TaskAttemptsAsync(world, taskId)).ShouldBe(2);
        faults.FinishCalls.ShouldBe(1, "the redelivery stopped at the reconciler and never reached the finisher");
        world.Host.Services.GetRequiredService<ReconcileCallCounter>().Calls
            .ShouldBe(2, "the handler really ran again, it did not just skip");
    }

    /// <summary>Both windows on one run, in sequence: crash, retry that commits, lost acknowledgement, redelivery.</summary>
    [Fact]
    public async Task ReconcileRunTask_CrashRetried_NoDuplicateRevision()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        var faults = new RetryFaults { FailFinishes = 1, FailAcks = 1 };
        await using var world = await StartRetryWorldAsync(faults, owned.Name);
        var taskId = await SeedCollectedRunTaskAsync(world);
        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();

        (await consumer.PollOnceAsync(ReconcileQueue, Token)).ShouldBe(1);
        (await LedgerAsync(world)).Revisions.ShouldBe(0, "window (a): the crash wrote nothing");

        await MakeTaskDueAsync(world, taskId);
        (await consumer.PollOnceAsync(ReconcileQueue, Token)).ShouldBe(1);
        var committed = await AssertSingleRevisionAsync(world);
        (await TaskStateAsync(world, taskId)).ShouldBe("active", "window (b): committed, not acknowledged");

        await ExpireTaskLeaseAsync(world, taskId);
        (await consumer.PollOnceAsync(ReconcileQueue, Token)).ShouldBe(1);

        (await AssertSingleRevisionAsync(world)).ShouldBe(committed, "the redelivery leaves the one revision alone");
        (await TaskStateAsync(world, taskId)).ShouldBe("done");
        (await TaskAttemptsAsync(world, taskId)).ShouldBe(3, "one crash, one lost acknowledgement, one clean pass");
        faults.FinishCalls.ShouldBe(2, "the redelivery stopped at the reconciler and never reached the finisher");
        world.Host.Services.GetRequiredService<ReconcileCallCounter>().Calls.ShouldBe(3);
    }

    /// <summary>
    /// Positive control: the counting queries do see a second revision when one legitimately exists (late evidence),
    /// so the "exactly one" assertions above are not satisfied by a query that can only ever answer one.
    /// </summary>
    [Fact]
    public async Task ReconcileRunTask_LateEvidence_LedgerCountsASecondRevision()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartRetryWorldAsync(new RetryFaults(), owned.Name, attemptsSkip: 1);
        var (runId, _) = await StartIncompleteRunAsync(world);
        await OpenLateWindowAsync(world, runId);
        (await LedgerAsync(world)).Revisions.ShouldBe(1);

        var late = await IngestQaRequests.PostEventsAsync(
            world.Client,
            runId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(),
                world.Plan.Executions[^1].ExecutionKey,
                sequence: 2)));
        late.Status.ShouldBe(HttpStatusCode.OK, late.ErrorMessage);
        await DispatchAndReconcileAsync(world, expectedReconciles: 1);

        var ledger = await LedgerAsync(world);
        ledger.Revisions.ShouldBe(2);
        ledger.FreezeRows.ShouldBe(2);
        ledger.PointerIsLatest.ShouldBeTrue();
        ledger.FreezeRowsOfLatest.ShouldBe(1, "each revision announces itself once, so the count can tell two revisions from one");
    }

    // ------------------------------------------------------------------ helpers: the faulted queue world

    /// <summary>The queue world of <see cref="StartWithQueueAsync"/> with the finisher and the acknowledgement breakable.</summary>
    private async Task<IngestTestWorld> StartRetryWorldAsync(RetryFaults faults, string database, int attemptsSkip = 0)
    {
        var world = await IngestTestWorld.StartAsync(
            _fixture,
            IngestTestWorld.Writer(),
            slots: 1,
            database: database,
            services: services =>
            {
                services.AddEvidgateTasks(new ConfigurationBuilder().Build());
                services.AddSingleton<ReconcileCallCounter>();
                services.RemoveAll<IRunReconciler>();
                services.AddScoped<IRunReconciler>(provider => new CountingRunReconciler(
                    new RunReconciler(
                        provider.GetRequiredService<ITenantDb>(),
                        provider.GetRequiredService<ITenantScopeAccessor>()),
                    provider.GetRequiredService<ReconcileCallCounter>()));
                services.AddScoped<ReconcileFinisher>();
                services.RemoveAll<IReconcileFinisher>();
                services.AddScoped<IReconcileFinisher>(provider =>
                    new CrashingFinisher(provider.GetRequiredService<ReconcileFinisher>(), faults));
                services.RemoveAll<ITaskLeaseWriter>();
                services.AddSingleton<ITaskLeaseWriter>(provider =>
                    new LostAckLeaseWriter(ActivatorUtilities.CreateInstance<TaskLeaseWriter>(provider), faults));
            });

        return await AnswerPlanOrDisposeAsync(world, attemptsSkip);
    }

    /// <summary>A normal run whose collection is whole (one shard, three matching events) and its one queued trigger.</summary>
    private async Task<Guid> SeedCollectedRunTaskAsync(IngestTestWorld world)
    {
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 3, declaredCount: 3);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(Producer, 1, "test.finished", "passed"),
            new StoredEvent(Producer, 2, "test.finished", "passed"),
            new StoredEvent(Producer, 3, "test.finished", "passed"));

        var taskId = await world.Host.Services.GetRequiredService<ITaskQueueWriter>().EnqueueAsync(
            new TaskEnqueue
            {
                Queue = ReconcileQueue,
                TaskType = RunReconcileContract.OutboxTaskType,
                PayloadJson = RunReconcileContract.PayloadJson(runId, Producer, declaredLastSequence: 3, declaredCount: 3),
                IdempotencyKey = RunReconcileContract.IdempotencyKey(runId, Producer),
                OrgId = world.Org,
            },
            Token);

        return taskId.ShouldNotBeNull("the trigger is one new task row");
    }

    /// <summary>
    /// What a run owns after the attempts: its revisions, the pointer, and the frozen events that name it.
    /// PointerIsLatest is true with no revision only when the run has no pointer either.
    /// </summary>
    private sealed record RevisionLedger(long Revisions, bool PointerIsLatest, long FreezeRows, long FreezeRowsOfLatest);

    private static async Task<RevisionLedger> LedgerAsync(IngestTestWorld world)
    {
        var runId = world.RunId;
        var revisions = await RunRevisionCountAsync(world, runId);
        if (revisions == 0)
        {
            return new RevisionLedger(0, await PointerAsync(world, runId) is null, await FrozenEventRowsOfRunAsync(world, runId), 0);
        }

        var latest = await RevisionSummaryAsync(world, runId);

        return new RevisionLedger(
            revisions,
            await PointerAsync(world, runId) == latest.Id,
            await FrozenEventRowsOfRunAsync(world, runId),
            await FrozenEventRowsAsync(world, latest.Id));
    }

    /// <summary>The settled state: one revision, the pointer names it, one frozen event, the run complete. Returns its id.</summary>
    private static async Task<Guid> AssertSingleRevisionAsync(IngestTestWorld world)
    {
        var ledger = await LedgerAsync(world);
        ledger.Revisions.ShouldBe(1, "exactly one revision");
        ledger.PointerIsLatest.ShouldBeTrue("the run's pointer names it");
        ledger.FreezeRows.ShouldBe(1, "exactly one frozen event for the run");
        ledger.FreezeRowsOfLatest.ShouldBe(1, "and it names that revision");
        (await StateAsync(world, world.RunId)).ShouldBe("complete");

        return (await RevisionSummaryAsync(world, world.RunId)).Id;
    }

    private static Task<string> TaskStateAsync(IngestTestWorld world, Guid taskId) =>
        world.ScalarAsync<string>($"select state from public.task where id = '{taskId:D}'");

    private static Task<int> TaskAttemptsAsync(IngestTestWorld world, Guid taskId) =>
        world.ScalarAsync<int>($"select attempts from public.task where id = '{taskId:D}'");

    private static Task MakeTaskDueAsync(IngestTestWorld world, Guid taskId) =>
        world.Fixture.ExecuteAsync(
            world.Database,
            $"update public.task set available_at = now() where id = '{taskId:D}' and state = 'retry'");

    private static Task ExpireTaskLeaseAsync(IngestTestWorld world, Guid taskId) =>
        world.Fixture.ExecuteAsync(
            world.Database,
            $"update public.task set locked_until = now() - interval '1 second' where id = '{taskId:D}' and state = 'active'");

    /// <summary>How many of each fault is left to inject, and how often the finisher really ran.</summary>
    private sealed class RetryFaults
    {
        private int _failFinishes;
        private int _failAcks;
        private int _finishCalls;

        public int FailFinishes { set => _failFinishes = value; }

        public int FailAcks { set => _failAcks = value; }

        public int FinishCalls => Volatile.Read(ref _finishCalls);

        public void RecordFinish() => Interlocked.Increment(ref _finishCalls);

        public bool TakeFinishFailure() => Interlocked.Decrement(ref _failFinishes) >= 0;

        public bool TakeAckFailure() => Interlocked.Decrement(ref _failAcks) >= 0;
    }

    /// <summary>The real finisher, then a crash: the revision, pointer and event exist only in the unfinished transaction.</summary>
    private sealed class CrashingFinisher(IReconcileFinisher inner, RetryFaults faults) : IReconcileFinisher
    {
        public async ValueTask<ReconcileFinalization> FinishAsync(
            Guid runId,
            ReconcileComparison comparison,
            ReconcileSettlement settlement,
            CancellationToken cancellationToken)
        {
            var finalization = await inner.FinishAsync(runId, comparison, settlement, cancellationToken);
            faults.RecordFinish();
            if (faults.TakeFinishFailure())
            {
                throw new InvalidOperationException("injected crash after the revision insert, before the commit");
            }

            return finalization;
        }
    }

    /// <summary>The real lease writer whose completion fails: the work is committed and the row never says so.</summary>
    private sealed class LostAckLeaseWriter(ITaskLeaseWriter inner, RetryFaults faults) : ITaskLeaseWriter
    {
        public Task<bool> MarkDoneAsync(TaskClaim claim, CancellationToken cancellationToken = default) =>
            faults.TakeAckFailure()
                ? throw new InvalidOperationException("injected loss of the acknowledgement")
                : inner.MarkDoneAsync(claim, cancellationToken);

        public Task<bool> MarkRetryAsync(
            TaskClaim claim, TimeSpan backoff, string lastError, CancellationToken cancellationToken = default) =>
            inner.MarkRetryAsync(claim, backoff, lastError, cancellationToken);

        public Task<bool> ReleaseAsync(TaskClaim claim, CancellationToken cancellationToken = default) =>
            inner.ReleaseAsync(claim, cancellationToken);

        public Task<bool> DeferAsync(
            TaskClaim claim, TimeSpan delay, string lastError, CancellationToken cancellationToken = default) =>
            inner.DeferAsync(claim, delay, lastError, cancellationToken);

        public Task<bool> MarkDeadLetterAsync(
            TaskClaim claim, string lastError, CancellationToken cancellationToken = default) =>
            inner.MarkDeadLetterAsync(claim, lastError, cancellationToken);
    }
}
