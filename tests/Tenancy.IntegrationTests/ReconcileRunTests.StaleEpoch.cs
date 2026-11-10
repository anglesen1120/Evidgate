// Stale-epoch half of the reconcile suite: the CI run attempt is the run's current epoch, so a batch that declares a
// different epoch is stored as stale evidence and the closed shard names stale_epoch instead of completing. Partial
// file of ReconcileRunTests for the same reason as the late-evidence file: it drives the suite's worlds and helpers.
using System.Net;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReconcileRunTests
{
    /// <summary>The run's CI reference: <c>repository_id/run_id/run_attempt</c> = 1/7/2, so the current epoch is 2.</summary>
    private const string RunCiRunRefJson =
        """{"provider":"github_actions","repository_id":1,"run_id":7,"run_attempt":2}""";

    /// <summary>The epoch a batch must declare to be current on <see cref="RunCiRunRefJson"/>'s run.</summary>
    private const long CurrentEpoch = 2L;

    /// <summary>The epoch of a superseded attempt of the same CI run: foreign, so its events are stale.</summary>
    private const long ForeignEpoch = 1L;

    /// <summary>
    /// A world whose caller owns a verified CI run reference and whose run records the same one, so the batch handler
    /// binds the slot and the insert can compare each declared epoch with the run's own attempt. Each test keeps a
    /// database of its own: the dispatcher and consumer claim rows of every tenant and would race the assertions.
    /// </summary>
    private async Task<IngestTestWorld> StartEpochWorldAsync(string database)
    {
        var writer = RequestPrincipal.Service(
            projectScope: null,
            claimedPermissions: [Permissions.Event.Write],
            credentialId: IngestTestWorld.Jti,
            ciRun: new CiRunReference("github_actions", 1L, 7L, 2));
        var world = await IngestTestWorld.StartAsync(_fixture, writer, planned: 2, slots: 1, database: database);
        world = await AnswerPlanOrDisposeAsync(world);
        await _fixture.ExecuteAsync(
            world.Database, $"update public.run set ci_run_ref = '{RunCiRunRefJson}'::jsonb where id = '{world.RunId:D}'");

        return world;
    }

    /// <summary>One batch of one producer delivered over the real events route.</summary>
    private static Task<ApiAnswer> PostBatchAsync(IngestTestWorld world, params object[] events) =>
        IngestQaRequests.PostEventsAsync(world.Client, world.RunId, IngestQaRequests.Batch(events));

    [Fact]
    public async Task Foreign_Epoch_Batch_Is_Stored_Stale_And_Its_Shard_Is_Never_Complete()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartEpochWorldAsync(dedicated.Name);
        await SetRunStateAsync(world, world.RunId, "collecting");

        // The event declares the superseded attempt, so the batch is accepted and kept, marked rather than refused.
        var foreign = IngestQaRequests.Event(
            Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1, epoch: ForeignEpoch);
        var answer = await PostBatchAsync(world, foreign);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("accepted").GetInt32().ShouldBe(1);
        (await EventFlagAsync(world, 1, "stale")).ShouldBeTrue("epoch 1 is not the run's attempt 2");

        // The derived flag is not part of the payload hash: a replay is still one row, with the same flag.
        var replay = await PostBatchAsync(world, foreign);
        replay.Status.ShouldBe(HttpStatusCode.OK, replay.ErrorMessage);
        (await world.CountAsync("event", $"run_id = '{world.RunId:D}'"))
            .ShouldBe(1, "a replayed event is a duplicate, never a second row");
        (await EventFlagAsync(world, 1, "stale")).ShouldBeTrue("a duplicate keeps the stored flag");

        await CloseSlotAsync(world, world.RunId, Producer, declaredLastSequence: 1, declaredCount: 1);
        var (outcome, finalization) = await ReconcileAndFinishAsync(world, world.RunId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Incomplete, Describe(outcome));

        // The revision is what the evaluator reads: stale_epoch with no other reason keeps the stored verdict off PASS
        // (the incomplete/stale_epoch row of EvaluationTaskMatrixTests evaluates to INCONCLUSIVE, stale_epoch_only).
        var revision = await RevisionSummaryAsync(world, world.RunId);
        revision.Completeness.ShouldBe(ReconcileFinisher.Incomplete);
        revision.Missing.ShouldBe(["stale_epoch"]);
    }

    [Fact]
    public async Task Current_Epoch_Batch_Is_Not_Stale_And_Its_Shard_Completes()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartEpochWorldAsync(dedicated.Name);
        await SetRunStateAsync(world, world.RunId, "collecting");

        // The control of the stale case: the same shape declaring the run's own attempt is ordinary evidence.
        var answer = await PostBatchAsync(
            world,
            IngestQaRequests.Event(
                Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1, epoch: CurrentEpoch));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        (await EventFlagAsync(world, 1, "stale")).ShouldBeFalse("epoch 2 is the run's attempt 2");

        await CloseSlotAsync(world, world.RunId, Producer, declaredLastSequence: 1, declaredCount: 1);
        var (outcome, finalization) = await ReconcileAndFinishAsync(world, world.RunId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Complete, Describe(outcome));
        (await RevisionSummaryAsync(world, world.RunId)).Missing.ShouldBeEmpty();
    }

    [Fact]
    public async Task Mixed_Batch_Marks_Only_The_Foreign_Epoch_Rows()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartEpochWorldAsync(dedicated.Name);
        await SetRunStateAsync(world, world.RunId, "collecting");

        var answer = await PostBatchAsync(
            world,
            IngestQaRequests.Event(
                Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1, epoch: CurrentEpoch),
            IngestQaRequests.Event(
                Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 1), sequence: 2, epoch: ForeignEpoch));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("accepted").GetInt32().ShouldBe(2);
        (await EventFlagAsync(world, 1, "stale")).ShouldBeFalse("the first row declares the current epoch");
        (await EventFlagAsync(world, 2, "stale")).ShouldBeTrue("the second row declares a superseded epoch");

        await CloseSlotAsync(world, world.RunId, Producer, declaredLastSequence: 2, declaredCount: 2);
        var (outcome, finalization) = await ReconcileAndFinishAsync(world, world.RunId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Incomplete, Describe(outcome));
        (await RevisionSummaryAsync(world, world.RunId)).Missing.ShouldBe(["stale_epoch"]);
    }

    [Fact]
    public async Task Event_Without_Epoch_Is_Never_Stale()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartEpochWorldAsync(dedicated.Name);
        await SetRunStateAsync(world, world.RunId, "collecting");

        // An absent epoch is undeclared, not a foreign one: the insert leaves the flag alone and the batch is accepted.
        var undeclared = IngestQaRequests.Event(
            Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1);
        undeclared.Remove("epoch");

        var answer = await PostBatchAsync(world, undeclared);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        (await EventFlagAsync(world, 1, "stale")).ShouldBeFalse("an event that declares no epoch is never compared");
    }

    [Fact]
    public async Task Run_Without_Ci_Run_Reference_Has_No_Current_Epoch_And_Marks_Nothing_Stale()
    {
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(database: dedicated.Name);
        await SetRunStateAsync(world, world.RunId, "collecting");

        // This world's run records no CI reference, so no attempt exists to compare a declared epoch with.
        var answer = await PostBatchAsync(
            world,
            IngestQaRequests.Event(
                Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1, epoch: 9L));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        (await EventFlagAsync(world, 1, "stale")).ShouldBeFalse("a run without a CI reference declares no current epoch");
        (await world.ScalarAsync<long>(
            $"select epoch from public.event where run_id = '{world.RunId:D}' and sequence = 1"))
            .ShouldBe(9L, "the declared epoch is still stored verbatim");
    }
}
