// The idempotency record is the arbiter of a resent batch: a replay answers the stored response without a second
// insert, and the same key with a different payload is a conflict that writes nothing.
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>Separates request replay and payload conflicts from billing-key deduplication.</summary>
public sealed partial class UsageRecordBatchTests
{
    // ------------------------------------------------------------------ T11: the idempotency record is the arbiter

    [Fact]
    public async Task RecordUsageBatch_IdempotencyReplay_ReturnsStoredResult_WithoutSecondInsert()
    {
        await using var world = await StartAsync();
        var command = Batch("replay-1", [Entry("batch:9:results", world.Project, 4m)]);

        var first = await SendAsync(world, command);
        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        first.Value!.Inserted.ShouldBe(["batch:9:results"]);
        (await LedgerCountAsync(world, world.Org)).ShouldBe(1);

        var replay = await SendAsync(world, command);
        replay.IsSuccess.ShouldBeTrue(replay.Error?.Message);
        replay.Value!.Inserted.ShouldBe(first.Value.Inserted, "a replay answers the stored response verbatim");
        replay.Value.Duplicates.ShouldBe(first.Value.Duplicates);
        (await LedgerCountAsync(world, world.Org)).ShouldBe(1, "the replayed request inserted nothing");
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.idempotency_record where org_id = '{world.Org:D}' and operation = 'usage:record_batch'"))
            .ShouldBe(1);

        // The same key with a different payload is a conflict, and it writes nothing either.
        var conflict = await SendAsync(world, Batch("replay-1", [Entry("batch:9:results", world.Project, 5m)]));
        conflict.IsSuccess.ShouldBeFalse();
        conflict.Error!.Status.ShouldBe(409);
        conflict.Error.Code.ShouldBe("idempotency_conflict");
        (await LedgerCountAsync(world, world.Org)).ShouldBe(1);
        (await ColumnAsync(world, world.Org, "batch:9:results", "quantity::text")).ShouldBe("4");
    }
}
