// The quota counter's storage shape: the page headroom that keeps the gate's in-place update heap-only.
using Evidgate.Modules.Usage.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <c>quota_counter</c> rows are updated in place by every admitted call; the table keeps page headroom
/// and no indexed column changes, so the update is heap-only.
/// </summary>
public sealed partial class UsageQuotaGateTests
{
    private const int StorageGateCalls = 6;

    [Fact]
    public async Task QuotaCounter_KeepsPageHeadroom_ForTheGatesInPlaceUpdates()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);

        (await world.ScalarAsync<string>(
            "select coalesce(array_to_string(reloptions, ','), '') from pg_catalog.pg_class "
            + "where oid = 'public.quota_counter'::regclass"))
            .ShouldContain("fillfactor=80", customMessage: "0086 sets fillfactor 80 on the gate's update target");
    }

    [Fact]
    public async Task QuotaGate_UpdatesTheCounterAsHeapOnlyUpdates()
    {
        await using var world = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedAsync(world, used: 0m);

        for (var call = 0; call < StorageGateCalls; call++)
        {
            (await world.CheckAsync(Request(world, 1m))).Decision.ShouldBe(QuotaDecision.Ok);
        }

        // Statistics reach the view when the writing backend flushes them, which can lag the commit.
        (long Updates, long HotUpdates) counts = default;
        for (var attempt = 0; attempt < 50 && counts.Updates < StorageGateCalls; attempt++)
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
            counts = (
                await world.ScalarAsync<long>(
                    "select coalesce(n_tup_upd, 0) from pg_catalog.pg_stat_all_tables "
                    + "where relid = 'public.quota_counter'::regclass"),
                await world.ScalarAsync<long>(
                    "select coalesce(n_tup_hot_upd, 0) from pg_catalog.pg_stat_all_tables "
                    + "where relid = 'public.quota_counter'::regclass"));
        }

        counts.Updates.ShouldBeGreaterThanOrEqualTo(StorageGateCalls);
        counts.HotUpdates.ShouldBe(
            counts.Updates,
            "no indexed column changes and the page has headroom, so every gate update is heap-only");
    }
}
