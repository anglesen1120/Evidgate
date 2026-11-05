using Evidgate.Modules.Usage.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Money and history: a unit price of twelve fraction digits survives storage unchanged, and loading a later card leaves
/// an earlier ledger row's price reference and amount as they were. Split out of the suite file to keep each file under
/// the size cap.
/// </summary>
public sealed partial class UsagePriceResolverTests
{
    // ------------------------------------------------------------------ money and history

    [Fact]
    public async Task Resolve_TwelveFractionDigits_RoundTripsExactly()
    {
        await using var world = await StartAsync();
        (await LoadOrgAsync(world, world.Org, Card("org-2026", unitPrice: "0.123456789012"))).Succeeded.ShouldBeTrue();

        var price = (await ResolveAsync(world, world.Org, UsageMetric.Results, March)).ShouldNotBeNull();

        price.UnitPrice.ShouldBe(0.123456789012m);
    }

    [Fact]
    public async Task Load_NewPriceRow_LeavesAnEarlierLedgerRowsPriceRefUnchanged()
    {
        await using var world = await StartAsync();
        var first = await LoadOrgAsync(world, world.Org, Card("org-v1", to: February));
        first.Succeeded.ShouldBeTrue();
        var priceId = first.PriceIds.ShouldHaveSingleItem();

        var ledgerId = await InsertLedgerRowAsync(world, world.Org, priceId, 0.0100m);

        (await LoadOrgAsync(world, world.Org, Card("org-v2", unitPrice: "0.0200", from: February))).Succeeded.ShouldBeTrue();

        var stored = await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select price_ref from public.usage_ledger_entry where id = '{ledgerId:D}'");
        var amount = await _fixture.ScalarAsync<decimal>(
            world.Database,
            $"select amount from public.usage_ledger_entry where id = '{ledgerId:D}'");

        stored.ShouldBe(priceId);
        amount.ShouldBe(0.0100m);
    }
}
