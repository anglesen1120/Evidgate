// Late-evidence and epoch-flag half of the reconcile suite. It is a partial file of ReconcileRunTests, not a
// separate class: every case here drives the same worlds, seeding and read helpers the rest of the suite declares,
// and splitting the class would mean duplicating them or widening their visibility for no gain.
using System.Net;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReconcileRunTests
{
    // ------------------------------------------------------------------ late evidence triggers

    [Fact]
    public async Task Late_Evidence_On_Incomplete_Run_Appends_Revision_Two_And_Leaves_Revision_One_Untouched()
    {
        // The dispatcher and consumer claim rows of every tenant, so these tests keep a database of their own.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 1, database: dedicated.Name);

        var (runId, revisionOne) = await StartIncompleteRunAsync(world);
        await OpenLateWindowAsync(world, runId);

        // The evidence the first revision missed: the declared second event, whose finished status also records the
        // attempt of the planned execution the plan still had unanswered.
        var late = await IngestQaRequests.PostEventsAsync(
            world.Client,
            runId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(),
                world.Plan.Executions[^1].ExecutionKey,
                sequence: 2)));

        late.Status.ShouldBe(HttpStatusCode.OK, late.ErrorMessage);
        late.Body.GetProperty("accepted").GetInt32().ShouldBe(1);
        (await EventFlagAsync(world, 2, "late")).ShouldBeTrue("the run finished, so its evidence is late");
        (await LateTriggerRowsAsync(world, runId)).ShouldBe(
            1,
            "one accepted late batch enqueues one reconcile, in the same transaction as the evidence");

        await DispatchAndReconcileAsync(world, expectedReconciles: 1);

        var latest = await RevisionSummaryAsync(world, runId);
        latest.RevisionNo.ShouldBe(2, "late evidence is counted through a revision of its own");
        latest.Completeness.ShouldBe(
            ReconcileFinisher.Complete,
            "the late event closed the count and the plan's last execution now has an attempt");
        latest.Missing.ShouldBeEmpty();
        (await RevisionRowAsync(world, runId, 1)).ShouldBe(
            revisionOne,
            "the earlier revision is immutable: the late attempt appends, it never rewrites");
        (await RevisionCountAsync(world)).ShouldBe(2);
        (await StateAsync(world, runId)).ShouldBe(
            "incomplete",
            "a finished run has no forward edge; the new revision carries the new completeness");
        (await PointerAsync(world, runId)).ShouldBe(latest.Id, "the pointer follows the newest revision");
    }

    [Fact]
    public async Task Late_Evidence_Resent_Appends_Exactly_One_Revision()
    {
        // The dispatcher and consumer claim rows of every tenant, so these tests keep a database of their own.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 1, database: dedicated.Name);

        var (runId, _) = await StartIncompleteRunAsync(world);
        await OpenLateWindowAsync(world, runId);

        var body = IngestQaRequests.Batch(IngestQaRequests.Event(
            Guid.CreateVersion7(),
            world.Plan.Executions[^1].ExecutionKey,
            sequence: 2));

        var first = await IngestQaRequests.PostEventsAsync(world.Client, runId, body);
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        first.Body.GetProperty("accepted").GetInt32().ShouldBe(1);

        // The same evidence again: the event id is already stored, so this batch accepts nothing and asks for nothing.
        var resend = await IngestQaRequests.PostEventsAsync(world.Client, runId, body);
        resend.Status.ShouldBe(HttpStatusCode.OK, resend.ErrorMessage);
        resend.Body.GetProperty("accepted").GetInt32().ShouldBe(0);
        resend.Body.GetProperty("duplicates").GetInt32().ShouldBe(1);
        (await LateTriggerRowsAsync(world, runId)).ShouldBe(1, "a resend stores no row, so it enqueues no trigger");

        await DispatchAndReconcileAsync(world, expectedReconciles: 1);

        (await RevisionCountAsync(world)).ShouldBe(2, "one late batch appends one revision, whatever it is resent as");
        (await RevisionSummaryAsync(world, runId)).RevisionNo.ShouldBe(2);
    }

    [Fact]
    public async Task Redelivered_Late_Reconcile_Appends_No_Second_Revision()
    {
        // The dispatcher and consumer claim rows of every tenant, so these tests keep a database of their own.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 1, database: dedicated.Name);

        var (runId, _) = await StartIncompleteRunAsync(world);
        await OpenLateWindowAsync(world, runId);

        var late = await IngestQaRequests.PostEventsAsync(
            world.Client,
            runId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(),
                world.Plan.Executions[^1].ExecutionKey,
                sequence: 2)));
        late.Status.ShouldBe(HttpStatusCode.OK, late.ErrorMessage);

        await DispatchAndReconcileAsync(world, expectedReconciles: 1);
        (await RevisionCountAsync(world)).ShouldBe(2);

        // Redelivery: the consumer commits the attempt's transaction and only then stamps the row done, so a crash in
        // between re-claims the same trigger. The evidence has not moved since - the newest revision already counts this
        // watermark - so the second attempt answers with that revision instead of appending a copy of it.
        var (again, second) = await ReconcileAndFinishOnHostAsync(world, runId, lateTrigger: true);

        again.Disposition.ShouldBe(ReconcileDisposition.ComparedLate, Describe(again));
        second.ShouldNotBeNull().RevisionNo.ShouldBe(2, "the guard answers with the revision the evidence already has");
        (await RevisionCountAsync(world)).ShouldBe(
            2,
            "one late batch appends one revision, however often its attempt is redelivered");
        (await StateAsync(world, runId)).ShouldBe("incomplete", "neither attempt settled the run");
    }

    /// <summary>
    /// The late window is decided once, by the ingest route, when it admits the batch — and that decision is what the
    /// trigger it writes carries. A queue that reaches the attempt after the window was spent (a lagging consumer, a
    /// lease expiry, a replay) must therefore still append revision N+1 for evidence the route already accepted and
    /// acknowledged: a revision is the only place that evidence ever counts. Replay appends nothing, because the frozen
    /// set has not moved.
    /// </summary>
    [Fact]
    public async Task Late_Reconcile_After_The_Window_Closed_Still_Appends_The_Next_Revision()
    {
        // The dispatcher and consumer claim rows of every tenant, so these tests keep a database of their own.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 1, database: dedicated.Name);

        var (runId, revisionOne) = await StartIncompleteRunAsync(world);
        await OpenLateWindowAsync(world, runId);

        var late = await IngestQaRequests.PostEventsAsync(
            world.Client,
            runId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(),
                world.Plan.Executions[^1].ExecutionKey,
                sequence: 2)));
        late.Status.ShouldBe(HttpStatusCode.OK, late.ErrorMessage);
        (await LateTriggerRowsAsync(world, runId)).ShouldBe(1);

        // The consumer claims the trigger after the window was spent: the evidence is stored, the window is not open.
        await CloseLateWindowAsync(world, runId);
        (await LateWindowOpenAsync(world, runId)).ShouldBeFalse("the attempt is claimed past the window's end");

        await DispatchAndReconcileAsync(world, expectedReconciles: 1);

        var latest = await RevisionSummaryAsync(world, runId);
        latest.RevisionNo.ShouldBe(
            2,
            "the ingest decision is authoritative: accepted late evidence reaches a revision however late the queue is");
        latest.Completeness.ShouldBe(ReconcileFinisher.Complete);
        latest.Missing.ShouldBeEmpty();
        (await RevisionRowAsync(world, runId, 1)).ShouldBe(
            revisionOne,
            "the earlier revision is immutable: the late attempt appends, it never rewrites");
        (await StateAsync(world, runId)).ShouldBe("incomplete", "a finished run has no forward edge");

        // The same attempt replayed once more, still after the window closed: the frozen set has not moved, so the
        // finisher answers with the revision the evidence already has instead of appending a copy.
        var (again, replay) = await ReconcileAndFinishOnHostAsync(world, runId, lateTrigger: true);

        again.Disposition.ShouldBe(ReconcileDisposition.ComparedLate, Describe(again));
        replay.ShouldNotBeNull().RevisionNo.ShouldBe(2, "a replay answers with the revision the evidence already has");
        (await RevisionCountAsync(world)).ShouldBe(2, "one late batch appends one revision, however late it is claimed");
    }

    /// <summary>
    /// The late guard's two halves are an AND, never an OR: the watermark alone would read "the run still holds this many
    /// events" and answer with the recorded revision, but a freeze that resolved an artifact no event names carries
    /// evidence that revision does not. Same watermark, different frozen set appends the next revision.
    /// </summary>
    [Fact]
    public async Task Late_Reconcile_With_A_Newly_Ready_Artifact_At_The_Same_Watermark_Appends_The_Next_Revision()
    {
        // The dispatcher and consumer claim rows of every tenant, so these tests keep a database of their own.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 1, database: dedicated.Name);

        var (runId, _) = await StartIncompleteRunAsync(world);
        await OpenLateWindowAsync(world, runId);

        var late = await IngestQaRequests.PostEventsAsync(
            world.Client,
            runId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(),
                world.Plan.Executions[^1].ExecutionKey,
                sequence: 2)));
        late.Status.ShouldBe(HttpStatusCode.OK, late.ErrorMessage);

        await DispatchAndReconcileAsync(world, expectedReconciles: 1);
        var second = await RevisionSummaryAsync(world, runId);
        second.RevisionNo.ShouldBe(2);

        // Evidence no event mentions: a ready artifact of an execution whose attempt already exists, so the pair resolves
        // and the frozen set grows while the stored-event count stays where it was.
        await SeedReadyArtifactAsync(
            world,
            runId,
            world.Plan.Executions[^1].ExecutionKey,
            attemptNo: 1,
            kind: "trace");

        var (outcome, finalization) = await ReconcileAndFinishOnHostAsync(world, runId, lateTrigger: true);

        outcome.Disposition.ShouldBe(ReconcileDisposition.ComparedLate, Describe(outcome));
        outcome.Comparison.ShouldNotBeNull().EventWatermark.ShouldBe(
            second.EventWatermark,
            "no event arrived, only an artifact");
        finalization.ShouldNotBeNull().RevisionNo.ShouldBe(3, "a different frozen set is different evidence");

        var third = await RevisionSummaryAsync(world, runId);
        third.RevisionNo.ShouldBe(3);
        third.EventWatermark.ShouldBe(second.EventWatermark);
        third.ArtifactSetHash.ShouldNotBe(second.ArtifactSetHash);
        (await RevisionCountAsync(world)).ShouldBe(3);
        (await PointerAsync(world, runId)).ShouldBe(third.Id);
        (await StateAsync(world, runId)).ShouldBe("incomplete");
    }

    /// <summary>
    /// A batch carries one producer, so a multi-producer late batch cannot exist: the parser rejects the first deviating
    /// envelope and the whole batch is refused, leaving no evidence, no trigger and the run as it was.
    /// </summary>
    [Fact]
    public async Task Late_Batch_Of_Two_Producers_Is_Rejected_And_Enqueues_No_Trigger()
    {
        // The dispatcher and consumer claim rows of every tenant, so these tests keep a database of their own.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 1, slots: 2, database: dedicated.Name);

        var (runId, _) = await StartIncompleteRunAsync(world, closeSecondShard: true);
        await OpenLateWindowAsync(world, runId);

        var mixed = await IngestQaRequests.PostEventsAsync(
            world.Client,
            runId,
            IngestQaRequests.Batch(
                IngestQaRequests.Event(
                    Guid.CreateVersion7(),
                    world.Plan.Executions[^1].ExecutionKey,
                    sequence: 2),
                IngestQaRequests.Event(
                    Guid.CreateVersion7(),
                    IngestQaRequests.KeyOf(world, 1),
                    sequence: 1,
                    producerId: SecondProducer,
                    type: "test.step",
                    status: null)));

        mixed.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"{mixed.ErrorMessage}: {mixed.Body}");
        mixed.Body.GetProperty("error").GetProperty("details")[0].GetProperty("reason").GetString()
            .ShouldBe("producer_mismatch");
        (await LateTriggerRowsAsync(world, runId)).ShouldBe(0, "a refused batch asks for no comparison");
        (await world.CountAsync("event", $"org_id = '{world.Org:D}'")).ShouldBe(1, "only the first revision's event is stored");
        (await RevisionCountAsync(world)).ShouldBe(1);
    }

    /// <summary>
    /// One accepted late batch writes exactly one trigger, naming the batch's producer and that producer's high-water
    /// mark inside the batch — never one per event, and never two for the same producer.
    /// </summary>
    [Fact]
    public async Task Late_Batch_Writes_One_Trigger_With_Its_Producers_High_Water_Mark()
    {
        // The dispatcher and consumer claim rows of every tenant, so these tests keep a database of their own.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 1, database: dedicated.Name);

        var (runId, _) = await StartIncompleteRunAsync(world, declaredLastSequence: 3, declaredCount: 3);
        await OpenLateWindowAsync(world, runId);

        var executionKey = world.Plan.Executions[^1].ExecutionKey;
        var late = await IngestQaRequests.PostEventsAsync(
            world.Client,
            runId,
            IngestQaRequests.Batch(
                IngestQaRequests.Event(Guid.CreateVersion7(), executionKey, sequence: 2, type: "test.step", status: null),
                IngestQaRequests.Event(Guid.CreateVersion7(), executionKey, sequence: 3)));

        late.Status.ShouldBe(HttpStatusCode.OK, $"{late.ErrorMessage}: {late.Body}");
        late.Body.GetProperty("accepted").GetInt32().ShouldBe(2);
        (await LateTriggerKeysAsync(world, runId)).ShouldBe(
            [$"orchestrator:run:{runId:D}:reconcile:late:{Producer}:3"],
            "one trigger for the batch, keyed by its producer and the batch's high-water mark");

        await DispatchAndReconcileAsync(world, expectedReconciles: 1);

        (await DoneReconcilesAsync(world)).ShouldBe(1);
        (await RevisionCountAsync(world)).ShouldBe(2);
        (await StateAsync(world, runId)).ShouldBe("incomplete");
    }

    /// <summary>
    /// The trigger and the evidence share one transaction: the writer here refuses the batch's trigger, so the attempt
    /// fails after it has already stored the events. Nothing may survive it — a kept event without its trigger would
    /// never be compared, and a kept trigger would ask for evidence the failed attempt rolled back.
    /// </summary>
    [Fact]
    public async Task Late_Batch_Whose_Trigger_Fails_Commits_Neither_Evidence_Nor_Trigger()
    {
        await using var world = await StartWithRefusingOutboxAsync(allowedReconcileEnqueues: 0);

        var (runId, _) = await StartIncompleteRunAsync(world);
        await OpenLateWindowAsync(world, runId);

        var crashed = await IngestQaRequests.PostEventsAsync(
            world.Client,
            runId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(),
                world.Plan.Executions[^1].ExecutionKey,
                sequence: 2)));

        crashed.Status.ShouldBe(HttpStatusCode.InternalServerError, $"{crashed.ErrorMessage}: {crashed.Body}");
        (await LateTriggerRowsAsync(world, runId)).ShouldBe(0, "a trigger that failed to enqueue leaves no row");
        (await world.CountAsync("event", $"org_id = '{world.Org:D}'")).ShouldBe(1, "the late event rolled back with the trigger it asked for");
        (await RevisionCountAsync(world)).ShouldBe(1);
        (await StateAsync(world, runId)).ShouldBe("incomplete");
    }

    [Fact]
    public async Task Late_Evidence_On_Complete_Run_Is_Refused_And_Appends_No_Revision()
    {
        // The dispatcher and consumer claim rows of every tenant, so these tests keep a database of their own.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(database: dedicated.Name);

        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 1, declaredCount: 1);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(Producer, 1, "test.step", null, ExecutionKey: world.Plan.Executions[0].ExecutionKey));

        var (outcome, finalization) = await ReconcileAndFinishAsync(world, runId);
        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Complete, Describe(outcome));
        (await StateAsync(world, runId)).ShouldBe("complete");

        // A complete run is closed whatever its window says: the batch is refused before any row is written.
        await OpenLateWindowAsync(world, runId);
        var refused = await IngestQaRequests.PostEventsAsync(
            world.Client,
            runId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(),
                world.Plan.Executions[1].ExecutionKey,
                sequence: 2)));

        refused.Status.ShouldBe(HttpStatusCode.Conflict, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("run_closed");
        (await LateTriggerRowsAsync(world, runId)).ShouldBe(0, "nothing was accepted, so no trigger was written");
        (await world.CountAsync("event", $"org_id = '{world.Org:D}'")).ShouldBe(1);

        // And a trigger that named a complete run anyway decides nothing: only a late window reopens the comparison.
        var attempt = await ReconcileAsync(world, runId);
        attempt.Disposition.ShouldBe(
            ReconcileDisposition.NotCollectable,
            "completeness is decided once; a later trigger cannot append over it");
        (await RevisionCountAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task Late_Evidence_Outside_The_Window_Is_Refused_And_Enqueues_No_Trigger()
    {
        // The dispatcher and consumer claim rows of every tenant, so these tests keep a database of their own.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 1, database: dedicated.Name);

        var (runId, _) = await StartIncompleteRunAsync(world);
        await CloseLateWindowAsync(world, runId);

        var refused = await IngestQaRequests.PostEventsAsync(
            world.Client,
            runId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(),
                world.Plan.Executions[^1].ExecutionKey,
                sequence: 2)));

        refused.Status.ShouldBe(HttpStatusCode.Conflict, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("run_closed");
        (await LateTriggerRowsAsync(world, runId)).ShouldBe(0);
        (await RevisionCountAsync(world)).ShouldBe(1, "a refused batch writes no evidence to reconcile");
    }

    // ------------------------------------------------------------------ the epoch flag

    /// <summary>
    /// An event's declared <c>epoch</c> is stored verbatim and marks nothing stale, because nothing can compare it: no
    /// job or job-lease table exists, and neither the run nor the producer slot stores a current epoch, so
    /// <c>event.stale</c> has no writer and the comparison names no epoch reason. This pins the state the removal left
    /// behind — the flag and the 00 §8.5 code stay in the schema, the reason returns with lease epochs — so an
    /// accidental half-restore (a writer without a source, or a reason without a writer) fails here.
    /// </summary>
    [Fact]
    public async Task Older_Declared_Epoch_Is_Stored_Verbatim_And_Marks_Nothing_Stale()
    {
        // The dispatcher and consumer claim rows of every tenant, so these tests keep a database of their own.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(database: dedicated.Name);

        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");

        // Two events of the same producer, the second declaring a lower epoch: the shape a superseded lease would send.
        var first = await IngestQaRequests.PostEventsAsync(
            world.Client,
            runId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(),
                IngestQaRequests.KeyOf(world, 0),
                sequence: 1,
                type: "test.step",
                status: null,
                epoch: 3L)));
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);

        var second = await IngestQaRequests.PostEventsAsync(
            world.Client,
            runId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(),
                IngestQaRequests.KeyOf(world, 1),
                sequence: 2,
                type: "test.step",
                status: null,
                epoch: 2L)));
        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);

        (await world.ScalarAsync<long>($"select epoch from public.event where org_id = '{world.Org:D}' and sequence = 1")).ShouldBe(3);
        (await world.ScalarAsync<long>($"select epoch from public.event where org_id = '{world.Org:D}' and sequence = 2")).ShouldBe(2);
        (await EventFlagAsync(world, 1, "stale")).ShouldBeFalse();
        (await EventFlagAsync(world, 2, "stale")).ShouldBeFalse("no epoch source exists to call it superseded");

        // The comparison names no epoch reason either: the shard's counts decide it, and nothing else.
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 2, declaredCount: 2);
        var (outcome, finalization) = await ReconcileAndFinishAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Complete, Describe(outcome));
        (await RevisionSummaryAsync(world, runId)).Missing.ShouldBeEmpty(
            "stale_epoch has no producer: the flag's only writer would be a current epoch nothing stores");
    }
}
