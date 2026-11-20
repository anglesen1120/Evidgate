using System.Net;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReconcileRunTests
{
    [Fact]
    public async Task LateEval_NewRevision_CreatesNextEvaluation_PriorRowIntact()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 1, database: owned.Name);
        await StartEvaluatedIncompleteRunAsync(world);
        var first = await LiveEvaluationSnapshotAsync(world, revision: 1);

        await AppendLateEvaluationAsync(world, revision: 2);

        (await EvaluationRowsAsync(world)).ShouldBe(2);
        (await RevisionCountAsync(world)).ShouldBe(2);
        (await world.ScalarAsync<long>(
            "select count(distinct evidence_revision) from public.evaluation where mode = 'live' "
            + $"and org_id = '{world.Org:D}' and run_id = '{world.RunId:D}'"))
            .ShouldBe(2, "the second revision decides through a live row of its own");
        (await LiveEvaluationSnapshotAsync(world, revision: 1)).ShouldBe(first, "the prior row is byte-identical");
    }

    [Fact]
    public async Task LateEval_EveryLateRevision_ReEvaluated()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 1, database: owned.Name);
        await StartEvaluatedIncompleteRunAsync(world);
        var first = await LiveEvaluationSnapshotAsync(world, revision: 1);
        await AppendLateEvaluationAsync(world, revision: 2);
        (await EvaluationRowsAsync(world)).ShouldBe(2);
        var second = await LiveEvaluationSnapshotAsync(world, revision: 2);

        await AppendLateEvaluationAsync(world, revision: 3);

        (await RevisionCountAsync(world)).ShouldBe(3);
        (await EvaluationRowsAsync(world)).ShouldBe(3);
        (await world.ScalarAsync<string>(
            "select string_agg(evidence_revision::text, ',' order by evidence_revision) "
            + "from public.evaluation where mode = 'live' "
            + $"and org_id = '{world.Org:D}' and run_id = '{world.RunId:D}'"))
            .ShouldBe("1,2,3");
        (await LiveEvaluationSnapshotAsync(world, revision: 1)).ShouldBe(first);
        (await LiveEvaluationSnapshotAsync(world, revision: 2)).ShouldBe(second);
    }

    [Fact]
    public async Task LateEval_FrozenEventRedelivered_ProducesNoSecondLiveEvaluation()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 1, database: owned.Name);
        await EvaluateFirstAndLateRevisionAsync(world);
        (await EvaluationRowsAsync(world)).ShouldBe(2);
        var first = await LiveEvaluationSnapshotAsync(world, revision: 1);
        var second = await LiveEvaluationSnapshotAsync(world, revision: 2);
        var payload = await world.ScalarAsync<string>(
            $"select (payload - '_evidgate_correlation')::text from public.task where org_id = '{world.Org:D}' "
            + $"and task_type = '{EvidenceRevisionFrozenContract.OutboxTaskType}' "
            + "and payload->>'evidence_revision_no' = '2'");
        payload.ShouldNotBeNullOrEmpty("the second revision must have its own frozen task to replay");

        // A fresh key skips queue dedupe, so the handler runs again for evidence that already has its live row.
        var redelivered = await world.Host.Services.GetRequiredService<ITaskQueueWriter>().EnqueueAsync(
            new TaskEnqueue
            {
                Queue = DefaultQueue,
                TaskType = EvidenceRevisionFrozenContract.OutboxTaskType,
                PayloadJson = payload,
                IdempotencyKey = Guid.CreateVersion7().ToString("D"),
                OrgId = world.Org,
            }, Token);
        redelivered.ShouldNotBeNull();
        await DispatchFrozenEvaluationsAsync(world, expectedDone: 3);

        (await FrozenEvaluationTaskCountAsync(world)).ShouldBe(3);
        (await EvaluationRowsAsync(world)).ShouldBe(2);
        (await world.ScalarAsync<long>(
            $"select count(*) from public.evaluation where org_id = '{world.Org:D}' "
            + $"and run_id = '{world.RunId:D}' and mode = 'live' and evidence_revision = 2"))
            .ShouldBe(1);
        (await LiveEvaluationSnapshotAsync(world, revision: 1)).ShouldBe(first);
        (await LiveEvaluationSnapshotAsync(world, revision: 2)).ShouldBe(second);
    }

    [Fact]
    public async Task LateEval_CanceledRun_AfterCancel_NoRevisionNoEvaluation()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var controlOwned = await _fixture.CreateDedicatedDatabaseAsync();

        // The positive control: the same seed shape on a run that is not canceled turns the event into a revision.
        await using var control = await StartWithQueueAsync(attemptsSkip: 1, database: controlOwned.Name);
        await StartEvaluatedIncompleteRunAsync(control);
        await AppendLateEvaluationAsync(control, revision: 2);
        (await RevisionCountAsync(control)).ShouldBe(2);
        (await EvaluationRowsAsync(control)).ShouldBe(2);

        await using var world = await StartWithQueueAsync(attemptsSkip: 1, database: owned.Name);
        await SeedPinnedPolicyAsync(world, world.RunId, "trace");
        await world.SetRunStateAsync("canceled");
        (await RevisionCountAsync(world)).ShouldBe(0);
        (await EvaluationRowsAsync(world)).ShouldBe(0);

        var accepted = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(),
                world.Plan.Executions[^1].ExecutionKey, sequence: 1)));

        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        accepted.Body.GetProperty("accepted").GetInt32().ShouldBe(1);
        (await EventFlagAsync(world, sequence: 1, column: "after_cancel")).ShouldBeTrue();
        (await EventFlagAsync(world, sequence: 1, column: "late")).ShouldBeFalse(
            "post-cancel evidence is stamped as such, never as late evidence worth a revision");
        await DrainLateEvaluationQueueAsync(world);

        (await RevisionCountAsync(world)).ShouldBe(0);
        (await EvaluationRowsAsync(world)).ShouldBe(0);
        (await FrozenEventRowsOfRunAsync(world, world.RunId)).ShouldBe(0);
        (await FrozenEvaluationTaskCountAsync(world)).ShouldBe(0);
        (await ReconcileTaskCountAsync(world))
            .ShouldBe(0, "post-cancel evidence must not enqueue a reconcile task, even one already consumed");
        (await StateAsync(world, world.RunId)).ShouldBe("canceled");
    }

    [Fact]
    public async Task LateEval_AfterWindow_IngestRejects_NoEvaluation()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 1, database: owned.Name);
        await StartEvaluatedIncompleteRunAsync(world);
        var first = await LiveEvaluationSnapshotAsync(world, revision: 1);
        await CloseLateWindowAsync(world, world.RunId);

        // LateEval_NewRevision_CreatesNextEvaluation_PriorRowIntact is this seed with the window still open.
        var refused = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(),
                world.Plan.Executions[^1].ExecutionKey, sequence: 2)));

        refused.Status.ShouldBe(HttpStatusCode.Conflict, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("run_closed");
        await DrainLateEvaluationQueueAsync(world);
        (await RevisionCountAsync(world)).ShouldBe(1);
        (await EvaluationRowsAsync(world)).ShouldBe(1);
        (await LiveEvaluationSnapshotAsync(world, revision: 1)).ShouldBe(first);
        (await FrozenEventRowsOfRunAsync(world, world.RunId)).ShouldBe(1);
        (await FrozenEvaluationTaskCountAsync(world)).ShouldBe(1);
        (await ReconcileTaskCountAsync(world))
            .ShouldBe(0, "refused evidence must not enqueue a reconcile task, even one already consumed");
    }

    [Fact]
    public async Task LateEval_CompleteRun_InsideWindow_SecondLiveEvaluation_FirstRowByteIdentical()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 0, database: owned.Name);
        await StartEvaluatedCompleteRunAsync(world);
        await OpenLateWindowAsync(world, world.RunId);
        var first = await LiveEvaluationSnapshotAsync(world, revision: 1);

        await AppendCompleteLateEvaluationAsync(world);

        (await EvaluationRowsAsync(world)).ShouldBe(2);
        (await RevisionCountAsync(world)).ShouldBe(2);
        (await world.ScalarAsync<long>(
            "select count(distinct evidence_revision) from public.evaluation where mode = 'live' "
            + $"and org_id = '{world.Org:D}' and run_id = '{world.RunId:D}'"))
            .ShouldBe(2, "the second revision decides through a live row of its own");
        var second = await LiveEvaluationSnapshotAsync(world, revision: 2);
        second.ShouldNotBe(first, "a new revision is a new decision");
        (await LiveEvaluationSnapshotAsync(world, revision: 1)).ShouldBe(first, "the prior row is byte-identical");
        (await StateAsync(world, world.RunId)).ShouldBe("complete", "the terminal state never moves for late evidence");
    }

    [Fact]
    public async Task LateEval_CompleteRun_AfterWindow_NoEvaluation()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 0, database: owned.Name);
        await StartEvaluatedCompleteRunAsync(world);
        var first = await LiveEvaluationSnapshotAsync(world, revision: 1);
        var completed = await CompletedAtTextAsync(world, world.RunId);
        await CloseCompleteWindowAsync(world, world.RunId);

        var refused = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(),
                world.Plan.Executions[^1].ExecutionKey, sequence: 4, attemptNo: 2)));

        refused.Status.ShouldBe(HttpStatusCode.Conflict, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("run_closed");
        await DrainLateEvaluationQueueAsync(world);
        (await RevisionCountAsync(world)).ShouldBe(1);
        (await EvaluationRowsAsync(world)).ShouldBe(1);
        (await LiveEvaluationSnapshotAsync(world, revision: 1)).ShouldBe(first);
        (await FrozenEvaluationTaskCountAsync(world)).ShouldBe(1);
        (await ReconcileTaskCountAsync(world)).ShouldBe(0, "refused evidence must not enqueue a reconcile task");
        (await StateAsync(world, world.RunId)).ShouldBe("complete");
        (await CompletedAtTextAsync(world, world.RunId)).ShouldBe(completed, "a refusal never moves the anchor");
    }

    // ---------------------------------------------------------------- a complete run's three ordinary entry points

    /// <summary>
    /// The deadline sweep's scan names only runs that can still take evidence, so a complete run inside its late window
    /// is never swept: the window is what a late batch asks for, never an ordinary scan.
    /// </summary>
    [Fact]
    public async Task Complete_DeadlineSweep_DoesNotNameTheRun()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 0, database: owned.Name);
        await StartEvaluatedCompleteRunAsync(world);
        await OpenLateWindowAsync(world, world.RunId);
        var first = await LiveEvaluationSnapshotAsync(world, revision: 1);
        var completed = await CompletedAtTextAsync(world, world.RunId);

        (await world.ScalarAsync<long>("select count(*) from evidgate.reconcile_overdue_runs(null, null, 1000)"))
            .ShouldBe(0, "the scan names only queued, running and collecting runs");

        (await RevisionCountAsync(world)).ShouldBe(1);
        (await EvaluationRowsAsync(world)).ShouldBe(1);
        (await LiveEvaluationSnapshotAsync(world, revision: 1)).ShouldBe(first);
        (await CompletedAtTextAsync(world, world.RunId)).ShouldBe(completed);
    }

    /// <summary>
    /// At-least-once delivery of an ordinary trigger — a redelivery of the deadline attempt, under a fresh key — finds a
    /// complete run inside its window and still answers as today: no comparison, no revision, no state move.
    /// </summary>
    [Fact]
    public async Task Complete_OldTaskRedelivery_AnswersNotCollectable()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 0, database: owned.Name);
        await StartEvaluatedCompleteRunAsync(world);
        await OpenLateWindowAsync(world, world.RunId);
        var first = await LiveEvaluationSnapshotAsync(world, revision: 1);
        var completed = await CompletedAtTextAsync(world, world.RunId);
        var history = await RunStateEventCountAsync(world, world.RunId);
        var counter = world.Host.Services.GetRequiredService<ReconcileCallCounter>();

        for (var delivery = 1; delivery <= 2; delivery++)
        {
            (await EnqueueOrdinaryReconcileAsync(world)).ShouldNotBeNull();
            await RunQueuedReconcilesAsync(world, expectedDone: delivery);
        }

        counter.Calls.ShouldBe(2, "the handler really ran for each delivery instead of the row being skipped");
        (await RevisionCountAsync(world)).ShouldBe(1);
        (await EvaluationRowsAsync(world)).ShouldBe(1);
        (await LiveEvaluationSnapshotAsync(world, revision: 1)).ShouldBe(first);
        (await StateAsync(world, world.RunId)).ShouldBe("complete");
        (await CompletedAtTextAsync(world, world.RunId)).ShouldBe(completed);
        (await RunStateEventCountAsync(world, world.RunId)).ShouldBe(history, "a non-collectable attempt appends no state history");
    }

    /// <summary>An operator enqueue of the ordinary trigger answers exactly like the redelivery: nothing to compare.</summary>
    [Fact]
    public async Task Complete_ManualEnqueue_AnswersNotCollectable()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 0, database: owned.Name);
        await StartEvaluatedCompleteRunAsync(world);
        await OpenLateWindowAsync(world, world.RunId);
        var first = await LiveEvaluationSnapshotAsync(world, revision: 1);
        var completed = await CompletedAtTextAsync(world, world.RunId);
        var history = await RunStateEventCountAsync(world, world.RunId);

        (await EnqueueOrdinaryReconcileAsync(world)).ShouldNotBeNull();
        await RunQueuedReconcilesAsync(world, expectedDone: 1);

        (await RevisionCountAsync(world)).ShouldBe(1);
        (await EvaluationRowsAsync(world)).ShouldBe(1);
        (await LiveEvaluationSnapshotAsync(world, revision: 1)).ShouldBe(first);
        (await StateAsync(world, world.RunId)).ShouldBe("complete");
        (await CompletedAtTextAsync(world, world.RunId)).ShouldBe(completed);
        (await RunStateEventCountAsync(world, world.RunId)).ShouldBe(history);
    }

    private async Task StartEvaluatedCompleteRunAsync(IngestTestWorld world)
    {
        await SeedPinnedPolicyAsync(world, world.RunId, "trace");
        await StartCompleteRunAsync(world);
        await DispatchFrozenEvaluationsAsync(world, expectedDone: 1);
        (await RevisionCountAsync(world)).ShouldBe(1);
        (await EvaluationRowsAsync(world)).ShouldBe(1);
    }

    /// <summary>The late event of a complete run: a fourth event past the shard's declared three, and a second attempt.</summary>
    private static async Task AppendCompleteLateEvaluationAsync(IngestTestWorld world)
    {
        var accepted = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(),
                world.Plan.Executions[^1].ExecutionKey, sequence: 4, attemptNo: 2)));
        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        accepted.Body.GetProperty("accepted").GetInt32().ShouldBe(1);
        await DispatchAndReconcileAsync(world, expectedReconciles: 1);
        await DispatchFrozenEvaluationsAsync(world, expectedDone: 2);
    }

    /// <summary>A complete run's window is closed by its zero-minute window: the anchor is the completion instant itself.</summary>
    private Task CloseCompleteWindowAsync(IngestTestWorld world, Guid runId) =>
        _fixture.ExecuteAsync(world.Database, $"update public.run set late_window_minutes = 0 where id = '{runId:D}'");

    private static Task<string?> CompletedAtTextAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<string?>($"select completed_at::text from public.run where id = '{runId:D}'");

    private static Task<long> RunStateEventCountAsync(IngestTestWorld world, Guid runId) =>
        world.CountAsync("run_state_event", $"run_id = '{runId:D}'");

    /// <summary>One ordinary (non-late) reconcile task row for the world's run, as an operator enqueue writes it.</summary>
    private static Task<Guid?> EnqueueOrdinaryReconcileAsync(IngestTestWorld world) =>
        world.Host.Services.GetRequiredService<ITaskQueueWriter>().EnqueueAsync(
            new TaskEnqueue
            {
                Queue = DefaultQueue,
                TaskType = RunReconcileContract.OutboxTaskType,
                PayloadJson = RunReconcileContract.DeadlinePayloadJson(world.RunId),
                IdempotencyKey = Guid.CreateVersion7().ToString("D"),
                OrgId = world.Org,
            }, Token);

    /// <summary>Claims queued work until the org's reconcile rows reach <paramref name="expectedDone"/> completed.</summary>
    private static async Task RunQueuedReconcilesAsync(IngestTestWorld world, int expectedDone)
    {
        await world.Host.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(Token);
        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();
        for (var poll = 0; poll < 12; poll++)
        {
            var done = await world.ScalarAsync<long>(
                $"select count(*) from public.task where org_id = '{world.Org:D}' "
                + $"and task_type = '{RunReconcileContract.OutboxTaskType}' and state = 'done'");
            if (done >= expectedDone)
            {
                return;
            }

            await consumer.PollOnceAsync(DefaultQueue, Token);
        }

        (await world.ScalarAsync<long>(
            $"select count(*) from public.task where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunReconcileContract.OutboxTaskType}' and state = 'done'"))
            .ShouldBeGreaterThanOrEqualTo(expectedDone, "the queued reconcile ran");
    }

    private async Task StartEvaluatedIncompleteRunAsync(IngestTestWorld world)
    {
        await SeedPinnedPolicyAsync(world, world.RunId, "trace");
        await StartIncompleteRunAsync(world);
        await OpenLateWindowAsync(world, world.RunId);
        await DispatchFrozenEvaluationsAsync(world, expectedDone: 1);
        (await RevisionCountAsync(world)).ShouldBe(1);
        (await EvaluationRowsAsync(world)).ShouldBe(1);
    }

    /// <summary>
    /// The late event of revision N carries attempt N-1 of the last planned execution: attempt 1 is the evidence revision 2
    /// counts, and each later attempt is new evidence the same way a retry is, so the watermark moves and a revision follows.
    /// </summary>
    private static async Task AppendLateEvaluationAsync(IngestTestWorld world, int revision)
    {
        var accepted = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(),
                world.Plan.Executions[^1].ExecutionKey, sequence: revision, attemptNo: revision - 1)));
        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        accepted.Body.GetProperty("accepted").GetInt32().ShouldBe(1);
        await DispatchAndReconcileAsync(world, expectedReconciles: revision - 1);
        await DispatchFrozenEvaluationsAsync(world, expectedDone: revision);
    }

    private static async Task<string> LiveEvaluationSnapshotAsync(IngestTestWorld world, int revision)
    {
        // Whole-row JSON carries every persisted column, so a column added later cannot weaken the immutability assertion.
        var snapshot = await world.ScalarAsync<string>(
            "select to_jsonb(e)::text from public.evaluation e where e.mode = 'live' "
            + $"and e.org_id = '{world.Org:D}' and e.run_id = '{world.RunId:D}' "
            + $"and e.evidence_revision = {revision}");
        snapshot.ShouldNotBeNullOrEmpty("the historical row must exist before equality can prove it is unchanged");
        return snapshot;
    }

    /// <summary>How many reconcile task rows the tenant holds, whatever state they reached.</summary>
    private static Task<long> ReconcileTaskCountAsync(IngestTestWorld world) =>
        world.ScalarAsync<long>(
            $"select count(*) from public.task where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunReconcileContract.OutboxTaskType}'");

    /// <summary>
    /// Drives handlers that could turn rejected evidence into a revision or evaluation. Only the two types these flows
    /// can produce are counted: the baseline run's state-change notification has no handler, so its row stays queued
    /// forever and waiting on it would time the drain out on work that can never run.
    /// </summary>
    private static async Task DrainLateEvaluationQueueAsync(IngestTestWorld world)
    {
        var dispatcher = world.Host.Services.GetRequiredService<OutboxDispatcher>();
        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();
        var handled = $"'{RunReconcileContract.OutboxTaskType}', '{EvidenceRevisionFrozenContract.OutboxTaskType}'";
        for (var poll = 0; poll < 12; poll++)
        {
            await dispatcher.DispatchBatchAsync(Token);
            var claimable = await world.ScalarAsync<long>(
                $"select count(*) from public.task where org_id = '{world.Org:D}' "
                + $"and task_type in ({handled}) "
                + $"and queue = '{DefaultQueue}' and (state = 'active' "
                + "or (state in ('queued', 'retry') and available_at <= now()))");
            if (claimable == 0)
            {
                break;
            }

            await consumer.PollOnceAsync(DefaultQueue, Token);
        }

        (await world.ScalarAsync<long>(
            $"select count(*) from public.task where org_id = '{world.Org:D}' "
            + $"and task_type in ({handled}) "
            + "and state in ('queued', 'active', 'retry')"))
            .ShouldBe(0, "the refused or post-cancel evidence left no claimable handler work waiting");
    }
}
