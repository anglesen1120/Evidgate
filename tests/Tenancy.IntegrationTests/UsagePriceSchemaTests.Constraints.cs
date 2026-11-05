// The closed value vocabularies, the one card version per scope and metric, and the non-overlap rule of one
// scope and metric: the three refusals the card load maps to a caller's mistake.
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class UsagePriceSchemaTests
{
    [Fact]
    public async Task Refusals_AreNamedByTheirOwnConstraint()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var org = Guid.CreateVersion7();

        await InsertAsync(database, org, PriceRow(Guid.CreateVersion7(), org, effectiveTo: At(2)));

        // A negative price, a currency that is not three uppercase letters, an empty window, and a
        // metric outside the closed metering list: each answers 23514 with its own constraint name, so
        // a caller can name what it sent wrong without the message echoing the value. Each refused row
        // takes a free window and its own card version: the guards run before the checks, so an
        // overlapping or repeated row would answer as that first.
        await AssertRefusedAsync(
            database,
            org,
            PriceRow(Guid.CreateVersion7(), org, unitPrice: -0.01m, effectiveFrom: At(6), effectiveTo: At(7), version: "card-negative"),
            "23514",
            "usage_price_unit_price_check");
        await AssertRefusedAsync(
            database,
            org,
            PriceRow(Guid.CreateVersion7(), org, currency: "usd", effectiveFrom: At(8), effectiveTo: At(9), version: "card-currency"),
            "23514",
            "usage_price_currency_check");
        await AssertRefusedAsync(
            database,
            org,
            PriceRow(
                Guid.CreateVersion7(),
                org,
                effectiveFrom: At(3),
                effectiveTo: At(2),
                version: "card-window"),
            "23514",
            "usage_price_window_check");
        await AssertRefusedAsync(
            database,
            org,
            PriceRow(Guid.CreateVersion7(), org, metric: "widgets", effectiveFrom: At(10), effectiveTo: At(11), version: "card-metric"),
            "23514",
            "usage_price_metric_check");
        await AssertRefusedAsync(
            database,
            org,
            PriceRow(Guid.CreateVersion7(), org, effectiveFrom: At(12), effectiveTo: At(12).AddMonths(1), version: string.Empty),
            "23514",
            "usage_price_rate_card_version_check");
    }

    [Fact]
    public async Task DuplicateCardVersion_IsRefused_ButAnotherScopeOrMetricIsNot()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var orgA = Guid.CreateVersion7();
        var orgB = Guid.CreateVersion7();

        await InsertAsync(database, orgA, PriceRow(Guid.CreateVersion7(), orgA, effectiveFrom: At(1), effectiveTo: At(2)));

        // Same scope, metric and version, in a window that does not overlap: the unique index answers
        // a card reload rather than the caller diffing rows.
        await AssertRefusedAsync(
            database,
            orgA,
            PriceRow(Guid.CreateVersion7(), orgA, effectiveFrom: At(6), effectiveTo: At(7)),
            "23505",
            "usage_price_card_key");

        // The same reload in a window that also overlaps an existing row still answers as a duplicate:
        // the guard's version check runs under the unique index's name before the window rule, so the
        // store's duplicate_version mapping does not depend on which window the retry used.
        await AssertRefusedAsync(
            database,
            orgA,
            PriceRow(Guid.CreateVersion7(), orgA, effectiveFrom: At(1), effectiveTo: At(2)),
            "23505",
            "usage_price_card_key");

        // A different metric and a different tenant are different scopes: both accept the same version.
        await InsertAsync(
            database,
            orgA,
            PriceRow(Guid.CreateVersion7(), orgA, metric: "ai_tokens", version: "card-1"));
        await InsertAsync(database, orgB, PriceRow(Guid.CreateVersion7(), orgB, metric: "ai_tokens", version: "card-1"));
    }

    [Fact]
    public async Task OverlappingWindows_AreRefusedInOneScope_AndAdjacentAndOverridesAreNot()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var org = Guid.CreateVersion7();

        await InsertAsync(
            database,
            org,
            PriceRow(Guid.CreateVersion7(), org, effectiveFrom: At(1), effectiveTo: At(3)));

        // [Jan, Mar) overlaps [Feb, Apr) on February: one instant may carry one price per scope and
        // metric, or the resolver's answer would depend on row order.
        await AssertRefusedAsync(
            database,
            org,
            PriceRow(Guid.CreateVersion7(), org, effectiveFrom: At(2), effectiveTo: At(4), version: "card-2"),
            "23514",
            "usage_price_no_overlap");

        // Adjacent windows share only the boundary, which is open: [Jan, Mar) + [Mar, May) is legal.
        await InsertAsync(
            database,
            org,
            PriceRow(Guid.CreateVersion7(), org, effectiveFrom: At(3), effectiveTo: At(5), version: "card-3"));

        // A NULL end is infinity, so it overlaps everything the same metric prices after its start.
        await InsertAsync(
            database,
            org,
            PriceRow(Guid.CreateVersion7(), org, metric: "ai_tokens", effectiveFrom: At(6), effectiveTo: null, version: "card-4"));
        await AssertRefusedAsync(
            database,
            org,
            PriceRow(Guid.CreateVersion7(), org, metric: "ai_tokens", effectiveFrom: At(9), effectiveTo: null, version: "card-5"),
            "23514",
            "usage_price_no_overlap");

        // An override covers the window the platform card covers: override-first is the point of the
        // table, so the guard compares only rows of the same scope. [May, Jun) starts where the org's own
        // [Mar, May) ends and sits inside the global card's open window.
        var globalId = await InsertGlobalCardAsync(database, "card-global", At(1), null);
        (globalId == Guid.Empty).ShouldBeFalse();
        await InsertAsync(
            database,
            org,
            PriceRow(
                Guid.CreateVersion7(),
                org,
                effectiveFrom: At(5),
                effectiveTo: At(6),
                version: "card-override"));
    }
}
