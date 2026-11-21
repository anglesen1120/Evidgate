using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class FinalizeEndpointTests
{
    // ------------------------------------------------------------------ concurrency (AC6, lock order)

    [Fact]
    public async Task Finalize_ConcurrentSameSlot_OneCloseOneOutboxRow()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);
        await world.SetRunStateAsync("running");

        // Two finalizes of the same slot at once, each declaring DIFFERENT values: one wins the close, the other is
        // the idempotent no-op that returns (and enqueues nothing for) the stored declaration.
        var answers = await Task.WhenAll(
            FinalizeAsync(world.Client, world.RunId, await BodyAsync(world, lastSequence: 4, count: 4)),
            FinalizeAsync(world.Client, world.RunId, await BodyAsync(world, lastSequence: 7, count: 7)));

        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.Accepted);

        // Exactly one close and exactly one applied transition — the state-changed and reconcile rows are the
        // observable proof, so no exact row-version count is pinned here (the version bump is a trigger detail).
        (await StateChangedRowsAsync(world, "collecting")).ShouldBe(1);
        (await ReconcileRowsAsync(world)).ShouldBe(1);

        // The winner's declaration is what both reads see, and the single trigger carries the same values.
        var stored = await SlotDeclaredLastSequenceAsync(world);
        (stored == 4L || stored == 7L).ShouldBeTrue($"the close stored a declaration of one racer, not {stored}");
        (await ReconcilePayloadAsync(world)).GetProperty("declared_last_sequence").GetInt64().ShouldBe(stored);
    }

    [Fact]
    public async Task Finalize_ConcurrentWithEventBatch_NoDeadlock()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.Jti, hosts: _hosts);

        // The deadlock-prone path is a run that is still QUEUED: both handlers may take queued -> running
        // (FOR NO KEY UPDATE) and then read the run FOR SHARE, so one that shared-locked first and upgraded second
        // would deadlock (40P01, 05.3.1's lock-order rule). Three fresh runs, each with both requests in flight at
        // once, pin that interleaving instead of a serialised one.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var plan = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, 2);
            await IngestTestWorld.SeedSlotsAsync(
                _fixture, world.Database, world.Org, plan.RunId, 1, IngestTestWorld.Jti);
            await SetRunStateAsync(world, plan.RunId, "queued");

            var body = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["producer_id"] = Producer,
                ["shard"] = 1,
                ["last_sequence"] = 1,
                ["count"] = 1,
                ["manifest_hash"] = await ManifestHashAsync(world, plan.RunId),
            };

            var answers = await Task.WhenAll(
                FinalizeAsync(world.Client, plan.RunId, body),
                IngestQaRequests.PostEventsAsync(
                    world.Client,
                    plan.RunId,
                    IngestQaRequests.Batch(IngestQaRequests.Event(
                        Guid.CreateVersion7(), plan.Executions[0].ExecutionKey, sequence: 1))));

            var finalize = answers[0];
            var batch = answers[1];

            // Neither side deadlocks (40P01) or fails with a 5xx, whatever order the two commit in, and the end
            // state is the same either way: the run is collecting and the slot is closed once.
            ((int)finalize.Status).ShouldBeLessThan(500, finalize.ErrorMessage);
            ((int)batch.Status).ShouldBeLessThan(500, batch.ErrorMessage);
            finalize.Status.ShouldBe(HttpStatusCode.Accepted, finalize.ErrorMessage);
            batch.Status.ShouldBe(HttpStatusCode.OK, batch.ErrorMessage);

            (await RunStateAsync(world, plan.RunId)).ShouldBe("collecting");
            (await SlotClosedInRunAsync(world, plan.RunId)).ShouldBeTrue();
            (await ReconcileRowsInRunAsync(world, plan.RunId)).ShouldBe(1);
        }
    }

    /// <summary>
    /// A finalize on a run that cannot transition any more (collecting) waits for no exclusive lock: it must
    /// complete while another transaction holds the run row <c>FOR SHARE</c> — the lock an event batch takes on the
    /// ingest hot path. A finalize that took <c>FOR NO KEY UPDATE</c> there would queue behind it, so the held lock
    /// is released and the case fails instead of hanging.
    /// </summary>
    /// <remarks>
    /// A private database: the ten-second bound is a wall-clock guard, so sibling tests sharing the database
    /// would turn load into a false failure.
    /// </remarks>
    [Fact]
    public async Task Finalize_OnCollectingRun_DoesNotWaitBehindSharedRunLock()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), slots: 2, database: database, hosts: _hosts);
        await world.SetRunStateAsync("collecting");

        // The second shard is what finalizes, so the closed slot of shard 1 cannot make this a no-op.
        var body = await BodyAsync(world, producerId: "shard-2", shard: 2);

        await using var holder = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await holder.ScalarAsync<int>(
            "select 1 from public.run r where r.org_id = @org_id and r.id = @run_id for share of r",
            ("org_id", world.Org),
            ("run_id", world.RunId))).ShouldBe(1);

        var finalize = FinalizeAsync(world.Client, world.RunId, body);
        var finished = await Task.WhenAny(finalize, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        // Release the shared lock before asserting, so a failure leaves no blocked statement behind.
        await holder.CommitAsync();

        finished.ShouldBeSameAs(finalize, "the finalize was still blocked behind a shared run lock after 10s");
        var answer = await finalize;
        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);
        (await SlotClosedAsync(world, "shard-2")).ShouldBeTrue();
        (await RunStateAsync(world)).ShouldBe("collecting");
        (await ReconcileRowsAsync(world)).ShouldBe(1);
    }

}
