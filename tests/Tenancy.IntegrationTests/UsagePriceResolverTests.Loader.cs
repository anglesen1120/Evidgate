using Evidgate.Modules.Usage.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// What a load stores and what it refuses: a version already loaded and a range that overlaps a stored row are the
/// database's decisions, a refused multi-row card writes none of its rows, and a stored card keeps the card's own row
/// order in the ids it returns. Split out of the suite file to keep each file under the size cap.
/// </summary>
public sealed partial class UsagePriceResolverTests
{
    // ------------------------------------------------------------------ refusals

    [Fact]
    public async Task Load_SameVersionTwice_RefusedAndOneRowStays()
    {
        await using var world = await StartAsync();
        (await LoadOrgAsync(world, world.Org, Card("org-2026"))).Succeeded.ShouldBeTrue();

        var again = await LoadOrgAsync(world, world.Org, Card("org-2026"));

        again.Succeeded.ShouldBeFalse();
        again.Violations.ShouldContain(violation =>
            violation.Reason == UsagePriceRefusalReasons.DuplicateVersion);
        (await CountAsync(world, "org-2026", world.Org)).ShouldBe(1);
    }

    [Fact]
    public async Task Load_RangeOverlappingAStoredRow_RefusedAndWritesNothing()
    {
        await using var world = await StartAsync();
        (await LoadOrgAsync(world, world.Org, Card("org-v1", to: March))).Succeeded.ShouldBeTrue();

        var overlapping = await LoadOrgAsync(
            world, world.Org, Card("org-v2", unitPrice: "0.0200", from: February, to: April));

        overlapping.Succeeded.ShouldBeFalse();
        overlapping.Violations.ShouldContain(violation =>
            violation.Reason == UsagePriceRefusalReasons.OverlappingRange);
        (await CountAsync(world, "org-v2", world.Org)).ShouldBe(0);
    }

    [Fact]
    public async Task Load_GlobalVersionTwice_RefusedAndOneRowStays()
    {
        await using var world = await StartAsync();
        (await LoadGlobalAsync(world, Card("global-2026"))).Succeeded.ShouldBeTrue();

        var again = await LoadGlobalAsync(world, Card("global-2026"));

        again.Succeeded.ShouldBeFalse();
        again.Violations.ShouldContain(violation =>
            violation.Reason == UsagePriceRefusalReasons.DuplicateVersion);
        (await CountAsync(world, "global-2026")).ShouldBe(1);
    }

    [Fact]
    public async Task Load_GlobalRangeOverlappingAStoredGlobalRow_Refused()
    {
        await using var world = await StartAsync();
        (await LoadGlobalAsync(world, Card("global-v1", to: March))).Succeeded.ShouldBeTrue();

        var overlapping = await LoadGlobalAsync(world, Card("global-v2", from: February, to: April));

        overlapping.Succeeded.ShouldBeFalse();
        overlapping.Violations.ShouldContain(violation =>
            violation.Reason == UsagePriceRefusalReasons.OverlappingRange);
        (await CountAsync(world, "global-v2")).ShouldBe(0);
    }

    // ------------------------------------------------------------------ a whole card, two metrics

    [Fact]
    public async Task LoadOrg_TwoRowCard_SecondRowConflicts_NeitherRowPersists()
    {
        await using var world = await StartAsync();
        (await LoadOrgAsync(world, world.Org, CardOfRows(
            "org-base",
            Row("results", "0.0100", January, February),
            Row("ai_tokens", "0.0200", January, February)))).Succeeded.ShouldBeTrue();

        var conflicting = await LoadOrgAsync(world, world.Org, CardOfRows(
            "org-v2",
            Row("results", "0.0300", February, March),
            Row("ai_tokens", "0.0400", January, March)));

        conflicting.Succeeded.ShouldBeFalse();
        conflicting.PriceIds.ShouldBeEmpty();
        conflicting.Violations.ShouldContain(violation =>
            violation.Reason == UsagePriceRefusalReasons.OverlappingRange);

        // The first row of the card had a clear window; the refused card still wrote neither row.
        (await CountAsync(world, "org-v2", world.Org)).ShouldBe(0);
        (await CountAsync(world, "org-base", world.Org)).ShouldBe(2);
    }

    [Fact]
    public async Task LoadGlobal_TwoRowCard_SecondRowConflicts_NeitherRowPersists()
    {
        await using var world = await StartAsync();
        (await LoadGlobalAsync(world, CardOfRows(
            "global-base",
            Row("results", "0.0100", January, February),
            Row("ai_tokens", "0.0200", January, February)))).Succeeded.ShouldBeTrue();

        var conflicting = await LoadGlobalAsync(world, CardOfRows(
            "global-v2",
            Row("results", "0.0300", February, March),
            Row("ai_tokens", "0.0400", January, March)));

        conflicting.Succeeded.ShouldBeFalse();
        conflicting.PriceIds.ShouldBeEmpty();
        conflicting.Violations.ShouldContain(violation =>
            violation.Reason == UsagePriceRefusalReasons.OverlappingRange);
        (await CountAsync(world, "global-v2")).ShouldBe(0);
        (await CountAsync(world, "global-base")).ShouldBe(2);
    }

    [Fact]
    public async Task LoadOrg_TwoRowCard_PersistsBothRowsAndReturnsIdsInCardOrder()
    {
        await using var world = await StartAsync();

        var loaded = await LoadOrgAsync(world, world.Org, CardOfRows(
            "org-pair",
            Row("ai_tokens", "0.0200", January, February),
            Row("results", "0.0100", January, February)));

        loaded.Succeeded.ShouldBeTrue();
        loaded.PriceIds.Count.ShouldBe(2);

        // The rows are written in metric order so concurrent loads take the same locks, but the ids still follow the
        // card's own order.
        var first = await RowAsync(world, loaded.PriceIds[0], world.Org);
        var second = await RowAsync(world, loaded.PriceIds[1], world.Org);

        first.Metric.ShouldBe("ai_tokens");
        first.UnitPrice.ShouldBe(0.0200m);
        second.Metric.ShouldBe("results");
        second.UnitPrice.ShouldBe(0.0100m);
        (await CountAsync(world, "org-pair", world.Org)).ShouldBe(2);
    }

    [Fact]
    public async Task LoadGlobal_TwoRowCard_PersistsBothRowsAndReturnsIdsInCardOrder()
    {
        await using var world = await StartAsync();

        var loaded = await LoadGlobalAsync(world, CardOfRows(
            "global-pair",
            Row("ai_tokens", "0.0200", January, February),
            Row("results", "0.0100", January, February)));

        loaded.Succeeded.ShouldBeTrue();
        loaded.PriceIds.Count.ShouldBe(2);

        var first = await RowAsync(world, loaded.PriceIds[0], org: null);
        var second = await RowAsync(world, loaded.PriceIds[1], org: null);

        first.Metric.ShouldBe("ai_tokens");
        first.UnitPrice.ShouldBe(0.0200m);
        second.Metric.ShouldBe("results");
        second.UnitPrice.ShouldBe(0.0100m);
        (await CountAsync(world, "global-pair")).ShouldBe(2);
    }
}
