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

    /// <summary>
    /// Drives the ingest outbox and the consumer until no task of this tenant is claimable, so a refusal that enqueued
    /// anything would be caught a moment later instead of passing because nothing was polled yet.
    /// </summary>
    private static async Task DrainLateEvaluationQueueAsync(IngestTestWorld world)
    {
        var dispatcher = world.Host.Services.GetRequiredService<OutboxDispatcher>();
        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();
        for (var poll = 0; poll < 12; poll++)
        {
            await dispatcher.DispatchBatchAsync(Token);
            var claimable = await world.ScalarAsync<long>(
                $"select count(*) from public.task where org_id = '{world.Org:D}' "
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
            + "and state in ('queued', 'active', 'retry')"))
            .ShouldBe(0, "the refused or post-cancel evidence left no claimable or waiting work behind");
    }
}
