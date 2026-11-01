// Frozen-revision event half of the reconcile suite. It is a partial file of ReconcileRunTests for the same reason the
// late-evidence file is: every case drives the worlds, seeding and read helpers the rest of the suite declares.
using System.Net;
using System.Text.Json;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReconcileRunTests
{
    // ------------------------------------------------------------------ the frozen-revision event

    /// <summary>
    /// One inserted revision is one frozen event, in the attempt's own transaction: the row names the tenant, the
    /// project, the run and the revision the insert allocated, and its key is the revision id, so the idempotency
    /// contract holds per revision instead of per attempt. The payload carries ids only — a consumer re-reads the
    /// revision it names.
    /// </summary>
    [Fact]
    public async Task Frozen_Event_First_Revision_IsOneRowCarryingIdsOnly()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 1, declaredCount: 1);
        await StoreEventsAsync(world, runId, new StoredEvent(Producer, 1, "test.finished", "passed"));

        var (outcome, finalization) = await ReconcileAndFinishAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        var completed = finalization.ShouldNotBeNull("the compared attempt persisted a revision");
        var revision = await RevisionSummaryAsync(world, runId);
        revision.Id.ShouldBe(completed.RevisionId);

        (await FrozenEventRowsAsync(world, revision.Id)).ShouldBe(1, "one inserted revision announces itself once");
        (await FrozenEventAggregateTypeAsync(world, revision.Id)).ShouldBe(
            EvidenceRevisionFrozenContract.AggregateType,
            "the event is an evidence_revision transition, not a run one");
        (await FrozenEventKeyAsync(world, revision.Id)).ShouldBe(
            EvidenceRevisionFrozenContract.IdempotencyKey(revision.Id),
            "the key is a function of the revision, so a replay of the same insert cannot add a second row");

        var payload = await FrozenEventPayloadAsync(world, revision.Id);
        payload.GetProperty("org_id").GetGuid().ShouldBe(world.Org);
        payload.GetProperty("project_id").GetGuid().ShouldBe(world.Project, "the payload names the run's project");
        payload.GetProperty("run_id").GetGuid().ShouldBe(runId);
        payload.GetProperty("evidence_revision_id").GetGuid().ShouldBe(revision.Id);
        payload.GetProperty("evidence_revision_no").GetInt32().ShouldBe(1);
        // jsonb normalises key order, so the assertion sorts: exactly these five members and nothing else.
        payload.EnumerateObject().Select(member => member.Name).OrderBy(name => name, StringComparer.Ordinal).ShouldBe(
            ["evidence_revision_id", "evidence_revision_no", "org_id", "project_id", "run_id"]);
    }

    /// <summary>
    /// Late evidence appends a revision, and that revision announces itself too: the run now has two frozen events, each
    /// naming its own revision under its own key, because a consumer that saw only the first revision would never
    /// evaluate the second.
    /// </summary>
    [Fact]
    public async Task Frozen_Event_Late_Revision_AppendsASecondRowWithItsOwnKey()
    {
        await using var world = await StartWithQueueAsync(attemptsSkip: 1);

        var (runId, _) = await StartIncompleteRunAsync(world);
        await OpenLateWindowAsync(world, runId);

        var first = await RevisionSummaryAsync(world, runId);
        first.RevisionNo.ShouldBe(1);
        (await FrozenEventRowsAsync(world, first.Id)).ShouldBe(1, "the first freeze announced revision 1");

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
        second.RevisionNo.ShouldBe(2, "late evidence is counted through a revision of its own");
        (await FrozenEventRowsOfRunAsync(world, runId)).ShouldBe(2, "each revision announces itself, once");
        (await FrozenEventRowsAsync(world, first.Id)).ShouldBe(1, "the earlier revision keeps its one event");
        (await FrozenEventRowsAsync(world, second.Id)).ShouldBe(1);

        var firstKey = await FrozenEventKeyAsync(world, first.Id);
        var secondKey = await FrozenEventKeyAsync(world, second.Id);
        secondKey.ShouldBe(EvidenceRevisionFrozenContract.IdempotencyKey(second.Id));
        secondKey.ShouldNotBe(firstKey, "two revisions are two rows, so the outbox dedupe never swallows the second");
        (await FrozenEventPayloadAsync(world, second.Id)).GetProperty("evidence_revision_no").GetInt32().ShouldBe(2);
    }

    /// <summary>
    /// A redelivered late attempt whose evidence the newest revision already counts inserts nothing — the finisher answers
    /// with the recorded revision — so it announces nothing either: the revision keeps its one event and no second row
    /// appears for the run.
    /// </summary>
    [Fact]
    public async Task Frozen_Event_ReplayedAttemptThatFindsTheRevisionRecorded_WritesNoRow()
    {
        await using var world = await StartWithQueueAsync(attemptsSkip: 1);

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
        (await FrozenEventRowsOfRunAsync(world, runId)).ShouldBe(2, "two revisions announced themselves once each");

        // Redelivery: the consumer commits the transaction and only then stamps the row done, so a crash in between
        // re-claims the same trigger. The evidence has not moved, so this attempt appends no revision.
        var (again, replay) = await ReconcileAndFinishOnHostAsync(world, runId, lateTrigger: true);

        again.Disposition.ShouldBe(ReconcileDisposition.ComparedLate, Describe(again));
        var recorded = replay.ShouldNotBeNull("the guard answers with the revision the evidence already has");
        recorded.RevisionNo.ShouldBe(2);
        (await RevisionCountAsync(world)).ShouldBe(2, "no revision was inserted");
        (await FrozenEventRowsOfRunAsync(world, runId)).ShouldBe(
            2,
            "an attempt that inserts nothing announces nothing, so the run keeps its two rows");
        (await FrozenEventRowsAsync(world, recorded.RevisionId)).ShouldBe(
            1,
            "the recorded revision still has exactly the one event its own insert wrote");
    }

    /// <summary>
    /// The event shares the attempt's one transaction: an attempt whose consumer never commits leaves no revision, and no
    /// frozen event either — a row that survived the rollback would announce a revision nobody can read.
    /// </summary>
    [Fact]
    public async Task Frozen_Event_RolledBackAttempt_LeavesNoRow()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 1, declaredCount: 1);
        await StoreEventsAsync(world, runId, new StoredEvent(Producer, 1, "test.finished", "passed"));

        var (outcome, finalization) = await ReconcileRolledBackAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        var completed = finalization.ShouldNotBeNull(
            "the attempt wrote its revision inside the transaction that then rolled back");
        (await RevisionCountAsync(world)).ShouldBe(0, "an uncommitted attempt leaves no revision behind");
        (await FrozenEventRowsOfRunAsync(world, runId)).ShouldBe(
            0,
            "the frozen event rolls back with the revision it announces");
        (await FrozenEventRowsAsync(world, completed.RevisionId)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ helpers: the frozen-revision rows

    /// <summary>How many frozen-revision rows name one revision as their aggregate.</summary>
    private static Task<long> FrozenEventRowsAsync(IngestTestWorld world, Guid revisionId) =>
        world.ScalarAsync<long>(
            $"select count(*) from public.outbox where task_type = '{EvidenceRevisionFrozenContract.OutboxTaskType}' "
            + $"and aggregate_id = '{revisionId:D}'");

    /// <summary>How many frozen-revision rows name one run in their payload, whatever revision each announces.</summary>
    private static Task<long> FrozenEventRowsOfRunAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<long>(
            $"select count(*) from public.outbox where task_type = '{EvidenceRevisionFrozenContract.OutboxTaskType}' "
            + $"and payload ->> 'run_id' = '{runId:D}'");

    /// <summary>The <c>aggregate_type</c> the one row of a revision names.</summary>
    private static Task<string> FrozenEventAggregateTypeAsync(IngestTestWorld world, Guid revisionId) =>
        world.ScalarAsync<string>(
            $"select aggregate_type from public.outbox where task_type = "
            + $"'{EvidenceRevisionFrozenContract.OutboxTaskType}' and aggregate_id = '{revisionId:D}'");

    /// <summary>The <c>idempotency_key</c> the one row of a revision carries.</summary>
    private static Task<string> FrozenEventKeyAsync(IngestTestWorld world, Guid revisionId) =>
        world.ScalarAsync<string>(
            $"select idempotency_key from public.outbox where task_type = "
            + $"'{EvidenceRevisionFrozenContract.OutboxTaskType}' and aggregate_id = '{revisionId:D}'");

    /// <summary>The one row's work payload, without the correlation carrier the outbox writer stamps into it.</summary>
    private static async Task<JsonElement> FrozenEventPayloadAsync(IngestTestWorld world, Guid revisionId)
    {
        var text = await world.ScalarAsync<string>(
            "select (payload - '_evidgate_correlation')::text from public.outbox where task_type = "
            + $"'{EvidenceRevisionFrozenContract.OutboxTaskType}' and aggregate_id = '{revisionId:D}'");

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
