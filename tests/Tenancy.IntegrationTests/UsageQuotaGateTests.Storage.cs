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
    private const int StorageGateCalls = 9;

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
    public async Task QuotaGate_UpdatesTheCounterAsHeapOnlyUpdates_AcrossTheWarningLine()
    {
        // A private database: the counters are table-wide, so no other test's update may be in them.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await UsageQuotaWorld.StartAsync(_fixture, database: dedicated.Name);
        await SeedAsync(world, used: 0m);

        // Nine unit calls cross the warning line (8 of 10), which also writes warned_at.
        for (var call = 0; call < StorageGateCalls; call++)
        {
            (await world.CheckAsync(Request(world, 1m))).Decision.ShouldBeOneOf(QuotaDecision.Ok, QuotaDecision.Warn);
        }

        (await world.CounterAsync(project: world.Project)).ShouldNotBeNull().Used.ShouldBe(StorageGateCalls);

        // Statistics reach the view when the writing backend flushes them, which can lag the commit.
        string counts = string.Empty;
        for (var attempt = 0; attempt < 50 && !counts.StartsWith(StorageGateCalls + "|", StringComparison.Ordinal); attempt++)
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
            counts = await world.ScalarAsync<string>(
                "select coalesce(n_tup_upd, 0)::text || '|' || coalesce(n_tup_hot_upd, 0)::text "
                + "from pg_catalog.pg_stat_all_tables where relid = 'public.quota_counter'::regclass");
        }

        counts.ShouldBe(
            $"{StorageGateCalls}|{StorageGateCalls}",
            "every gate update is heap-only: no indexed column changes");
    }
}
