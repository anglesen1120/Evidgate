using System.Globalization;
using System.Text;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Application;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Microsoft.Extensions.DependencyInjection;
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
public sealed partial class UsagePriceResolverTests
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
}
