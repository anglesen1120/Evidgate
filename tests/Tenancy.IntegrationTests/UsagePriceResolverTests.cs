using System.Globalization;
using System.Text;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Application;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The rate card against a real PostgreSQL: the global card written through the security-definer function, an org's own
/// override written on its tenant transaction, the lookup's precedence and half-open range, and the two refusals the
/// database arbitrates — a version already loaded and a range that overlaps a stored row.
/// <para>
/// Each case runs on a private database: global rows belong to no tenant, so they are visible to every org and cannot be
/// isolated by tenant the way the shared database's cases are.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed class UsagePriceResolverTests
{
    private static readonly DateTimeOffset January = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset February = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset March = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset April = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly PostgresTenancyFixture _fixture;

    public UsagePriceResolverTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>One private database, its host, and the two orgs every case needs.</summary>
    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid Org,
        Guid OtherOrg,
        DedicatedDatabase Dedicated) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
            await Dedicated.DisposeAsync();
        }
    }

    private Task<World> StartAsync() =>
        _fixture.StartDedicatedAsync(async dedicated =>
        {
            var database = dedicated.Name;
            var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
            var client = api.CreateClient();
            var user = Guid.CreateVersion7();
            var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
            var otherOrg = await CreateOrgAsync(client, user, Unique.Slug("other"));

            return new World(api, client, database, org, otherOrg, dedicated);
        });

    // ------------------------------------------------------------------ helpers

    private static string Card(
        string version,
        string unitPrice = "0.0100",
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string metric = "results") =>
        CardOfRows(version, Row(metric, unitPrice, from ?? January, to));

    private static string CardOfRows(string version, params string[] rows) =>
        "{\"rate_card_version\":\"" + version + "\",\"prices\":[" + string.Join(",", rows) + "]}";

    private static string Row(string metric, string unitPrice, DateTimeOffset from, DateTimeOffset? to) =>
        "{\"metric\":\"" + metric
        + "\",\"unit_price\":\"" + unitPrice + "\",\"currency\":\"USD\",\"effective_from\":\""
        + Instant(from) + "\",\"effective_to\":"
        + (to is null ? "null" : "\"" + Instant(to.Value) + "\"")
        + ",\"included_quota\":null}";

    private static string Instant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static byte[] Bytes(string card) => Encoding.UTF8.GetBytes(card);

    private static async Task<UsagePriceLoadResult> LoadGlobalAsync(World world, string card)
    {
        var token = TestContext.Current.CancellationToken;
        await using var services = world.Api.Services.CreateAsyncScope();
        var loader = services.ServiceProvider.GetRequiredService<UsagePriceLoader>();

        return await loader.LoadAsync(UsagePriceScope.Global, Bytes(card), token);
    }

    private static async Task<UsagePriceLoadResult> LoadOrgAsync(World world, Guid org, string card)
    {
        var token = TestContext.Current.CancellationToken;
        await using var services = world.Api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var scopes = provider.GetRequiredService<ITenantScopeFactory>();

        await using var scope = await scopes.BeginAsync(org, null, false, token);
        var loader = provider.GetRequiredService<UsagePriceLoader>();
        var result = await loader.LoadAsync(UsagePriceScope.Org, Bytes(card), token);
        await scope.CommitAsync(token);

        return result;
    }

    private static async Task<ResolvedUsagePrice?> ResolveAsync(
        World world,
        Guid org,
        UsageMetric metric,
        DateTimeOffset at)
    {
        var token = TestContext.Current.CancellationToken;
        await using var services = world.Api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var scopes = provider.GetRequiredService<ITenantScopeFactory>();

        await using var scope = await scopes.BeginAsync(org, null, true, token);
        var resolver = provider.GetRequiredService<IUsagePriceResolver>();

        return await resolver.ResolveAsync(metric, at, token);
    }

    private Task<long> CountAsync(World world, string version, Guid? org = null)
    {
        var scope = org is null
            ? "org_id is null"
            : "org_id = '" + org.Value.ToString("D", CultureInfo.InvariantCulture) + "'";

        return _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.usage_price where rate_card_version = '{version}' and {scope}");
    }

    /// <summary>The metric and unit price a stored row holds, read by the id the load returned.</summary>
    private async Task<(string Metric, decimal UnitPrice)> RowAsync(World world, Guid priceId, Guid? org)
    {
        var scope = org is null
            ? "org_id is null"
            : "org_id = '" + org.Value.ToString("D", CultureInfo.InvariantCulture) + "'";
        var filter = " where id = '" + priceId.ToString("D", CultureInfo.InvariantCulture) + "' and " + scope;

        var metric = await _fixture.ScalarAsync<string>(
            world.Database,
            "select metric from public.usage_price" + filter);
        var unitPrice = await _fixture.ScalarAsync<decimal>(
            world.Database,
            "select unit_price from public.usage_price" + filter);

        return (metric, unitPrice);
    }

    /// <summary>One ledger row that points at <paramref name="priceRef"/>, written as the app role inside a tenant scope.</summary>
    private async Task<Guid> InsertLedgerRowAsync(World world, Guid org, Guid priceRef, decimal amount)
    {
        var id = Guid.CreateVersion7();

        await using var scope = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(world.Database), org);

        await scope.ExecuteAsync(
            """
            insert into public.usage_ledger_entry
                (id, org_id, metric, quantity, unit, period, billing_key, occurred_at, price_ref, amount, currency)
            values (@id, @org_id, 'results', 1, 'batch', '2026-01', @billing_key, @occurred_at, @price_ref, @amount, 'USD')
            """,
            ("id", id),
            ("org_id", org),
            ("billing_key", "price-test:" + id.ToString("N", CultureInfo.InvariantCulture)),
            ("occurred_at", January),
            ("price_ref", priceRef),
            ("amount", amount));
        await scope.CommitAsync();

        return id;
    }

    // ------------------------------------------------------------------ precedence and range

    [Fact]
    public async Task Resolve_WithoutAnOverride_ReturnsTheGlobalPrice()
    {
        await using var world = await StartAsync();
        (await LoadGlobalAsync(world, Card("global-2026"))).Succeeded.ShouldBeTrue();

        var price = (await ResolveAsync(world, world.Org, UsageMetric.Results, March)).ShouldNotBeNull();

        price.OrgId.ShouldBeNull();
        price.UnitPrice.ShouldBe(0.0100m);
        price.RateCardVersion.ShouldBe("global-2026");
    }

    [Fact]
    public async Task Resolve_OrgOverride_BeatsTheGlobalCard()
    {
        await using var world = await StartAsync();
        (await LoadGlobalAsync(world, Card("global-2026"))).Succeeded.ShouldBeTrue();
        (await LoadOrgAsync(world, world.Org, Card("org-2026", unitPrice: "0.0200"))).Succeeded.ShouldBeTrue();

        var price = (await ResolveAsync(world, world.Org, UsageMetric.Results, March)).ShouldNotBeNull();

        price.OrgId.ShouldBe(world.Org);
        price.UnitPrice.ShouldBe(0.0200m);
        price.RateCardVersion.ShouldBe("org-2026");
    }

    [Fact]
    public async Task Resolve_AtEffectiveFrom_Resolves()
    {
        await using var world = await StartAsync();
        (await LoadGlobalAsync(world, Card("global-2026", to: February))).Succeeded.ShouldBeTrue();

        var price = (await ResolveAsync(world, world.Org, UsageMetric.Results, January)).ShouldNotBeNull();

        price.RateCardVersion.ShouldBe("global-2026");
    }

    [Fact]
    public async Task Resolve_AtEffectiveTo_DoesNotResolveAndTheNextRowDoes()
    {
        await using var world = await StartAsync();
        (await LoadGlobalAsync(world, Card("global-january", to: February))).Succeeded.ShouldBeTrue();
        (await LoadGlobalAsync(world, Card("global-february", unitPrice: "0.0200", from: February))).Succeeded.ShouldBeTrue();

        var atTheBoundary = (await ResolveAsync(world, world.Org, UsageMetric.Results, February)).ShouldNotBeNull();
        // One microsecond, the column's own precision: a sub-microsecond instant would be rounded onto the boundary.
        var justBefore = (await ResolveAsync(world, world.Org, UsageMetric.Results, February.AddTicks(-10)))
            .ShouldNotBeNull();

        atTheBoundary.RateCardVersion.ShouldBe("global-february");
        atTheBoundary.UnitPrice.ShouldBe(0.0200m);
        justBefore.RateCardVersion.ShouldBe("global-january");
    }

    [Fact]
    public async Task Resolve_BeforeTheFirstRow_ReturnsNull()
    {
        await using var world = await StartAsync();
        (await LoadGlobalAsync(world, Card("global-2026", from: February))).Succeeded.ShouldBeTrue();

        (await ResolveAsync(world, world.Org, UsageMetric.Results, January)).ShouldBeNull();
        (await ResolveAsync(world, world.Org, UsageMetric.Results, February)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Resolve_AnotherOrgsOverride_NeverApplies()
    {
        await using var world = await StartAsync();
        (await LoadGlobalAsync(world, Card("global-2026"))).Succeeded.ShouldBeTrue();
        (await LoadOrgAsync(world, world.OtherOrg, Card("other-2026", unitPrice: "0.0300"))).Succeeded.ShouldBeTrue();

        var mine = (await ResolveAsync(world, world.Org, UsageMetric.Results, March)).ShouldNotBeNull();
        var theirs = (await ResolveAsync(world, world.OtherOrg, UsageMetric.Results, March)).ShouldNotBeNull();

        mine.RateCardVersion.ShouldBe("global-2026");
        mine.OrgId.ShouldBeNull();
        theirs.RateCardVersion.ShouldBe("other-2026");
        theirs.OrgId.ShouldBe(world.OtherOrg);
    }

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

    [Fact]
    public async Task LoadOrg_ConcurrentCardsWithReversedMetricOrder_AllSucceed()
    {
        await using var world = await StartAsync();
        var token = TestContext.Current.CancellationToken;

        // One tenant scope per async flow: the factory refuses two in the same flow, so each load gets its own
        // task, and the rendezvous releases both at the same instant.
        using var arrived = new SemaphoreSlim(0, 2);
        using var go = new SemaphoreSlim(0, 2);

        for (var round = 0; round < 4; round++)
        {
            var start = January.AddDays(2 * round);
            var middle = start.AddDays(1);
            var end = start.AddDays(2);
            var tag = "org-c" + round;

            // Same two metrics, opposite orders, disjoint windows: with card order kept the two statements would
            // wait on each other's advisory locks.
            var forward = CardOfRows(
                tag + "-forward",
                Row("results", "0.0100", start, middle),
                Row("ai_tokens", "0.0200", start, middle));
            var reverse = CardOfRows(
                tag + "-reverse",
                Row("ai_tokens", "0.0300", middle, end),
                Row("results", "0.0400", middle, end));

            async Task<UsagePriceLoadResult> LoadInOwnFlowAsync(string card)
            {
                await using var services = world.Api.Services.CreateAsyncScope();
                var provider = services.ServiceProvider;
                await using var scope = await provider.GetRequiredService<ITenantScopeFactory>()
                    .BeginAsync(world.Org, null, false, token);
                var loader = provider.GetRequiredService<UsagePriceLoader>();

                arrived.Release();
                await go.WaitAsync(token);

                var result = await loader.LoadAsync(UsagePriceScope.Org, Bytes(card), token);
                await scope.CommitAsync(token);
                return result;
            }

            var first = Task.Run(() => LoadInOwnFlowAsync(forward), token);
            var second = Task.Run(() => LoadInOwnFlowAsync(reverse), token);

            await arrived.WaitAsync(token);
            await arrived.WaitAsync(token);
            go.Release(2);

            var results = await Task.WhenAll(first, second);

            results.ShouldAllBe(result => result.Succeeded);
        }
    }

    [Fact]
    public async Task LoadGlobal_ConcurrentCardsWithReversedMetricOrder_AllSucceed()
    {
        await using var world = await StartAsync();
        var token = TestContext.Current.CancellationToken;
        using var arrived = new SemaphoreSlim(0, 2);
        using var go = new SemaphoreSlim(0, 2);

        for (var round = 0; round < 4; round++)
        {
            var start = January.AddDays(2 * round);
            var middle = start.AddDays(1);
            var end = start.AddDays(2);
            var tag = "global-c" + round;

            var forward = CardOfRows(
                tag + "-forward",
                Row("results", "0.0100", start, middle),
                Row("ai_tokens", "0.0200", start, middle));
            var reverse = CardOfRows(
                tag + "-reverse",
                Row("ai_tokens", "0.0300", middle, end),
                Row("results", "0.0400", middle, end));

            async Task<UsagePriceLoadResult> LoadInOwnFlowAsync(string card)
            {
                await using var services = world.Api.Services.CreateAsyncScope();
                var loader = services.ServiceProvider.GetRequiredService<UsagePriceLoader>();

                arrived.Release();
                await go.WaitAsync(token);

                return await loader.LoadAsync(UsagePriceScope.Global, Bytes(card), token);
            }

            var first = Task.Run(() => LoadInOwnFlowAsync(forward), token);
            var second = Task.Run(() => LoadInOwnFlowAsync(reverse), token);

            await arrived.WaitAsync(token);
            await arrived.WaitAsync(token);
            go.Release(2);

            var results = await Task.WhenAll(first, second);

            results.ShouldAllBe(result => result.Succeeded);
        }
    }

    [Fact]
    public async Task Load_RefusedCard_LeavesTheCallerTransactionUsable()
    {
        var token = TestContext.Current.CancellationToken;
        await using var world = await StartAsync();
        (await LoadOrgAsync(world, world.Org, Card("org-v1", to: March))).Succeeded.ShouldBeTrue();

        await using var services = world.Api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var scopes = provider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(world.Org, null, false, token);
        var loader = provider.GetRequiredService<UsagePriceLoader>();

        var refused = await loader.LoadAsync(
            UsagePriceScope.Org,
            Bytes(Card("org-v2", from: February, to: April)),
            token);

        refused.Succeeded.ShouldBeFalse();

        // The savepoint rolled the statement back, so the caller's unit of work still runs and still commits.
        await using (var command = scope.Connection.CreateCommand())
        {
            command.CommandText = "select 1";
            command.Transaction = scope.Transaction;
            (await command.ExecuteScalarAsync(token)).ShouldBe(1);
        }

        await scope.CommitAsync(token);
        (await CountAsync(world, "org-v2", world.Org)).ShouldBe(0);
    }

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
