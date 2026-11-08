// Request bodies are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The bounds and the isolation of the producer-slot facts of <c>GET /v1/runs/{r}</c>: a run at the table's maximum
/// number of shards serves every slot in <c>shard_index</c> order, the shard count the table refuses never appears,
/// and each slot's event counts come from its own run and its own producer id — a flat aggregate over either would
/// report the sibling's numbers.
/// </summary>
public sealed partial class GetRunDetailFieldsTests
{
    /// <summary>
    /// The stored maximum: <c>shard_index</c> is checked <c>1..256</c> per run, so 256 slots is the widest list the
    /// read can serve. Every one is served in order, including the 256th, and the 256-row cap is the table's, not a
    /// silent truncation. A 257th slot is refused by the check, and the served list stays at 256.
    /// </summary>
    [Fact]
    public async Task GetRun_ProducerSlots_MaximumShardCount_ServesEverySlotAndRefusesTheNext()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await NewRunAsync(world);
        await _fixture.ExecuteAsync(world.Database, $"""
            delete from public.producer_slot where run_id = '{runId:D}';
            insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id)
            select gen_random_uuid(), '{world.Org}', '{runId:D}', n, 'shard-' || n
              from generate_series(1, 256) as n;
            """);
        IReadOnlyList<string> expected = [.. Enumerable.Range(1, 256).Select(n => $"shard-{n}")];

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var slots = SlotFacts(answer);
        slots.Count.ShouldBe(256);
        answer.Body.GetProperty("declared_producers").EnumerateArray().Select(item => item.GetString()!)
            .ShouldBe(expected);
        answer.Body.GetProperty("shard_count").GetInt32().ShouldBe(256);
        slots.Select(item => item.GetProperty("producer_id").GetString()!).ShouldBe(expected);
        slots[255].GetProperty("producer_id").GetString().ShouldBe("shard-256");

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync($"""
            insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id)
            values ('{Guid.CreateVersion7()}', '{world.Org}', '{runId:D}', 257, 'shard-257')
            """);

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("producer_slot_shard_index_check");
        SlotFacts(await GetAsync(world, runId)).Count.ShouldBe(256);
    }

    /// <summary>
    /// Two runs of one tenant hold a slot with the same producer id. Each run's read reports the events recorded
    /// against its own run only: the sibling's three events are counted on the sibling, so a join that dropped the run
    /// id would report them here and fail the first assertion.
    /// </summary>
    [Fact]
    public async Task GetRun_ProducerSlots_SiblingRunWithSameProducer_ServesItsOwnEventCounts()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var first = await NewRunAsync(world);
        var second = await NewRunAsync(world);
        await SeedEventAsync(world, first, "shard-1", sequence: 1);
        await SeedEventAsync(world, second, "shard-1", sequence: 1);
        await SeedEventAsync(world, second, "shard-1", sequence: 2, stale: true);
        await SeedEventAsync(world, second, "shard-1", sequence: 3, afterCancel: true);

        var firstAnswer = await GetAsync(world, first);

        firstAnswer.Status.ShouldBe(HttpStatusCode.OK, firstAnswer.ErrorMessage);
        var firstSlot = SlotFacts(firstAnswer)[0];
        firstSlot.GetProperty("producer_id").GetString().ShouldBe("shard-1");
        firstSlot.GetProperty("count").GetInt64().ShouldBe(1);
        firstSlot.GetProperty("stale_epoch_events").GetInt64().ShouldBe(0);
        firstSlot.GetProperty("after_cancel_events").GetInt64().ShouldBe(0);

        // The sibling's own read is the control: the same producer id carries the three seeded events there, so the
        // zeroes above are the run boundary, not the flags never having been written.
        var secondSlot = SlotFacts(await GetAsync(world, second))[0];
        secondSlot.GetProperty("count").GetInt64().ShouldBe(3);
        secondSlot.GetProperty("stale_epoch_events").GetInt64().ShouldBe(1);
        secondSlot.GetProperty("after_cancel_events").GetInt64().ShouldBe(1);
    }

    /// <summary>
    /// One canceled run, two slots. Slot 1 holds three events: one carrying both late flags, one after-cancel only and
    /// one plain — so it reports three events, one stale and two after-cancel; the event with both flags is counted in
    /// both members. Slot 2 holds one stale event, which must not raise slot 1's counts or slot 2's after-cancel.
    /// </summary>
    [Fact]
    public async Task GetRun_ProducerSlots_CanceledRun_OverlappingLateFlagsCountInBothMembers()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await NewRunAsync(world);
        var canceled = await CancelRunTestData.CancelAsync(world, runId);
        canceled.Status.ShouldBe(HttpStatusCode.Accepted, canceled.ErrorMessage);
        await SeedEventAsync(world, runId, "shard-1", sequence: 1, stale: true, afterCancel: true);
        await SeedEventAsync(world, runId, "shard-1", sequence: 2, afterCancel: true);
        await SeedEventAsync(world, runId, "shard-1", sequence: 3);
        await SeedEventAsync(world, runId, "shard-2", sequence: 1, stale: true);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var slots = SlotFacts(answer);
        slots.Select(item => item.GetProperty("producer_id").GetString()!).ShouldBe(["shard-1", "shard-2"]);
        slots[0].GetProperty("count").GetInt64().ShouldBe(3);
        slots[0].GetProperty("stale_epoch_events").GetInt64().ShouldBe(1);
        slots[0].GetProperty("after_cancel_events").GetInt64().ShouldBe(2);
        slots[1].GetProperty("count").GetInt64().ShouldBe(1);
        slots[1].GetProperty("stale_epoch_events").GetInt64().ShouldBe(1);
        slots[1].GetProperty("after_cancel_events").GetInt64().ShouldBe(0);
    }
}
