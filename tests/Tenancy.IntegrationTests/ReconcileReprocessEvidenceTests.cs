// Reprocessed-evidence half of the reconcile suite. It is a partial file of ReconcileRunTests, not a separate class:
// every case here drives the same worlds, seeding and read helpers the rest of the suite declares, and splitting the
// class would mean duplicating them or widening their visibility for no gain.
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReconcileRunTests
{
    // ------------------------------------------------------------------ reprocessed evidence triggers

    /// <summary>
    /// A canceled run accepts a reprocess of its own stored report, and that evidence has nowhere else to count: a
    /// canceled run is terminal, so the trigger its reprocess enqueues is the one thing that turns the re-read rows into
    /// revision N+1. The run stays canceled, the earlier revision stays as it is, and no state edge is taken.
    /// </summary>
    [Fact]
    public async Task Reprocess_On_Canceled_Run_Appends_Its_Revision_And_Leaves_The_Run_Canceled()
    {
        // The dispatcher and consumer claim rows of every tenant, so these tests keep a database of their own.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 1, database: dedicated.Name);

        var arranged = await ArrangeCanceledReprocessAsync(world);

        await EnqueueReprocessTriggerAsync(world, arranged.RunId, arranged.ProducerId);
        (await ReprocessTriggerRowsAsync(world, arranged.RunId)).ShouldBe(
            1,
            "one accepted reprocess set enqueues one reconcile, in the same transaction as the evidence");

        await DispatchAndReconcileAsync(world, expectedReconciles: 1);

        var latest = await RevisionSummaryAsync(world, arranged.RunId);
        latest.RevisionNo.ShouldBe(2, "the re-read evidence is counted through a revision of its own");
        latest.Completeness.ShouldBe(
            ReconcileFinisher.Incomplete,
            "the shard never delivered its declared second event and the plan still has an unanswered execution");
        latest.EventWatermark.ShouldBe(2, "the reprocessed event is part of the frozen evidence");
        (await RevisionRowAsync(world, arranged.RunId, 1)).ShouldBe(
            arranged.RevisionOne,
            "the earlier revision is immutable: the reprocess appends, it never rewrites");
        (await RevisionCountAsync(world)).ShouldBe(2);
        (await PointerAsync(world, arranged.RunId)).ShouldBe(latest.Id, "the pointer follows the newest revision");
        (await StateAsync(world, arranged.RunId)).ShouldBe(
            "canceled",
            "a canceled run is terminal: the revision carries the new evidence, the state does not move");
        (await VersionAsync(world, arranged.RunId)).ShouldBe(
            arranged.Version + 1,
            "the appended revision moves the run's evidence pointer in the same transaction — the row's only write, "
            + "since no guarded transition ran");
        (await StateChangeAuditCountAsync(world, arranged.RunId)).ShouldBe(
            arranged.StateChangeAuditRows,
            "an appended revision takes no edge, so it audits none");
        (await world.CountAsync("run", $"org_id = '{world.Org:D}'")).ShouldBe(
            1,
            "a reprocess appends a revision beside the run; it never creates a rerun");
        (await world.ScalarAsync<string?>(
            $"select rerun_scope from public.run where id = '{arranged.RunId:D}'")).ShouldBeNull(
            "the reprocessed revision is not labelled as a rerun");
    }

    /// <summary>
    /// The reprocess marker is the only thing that may compare a canceled run: neither the ordinary trigger nor a
    /// replayed late one appends anything, because no evidence was re-read for them. The positive control for both is
    /// <see cref="Reprocess_On_Canceled_Run_Appends_Its_Revision_And_Leaves_The_Run_Canceled"/>: same world, same rows,
    /// only the payload's marker differs.
    /// </summary>
    [Fact]
    public async Task Canceled_Run_Without_The_Reprocess_Marker_Stays_NotCollectable()
    {
        // The dispatcher and consumer claim rows of every tenant, so these tests keep a database of their own.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 1, database: dedicated.Name);

        var arranged = await ArrangeCanceledReprocessAsync(world);

        var ordinary = await ReconcileAsync(world, arranged.RunId);
        ordinary.Disposition.ShouldBe(ReconcileDisposition.NotCollectable, Describe(ordinary));
        ordinary.Comparison.ShouldBeNull();

        var late = await ReconcileAsync(world, arranged.RunId, lateTrigger: true);
        late.Disposition.ShouldBe(
            ReconcileDisposition.NotCollectable,
            "the late marker says ingest evidence arrived, and a canceled run never counts that as a revision");

        (await RevisionCountAsync(world)).ShouldBe(1, "no attempt was compared, so no revision was appended");
        (await StateAsync(world, arranged.RunId)).ShouldBe("canceled");
    }

    /// <summary>
    /// An incomplete run answers the reprocess marker like late evidence: same path, same append, state untouched —
    /// that run does not keep a distinct disposition of its own.
    /// </summary>
    [Fact]
    public async Task Reprocess_Marker_On_Incomplete_Run_Takes_The_Late_Path()
    {
        // The dispatcher and consumer claim rows of every tenant, so these tests keep a database of their own.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 1, database: dedicated.Name);

        var (runId, revisionOne) = await StartIncompleteRunAsync(world);

        // The evidence the first revision missed: the second event the shard declared but never delivered, so the
        // attempt compares again and the new evidence reaches a revision of its own.
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(
                Producer,
                2,
                "test.finished",
                "passed",
                ExecutionKey: world.Plan.Executions[^1].ExecutionKey));

        var (outcome, finalization) = await ReconcileAndFinishOnHostAsync(world, runId, reprocessTrigger: true);

        outcome.Disposition.ShouldBe(ReconcileDisposition.ComparedLate, Describe(outcome));
        finalization.ShouldNotBeNull().RevisionNo.ShouldBe(2, "the evidence reached a revision of its own");
        (await RevisionRowAsync(world, runId, 1)).ShouldBe(
            revisionOne,
            "the earlier revision is immutable: the attempt appends, it never rewrites");
        (await StateAsync(world, runId)).ShouldBe("incomplete", "a finished run has no forward edge");
    }

    /// <summary>
    /// A redelivered reprocess attempt answers with the revision the evidence already has: the consumer commits the
    /// attempt's transaction and only then stamps the row done, so a crash in between re-claims the same trigger.
    /// </summary>
    [Fact]
    public async Task Redelivered_Reprocess_Reconcile_Appends_No_Second_Revision()
    {
        // The dispatcher and consumer claim rows of every tenant, so these tests keep a database of their own.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 1, database: dedicated.Name);

        var arranged = await ArrangeCanceledReprocessAsync(world);
        await EnqueueReprocessTriggerAsync(world, arranged.RunId, arranged.ProducerId);
        await DispatchAndReconcileAsync(world, expectedReconciles: 1);
        (await RevisionCountAsync(world)).ShouldBe(2);

        var (again, second) = await ReconcileAndFinishOnHostAsync(world, arranged.RunId, reprocessTrigger: true);

        again.Disposition.ShouldBe(ReconcileDisposition.ComparedReprocess, Describe(again));
        second.ShouldNotBeNull().RevisionNo.ShouldBe(2, "the guard answers with the revision the evidence already has");
        (await RevisionCountAsync(world)).ShouldBe(
            2,
            "one reprocess set appends one revision, however often its attempt is redelivered");
        (await StateAsync(world, arranged.RunId)).ShouldBe("canceled", "neither attempt settled the run");
        (await StateChangeAuditCountAsync(world, arranged.RunId)).ShouldBe(
            arranged.StateChangeAuditRows,
            "no redelivery takes a state edge");
    }

    /// <summary>
    /// A complete run is terminal, so the only place a re-reading of its own stored report can count is its next revision:
    /// the marker appends revision N+1 beside a run that stays complete, and takes no state edge.
    /// </summary>
    [Fact]
    public async Task Reprocess_On_Complete_Run_Appends_Its_Revision_And_Leaves_The_Run_Complete()
    {
        // The dispatcher and consumer claim rows of every tenant, so these tests keep a database of their own. Every
        // planned execution is answered here: the complete arrangement below must not inherit a missing_result.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(database: dedicated.Name);

        var (runId, revisionOne) = await StartCompleteRunAsync(world);

        // The evidence the first revision missed, under a synthetic producer of its own: the same shape the reprocess
        // task writes.
        var producerId = "reprocess-" + Guid.CreateVersion7().ToString("D");
        await InsertSyntheticSlotAsync(world, runId, producerId, shardIndex: 2, declaredLastSequence: 1, declaredCount: 1);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(
                producerId,
                1,
                "test.finished",
                "passed",
                ExecutionKey: world.Plan.Executions[^1].ExecutionKey));
        var audits = await StateChangeAuditCountAsync(world, runId);

        var (outcome, finalization) = await ReconcileAndFinishOnHostAsync(world, runId, reprocessTrigger: true);

        outcome.Disposition.ShouldBe(ReconcileDisposition.ComparedReprocess, Describe(outcome));
        finalization.ShouldNotBeNull().RevisionNo.ShouldBe(2, "the re-read evidence reaches a revision of its own");
        (await RevisionRowAsync(world, runId, 1)).ShouldBe(
            revisionOne,
            "the earlier revision is immutable: the attempt appends, it never rewrites");
        (await StateAsync(world, runId)).ShouldBe(
            "complete",
            "a complete run is terminal: the revision carries the evidence, the state does not move");
        (await StateChangeAuditCountAsync(world, runId)).ShouldBe(audits, "an appended revision takes no edge");
    }

    /// <summary>
    /// A live run that already holds a revision answers the marker with an append beside it: the collection settlement
    /// decided that run once, so the re-read evidence must never re-decide it, and the comparison alone names whatever
    /// shard is still open.
    /// </summary>
    [Fact]
    public async Task Reprocess_Marker_On_Live_Run_With_A_Revision_Appends_Beside_It()
    {
        // The dispatcher and consumer claim rows of every tenant, so these tests keep a database of their own.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 1, database: dedicated.Name);

        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "collecting");

        // No declared edge leaves a settled run, so a revision beside a run that is still collecting has no producing
        // path: the fixture writes revision one itself, and the append this case asserts is the production one.
        await SeedRevisionOneAsync(world, runId, eventWatermark: 0);
        var revisionOne = await RevisionRowAsync(world, runId, 1);

        var producerId = "reprocess-" + Guid.CreateVersion7().ToString("D");
        await InsertSyntheticSlotAsync(world, runId, producerId, shardIndex: 2, declaredLastSequence: 1, declaredCount: 1);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(
                producerId,
                1,
                "test.finished",
                "passed",
                ExecutionKey: world.Plan.Executions[^1].ExecutionKey));

        var (outcome, finalization) = await ReconcileAndFinishOnHostAsync(world, runId, reprocessTrigger: true);

        outcome.Disposition.ShouldBe(ReconcileDisposition.ComparedReprocess, Describe(outcome));
        finalization.ShouldNotBeNull().RevisionNo.ShouldBe(2);
        (await RevisionRowAsync(world, runId, 1)).ShouldBe(revisionOne, "the append never rewrites the earlier revision");
        (await StateAsync(world, runId)).ShouldBe(
            "collecting",
            "a reprocess marker never settles a live run the collection decision has not settled");
    }

    /// <summary>
    /// A live run that holds no revision yet is the one case the marker writes nothing for: its collection settlement IS
    /// the revision this evidence belongs to, so comparing here would either decide a collecting run or append ahead of
    /// its baseline. The attempt reports the wait and leaves the run, its revisions and its queue rows untouched.
    /// </summary>
    [Fact]
    public async Task Reprocess_Marker_On_Live_Run_Without_A_Revision_Waits_For_The_Collection_Settlement()
    {
        // The dispatcher and consumer claim rows of every tenant, so these tests keep a database of their own.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartWithQueueAsync(attemptsSkip: 1, database: dedicated.Name);

        await SetRunStateAsync(world, world.RunId, "collecting");
        var versions = await VersionAsync(world, world.RunId);

        var outcome = await ReconcileAsync(world, world.RunId, reprocessTrigger: true);

        outcome.Disposition.ShouldBe(ReconcileDisposition.ReprocessAwaitsCollection, Describe(outcome));
        outcome.Comparison.ShouldBeNull("nothing was compared, so nothing may be frozen");
        (await RevisionCountAsync(world)).ShouldBe(0, "no revision exists, and the marker appends none");
        (await StateAsync(world, world.RunId)).ShouldBe("collecting", "the marker settles no live run");
        (await VersionAsync(world, world.RunId)).ShouldBe(versions, "not even the run's version moved");
    }

    // ------------------------------------------------------------------ world and driving

    /// <summary>
    /// A canceled run that already holds revision one, plus the rows a reprocess of its stored report leaves: the
    /// synthetic producer's slot, written closed and declared, and one event under it, which is what the reconciler's
    /// aggregate reads and what moves the evidence watermark.
    /// <para>
    /// The run is canceled first, because a settled run has no declared outgoing edge: moving <c>incomplete</c> to
    /// <c>canceled</c> afterwards is an edge the state machine refuses. Revision one is therefore the production append
    /// of a reprocess attempt on the canceled run — the same settlement path the cases below assert again with the re-read
    /// evidence — and the run stays canceled throughout.
    /// </para>
    /// </summary>
    /// <param name="world">The queued world, whose plan keeps one execution unattempted.</param>
    /// <returns>The run, revision one as a row, the synthetic producer and the row/audit state before the attempt.</returns>
    private async Task<ArrangedReprocess> ArrangeCanceledReprocessAsync(IngestTestWorld world)
    {
        var runId = world.RunId;
        await SetRunStateAsync(world, runId, "canceled");
        await CloseSlotAsync(world, runId, Producer, declaredLastSequence: 2, declaredCount: 2);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(Producer, 1, "test.step", null, ExecutionKey: world.Plan.Executions[0].ExecutionKey));

        var (arrangeOutcome, arranged) = await ReconcileAndFinishOnHostAsync(world, runId, reprocessTrigger: true);
        arrangeOutcome.Disposition.ShouldBe(ReconcileDisposition.ComparedReprocess, Describe(arrangeOutcome));
        arranged.ShouldNotBeNull().RevisionNo.ShouldBe(1, "the first reprocess append is revision one");
        (await StateAsync(world, runId)).ShouldBe("canceled", "an appended revision never moves the run");
        var revisionOne = await RevisionRowAsync(world, runId, 1);

        // The synthetic producer of a reprocess never collides with a shard that delivered the original report, and its
        // slot is written closed and declared: no producer will finalize it, so an open slot would hold the reconciler
        // on a shard that can never close.
        var producerId = "reprocess-" + Guid.CreateVersion7().ToString("D");
        await InsertSyntheticSlotAsync(world, runId, producerId, shardIndex: 2, declaredLastSequence: 1, declaredCount: 1);
        await StoreEventsAsync(
            world,
            runId,
            new StoredEvent(
                producerId,
                1,
                "test.finished",
                "passed",
                ExecutionKey: world.Plan.Executions[^1].ExecutionKey));

        return new ArrangedReprocess(
            runId,
            revisionOne,
            producerId,
            await VersionAsync(world, runId),
            await StateChangeAuditCountAsync(world, runId));
    }

    /// <summary>One seeded run and its post-cancel evidence, as a case drives and asserts on it.</summary>
    /// <param name="RunId">The run.</param>
    /// <param name="RevisionOne">Revision one of the run as a row, for the immutability assertion.</param>
    /// <param name="ProducerId">The synthetic producer the re-read event was written under.</param>
    /// <param name="Version">The run row's version before the attempt.</param>
    /// <param name="StateChangeAuditRows">The run's <c>state_changed</c> audit rows before the attempt.</param>
    private sealed record ArrangedReprocess(
        Guid RunId,
        string RevisionOne,
        string ProducerId,
        int Version,
        long StateChangeAuditRows);

    /// <summary>
    /// Writes revision one of a live run and moves the run's pointer onto it, in one transaction. No declared edge leaves
    /// a settled run, so a revision beside a run that is still collecting has no producing path — the finisher appends a
    /// revision and then settles the run. The fixture writes the row (and the pointer the reconciler reads as "this run
    /// already holds a verdict") so the append this case asserts stays the production one.
    /// </summary>
    /// <param name="world">The world whose database holds the run.</param>
    /// <param name="runId">The live run that must already hold a revision.</param>
    /// <param name="eventWatermark">The event count the revision counts.</param>
    private async Task SeedRevisionOneAsync(IngestTestWorld world, Guid runId, long eventWatermark)
    {
        var revisionId = Guid.CreateVersion7();
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await scope.ExecuteAsync(
            """
            insert into public.evidence_revision
              (id, org_id, run_id, revision_no, completeness, missing, outcome, artifact_set_hash, event_watermark,
               manifest_hash)
            values (@id, @org_id, @run_id, 1, 'incomplete', '{}'::text[], null, null, @watermark, @manifest_hash)
            """,
            ("id", revisionId),
            ("org_id", world.Org),
            ("run_id", runId),
            ("watermark", eventWatermark),
            ("manifest_hash", ManifestHashOf(runId)))).ShouldBe(1);
        (await scope.ExecuteAsync(
            "update public.run set current_evidence_revision_id = @id where org_id = @org_id and id = @run_id",
            ("id", revisionId),
            ("org_id", world.Org),
            ("run_id", runId))).ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>
    /// The synthetic producer's slot exactly as the reprocess writer leaves it: closed, declared and bound to no
    /// producer token, so the reconciler reads the declaration at once instead of waiting for a finalize.
    /// </summary>
    /// <param name="world">The world's database.</param>
    /// <param name="runId">The run the slot belongs to.</param>
    /// <param name="producerId">The synthetic producer id.</param>
    /// <param name="shardIndex">The free shard index the slot takes.</param>
    /// <param name="declaredLastSequence">The declared high-water mark.</param>
    /// <param name="declaredCount">The declared event count.</param>
    private Task InsertSyntheticSlotAsync(
        IngestTestWorld world,
        Guid runId,
        string producerId,
        int shardIndex,
        long declaredLastSequence,
        int declaredCount) =>
        _fixture.ExecuteAsync(
            world.Database,
            "insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id, bound_token_jti, "
            + "last_sequence, closed_at, declared_last_sequence, declared_count) values "
            + $"(gen_random_uuid(), '{world.Org:D}', '{runId:D}', {shardIndex}, '{producerId}', "
            + $"'synthetic:junit-report', {declaredLastSequence}, now(), {declaredLastSequence}, {declaredCount})");

    /// <summary>
    /// Enqueues the trigger the reprocess task writes for accepted post-cancel evidence, with the contract's own payload
    /// and key, so the case exercises the same row the task commits rather than a hand-written one.
    /// </summary>
    /// <param name="world">The world whose host owns the queue's composition.</param>
    /// <param name="runId">The run the reprocess belongs to.</param>
    /// <param name="producerId">The synthetic producer the re-read rows carry.</param>
    private static async Task EnqueueReprocessTriggerAsync(IngestTestWorld world, Guid runId, string producerId)
    {
        await using var services = world.Host.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, Token);

        await services.ServiceProvider.GetRequiredService<IOutboxWriter>().EnqueueAsync(
            RunReconcileContract.OutboxTaskType,
            RunReconcileContract.ReprocessPayloadJson(runId, producerId),
            RunReconcileContract.AggregateType,
            runId,
            RunReconcileContract.ReprocessBatchIdempotencyKey(runId, producerId, Guid.CreateVersion7()),
            Token);

        await scope.CommitAsync();
    }

    /// <summary>
    /// The reconcile triggers the reprocess path enqueued for one run: the key shape is the contract's, so a count here
    /// is exactly the number of re-read sets that asked for an attempt.
    /// </summary>
    /// <param name="world">The world's database.</param>
    /// <param name="runId">The run whose triggers are counted.</param>
    private static Task<long> ReprocessTriggerRowsAsync(IngestTestWorld world, Guid runId) =>
        world.CountAsync(
            "outbox",
            where: $"task_type = '{RunReconcileContract.OutboxTaskType}' "
            + $"and idempotency_key like 'orchestrator:run:{runId:D}:reconcile:reprocess:%'");
}
