// The warning and period half of the events route's quota admission: a crossing enqueues exactly one warning for
// its own tenant and no other, a resend never announces again, the month boundary carries the limit without
// carrying usage or the threshold stamps, and the operator switch is the only thing that admits an over-limit
// batch.
#pragma warning disable CA1861
using System.Globalization;
using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The warning, rollover and switch cases of <see cref="IngestQuotaTests"/>: what the route's one gate call does
/// to the counter's stamps and the warning outbox around them, over the real host and a real PostgreSQL.
/// </summary>
public sealed partial class IngestQuotaTests
{
    [Fact]
    public async Task IngestEvents_SpentMonthBefore_CarriesItsLimitIntoTheNewOne()
    {
        await using var world = await StartIngestAsync();
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);

        // The month the route gates and meters in is the database clock's, so the boundary case seeds the real month
        // before this one: spent to its limit, blocked, and its warning already announced.
        var spentMonth = await PreviousMonthAsync(world);
        await counters.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), world.Project, spentMonth, "results", 10m, 10m, WarnRatio,
            WarnedAt: DateTimeOffset.UtcNow.AddDays(-1),
            BlockedAt: DateTimeOffset.UtcNow.AddDays(-1),
            Org: world.Org));
        var spent = (await counters.CounterAsync(world.Org, world.Project, spentMonth)).ShouldNotBeNull();

        var answer = await PostAsync(world, EventBatch(world, 1, 1));

        // A month that ran out is no refusal for the next one: the limit and the warn ratio carry, never the usage or
        // either stamp, so the batch that opens the month is admitted.
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("accepted").GetInt32().ShouldBe(1);

        var carried = (await ReadAsync(counters, world)).ShouldNotBeNull();
        carried.Used.ShouldBe(1m);
        carried.Limit.ShouldBe(10m);
        carried.WarnRatio.ShouldBe(WarnRatio);
        carried.WarnedAt.ShouldBeNull();
        carried.BlockedAt.ShouldBeNull();

        // The month it carried from is left exactly as its block left it, and the month that opened holds that one
        // counter: no second row, and no warning, since one unit of ten is far from the line.
        (await counters.CounterAsync(world.Org, world.Project, spentMonth)).ShouldBe(spent);
        (await world.CountAsync("quota_counter", $"org_id = '{world.Org:D}'")).ShouldBe(2L);
        (await WarningRowsAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task IngestEvents_ReplayedWarningBatch_DoesNotChargeOrAnnounceAgain()
    {
        await using var world = await StartIngestAsync();
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(counters, world, used: 7m, limit: 10m);
        var body = EventBatch(world, 1, 1);

        var first = await PostAsync(world, body);
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        var crossed = (await ReadAsync(counters, world)).ShouldNotBeNull();
        crossed.Used.ShouldBe(8m);
        crossed.WarnedAt.ShouldNotBeNull();
        (await WarningRowsAsync(world)).ShouldBe(1L);

        // The resend adds no row, so it neither charges the counter again nor announces the crossing twice.
        var replay = await PostAsync(world, body);
        replay.Status.ShouldBe(HttpStatusCode.OK, replay.ErrorMessage);
        replay.Body.GetProperty("accepted").GetInt32().ShouldBe(0);
        replay.Body.GetProperty("duplicates").GetInt32().ShouldBe(1);
        (await ReadAsync(counters, world)).ShouldBe(crossed);
        (await WarningRowsAsync(world)).ShouldBe(1L);
        (await EventRowsAsync(world)).ShouldBe(1);

        var later = await PostAsync(world, EventBatch(world, 2, 1));
        later.Status.ShouldBe(HttpStatusCode.OK, later.ErrorMessage);
        (await ReadAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(9m);
        (await WarningRowsAsync(world)).ShouldBe(1L);
    }

    [Fact]
    public async Task IngestEvents_WarningCrossing_LeavesForeignOrgCounterAndOutboxUntouched()
    {
        await using var world = await StartIngestAsync();
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(counters, world, used: 7m, limit: 10m);
        var (otherOrg, otherProject) = await world.CreateOtherOrgAsync(Unique.Slug("quota-foreign"));

        // A foreign project already on the same warn line: only this tenant's counter may answer this crossing.
        await counters.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(), otherProject, await PeriodAsync(world), "results", 7m, 10m, WarnRatio,
            Org: otherOrg));
        var foreignBefore = (await counters.CounterAsync(otherOrg, otherProject, await PeriodAsync(world)))
            .ShouldNotBeNull();

        var answer = await PostAsync(world, EventBatch(world, 1, 1));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        (await ReadAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(8m);
        (await WarningRowsAsync(world)).ShouldBe(1L);
        (await counters.CounterAsync(otherOrg, otherProject, await PeriodAsync(world))).ShouldBe(foreignBefore);
        (await world.CountAsync("outbox", $"org_id = '{otherOrg:D}' and task_type = '{WarningTask}'")).ShouldBe(0L);
    }

    [Fact]
    public async Task IngestEvents_UnknownExecutionKeyRefusal_LeavesCounterAndWarningsUntouched()
    {
        await using var world = await StartIngestAsync();
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(counters, world, used: 7m, limit: 10m);

        // One planned execution, so a key of this shape is not one of the run's: the batch is refused before its
        // insert, and the gate — which runs after the insert — is never reached.
        var invalid = IngestQaRequests.Event(
            Guid.CreateVersion7(), new string('f', 64), 1, type: "test.step", status: null);
        var answer = await PostAsync(world, IngestQaRequests.Batch(invalid));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("unknown_execution_key");
        var unchanged = (await ReadAsync(counters, world)).ShouldNotBeNull();
        unchanged.Used.ShouldBe(7m);
        unchanged.WarnedAt.ShouldBeNull();
        (await WarningRowsAsync(world)).ShouldBe(0L);
        (await EventRowsAsync(world)).ShouldBe(0);
        (await BatchRowsAsync(world)).ShouldBe(0);

        // The positive control: the same counter warns for a batch the run's plan admits.
        (await PostAsync(world, EventBatch(world, 1, 1))).Status.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync(counters, world)).ShouldNotBeNull().Used.ShouldBe(8m);
        (await WarningRowsAsync(world)).ShouldBe(1L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IngestEvents_QuotaGateEnabledFlag_ControlsAdmissionWithoutCounterWrites(bool enabled)
    {
        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["EVIDGATE:Usage:QuotaGate:Enabled"] = enabled.ToString(CultureInfo.InvariantCulture),
        };
        await using var world = await StartIngestAsync(configuration: configuration);
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);

        // A scope with no room at all: only the switch can let the batch through, and it may not write a counter.
        await SeedAsync(counters, world, used: 10m, limit: 10m);
        var before = (await ReadAsync(counters, world)).ShouldNotBeNull();

        var answer = await PostAsync(world, EventBatch(world, 1, 3));

        answer.Status.ShouldBe(enabled ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK, answer.ErrorMessage);
        var after = (await ReadAsync(counters, world)).ShouldNotBeNull();

        if (enabled)
        {
            // The refusal is what a switched-on gate answers, and the stamp the route writes after the rollback is the
            // only counter write it leaves: the switch itself neither charges nor warns.
            after.Used.ShouldBe(before.Used);
            after.WarnedAt.ShouldBeNull();
            after.BlockedAt.ShouldNotBeNull("the refusal is stamped durably, after the batch's rollback");
            after.Version.ShouldBe(before.Version + 1, "and the stamp is the one write the refusal made");
        }
        else
        {
            after.ShouldBe(before, "a switched-off gate reads and writes no counter");
        }

        (await WarningRowsAsync(world)).ShouldBe(0L);
        (await EventRowsAsync(world)).ShouldBe(enabled ? 0 : 3);
    }
}
