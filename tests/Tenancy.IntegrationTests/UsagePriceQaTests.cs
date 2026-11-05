using System.Globalization;
using System.Text;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Application;
using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Domain;
using Evidgate.Modules.Usage.Infrastructure.Parsing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class UsagePriceQaTests(PostgresTenancyFixture fixture)
{
    private static readonly DateTimeOffset January = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset February = January.AddMonths(1);
    private static readonly DateTimeOffset March = January.AddMonths(2);
    private static readonly DateTimeOffset April = January.AddMonths(3);

    [Theory]
    [InlineData("1e2")]
    [InlineData("\"1e2\"")]
    [InlineData("\"0x10\"")]
    [InlineData("\"1,5\"")]
    [InlineData("\"NaN\"")]
    [InlineData("-0")]
    [InlineData("\"-0\"")]
    [InlineData("\"-0.01\"")]
    [InlineData("\"+1\"")]
    [InlineData("\" 1\"")]
    [InlineData("true")]
    [InlineData("null")]
    public void Parse_NonDecimalAmount_RefusesPriceAndQuota(string token)
    {
        Parse(Card("valid", Row(amount: "0"))).Card.ShouldNotBeNull();
        Refuses(Card("bad", Row(amount: token)), "invalid_amount", "/prices/0/unit_price");
        if (token != "null")
        {
            Parse(Card("valid", Row(quota: "0"))).Card.ShouldNotBeNull();
            Refuses(Card("bad", Row(quota: token)), "invalid_amount", "/prices/0/included_quota");
        }
    }

    [Theory]
    [InlineData("0x10")]
    [InlineData("1,5")]
    [InlineData("NaN")]
    public void Parse_InvalidJsonAmount_RefusesDocument(string token)
    {
        Parse(Card("valid", Row())).Card.ShouldNotBeNull();
        Refuses(Card("bad", Row(amount: token)), "invalid_json", "/");
    }

    [Theory]
    [InlineData("99999999999999999999.123456789012")]
    [InlineData("12345678901234567890.123456789012")]
    public void Parse_AmountNotExactlyRepresentable_RefusesInsteadOfRounding(string token)
    {
        Parse(Card("valid", Row(amount: "\"1234567890123456.123456789012\"")))
            .Card.ShouldNotBeNull().Rows.ShouldHaveSingleItem().UnitPrice.ShouldBe(1234567890123456.123456789012m);
        Refuses(Card("bad", Row(amount: "\"" + token + "\"")), "invalid_amount", "/prices/0/unit_price");
        Refuses(Card("bad", Row(quota: token)), "invalid_amount", "/prices/0/included_quota");
    }

    [Theory]
    [InlineData("rate_card_version")]
    [InlineData("metric")]
    [InlineData("unit_price")]
    [InlineData("currency")]
    [InlineData("effective_from")]
    [InlineData("member_name")]
    public void Parse_LoneSurrogateEscape_RefusesWithoutThrowing(string member)
    {
        var json = Card("valid", Row());
        Parse(Card("unicode-\uD83D\uDE00", Row())).Card.ShouldNotBeNull();
        json = member switch
        {
            "rate_card_version" => json.Replace("\"valid\"", "\"\\uD800\"", StringComparison.Ordinal),
            "metric" => json.Replace("\"results\"", "\"\\uD800\"", StringComparison.Ordinal),
            "unit_price" => json.Replace("\"0.0100\"", "\"\\uD800\"", StringComparison.Ordinal),
            "currency" => json.Replace("\"USD\"", "\"\\uD800\"", StringComparison.Ordinal),
            "effective_from" => json.Replace("2026-01-01T00:00:00.0000000Z", "\\uD800", StringComparison.Ordinal),
            _ => json.Replace("\"currency\"", "\"\\uD800\"", StringComparison.Ordinal),
        };
        var result = Parse(json);
        result.Card.ShouldBeNull();
        result.Violations.ShouldNotBeEmpty();
    }

    [Fact]
    public void Parse_SubMicrosecondWindow_RefusesBeforeDatabaseTruncatesIt()
    {
        Parse(Card("valid", Row(to: January.AddTicks(10)))).Card.ShouldNotBeNull();
        Refuses(Card("bad", Row(to: January.AddTicks(1))), "invalid_instant", "/prices/0/effective_to");
        Refuses(Card("bad", Row(from: January.AddTicks(1))), "invalid_instant", "/prices/0/effective_from");
    }

    [Fact]
    public void Parse_ExactBodyCap_AcceptsButOneMoreByteRefusesBeforeJsonParsing()
    {
        var json = Card("valid", Row());
        var padding = UsagePriceCardRules.MaxBodyBytes - Encoding.UTF8.GetByteCount(json);
        Parse(json + new string(' ', padding)).Card.ShouldNotBeNull();
        Refuses(json + new string(' ', padding + 1), "too_large", "/");
        Refuses("{" + new string(' ', UsagePriceCardRules.MaxBodyBytes), "too_large", "/");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_DuplicateMemberAtBodyCap_RefusesNamedMember(bool root)
    {
        Parse(Card("valid", Row())).Card.ShouldNotBeNull();
        var json = root
            ? Card("valid", Row()).Replace("\"prices\":", "\"rate_card_version\":\"other\",\"prices\":", StringComparison.Ordinal)
            : Card("valid", Row(extra: ",\"unit_price\":\"0.0200\""));
        json += new string(' ', UsagePriceCardRules.MaxBodyBytes - Encoding.UTF8.GetByteCount(json));
        Refuses(json, "duplicate_member", root ? "/rate_card_version" : "/prices/0/unit_price");
    }

    [Theory]
    [InlineData("{", "invalid_json", "/")]
    [InlineData("[]", "invalid_value", "/")]
    [InlineData("null", "invalid_value", "/")]
    [InlineData("{}", "required", "/rate_card_version")]
    public void Parse_MalformedCard_RefusesWithReasonAndPath(string json, string reason, string path)
    {
        Parse(Card("valid", Row())).Card.ShouldNotBeNull();
        Refuses(json, reason, path);
    }

    [Fact]
    public async Task Resolve_AdjacentOverrides_UsesHalfOpenWindowsAndFallsBackAfterExpiry()
    {
        await using var world = await StartAsync();
        (await LoadAsync(world, Card("global", Row()), null)).Succeeded.ShouldBeTrue();
        var first = await LoadAsync(world, Card("first", Row(from: February, to: March, amount: "\"0.0200\"")), world.Org);
        var second = await LoadAsync(world, Card("second", Row(from: March, to: April, amount: "\"0.0300\"")), world.Org);
        first.Succeeded.ShouldBeTrue();
        second.Succeeded.ShouldBeTrue();
        (await ResolveAsync(world, world.Org, February.AddTicks(-10))).ShouldNotBeNull().RateCardVersion.ShouldBe("global");
        (await ResolveAsync(world, world.Org, February)).ShouldNotBeNull().PriceId.ShouldBe(first.PriceIds.Single());
        (await ResolveAsync(world, world.Org, March.AddTicks(-10))).ShouldNotBeNull().PriceId.ShouldBe(first.PriceIds.Single());
        (await ResolveAsync(world, world.Org, March)).ShouldNotBeNull().PriceId.ShouldBe(second.PriceIds.Single());
        (await ResolveAsync(world, world.Org, April)).ShouldNotBeNull().RateCardVersion.ShouldBe("global");
    }

    [Fact]
    public async Task Resolve_ForeignOverrideWithoutGlobal_ReturnsNullNotForeignPrice()
    {
        await using var world = await StartAsync();
        var loaded = await LoadAsync(world, Card("foreign", Row()), world.OtherOrg);
        loaded.Succeeded.ShouldBeTrue();
        (await ResolveAsync(world, world.OtherOrg, January)).ShouldNotBeNull().PriceId.ShouldBe(loaded.PriceIds.Single());
        (await ResolveAsync(world, world.Org, January)).ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Load_TwoRowsWithStoredOverlap_RollsBackWholeCardAndAllowsRecovery(bool global)
    {
        await using var world = await StartAsync();
        var org = global ? (Guid?)null : world.Org;
        (await LoadAsync(world, Card("existing", Row(metric: "ai_tokens")), org)).Succeeded.ShouldBeTrue();
        var result = await LoadAsync(world, Card("candidate", Row(), Row(metric: "ai_tokens")), org);
        result.Succeeded.ShouldBeFalse();
        result.PriceIds.ShouldBeEmpty();
        result.Violations.ShouldContain(v => v.Reason == "overlapping_range");
        (await CountAsync(world, "candidate")).ShouldBe(0);
        (await ResolveAsync(world, world.Org, January)).ShouldBeNull();
        (await LoadAsync(world, Card("candidate", Row()), org)).Succeeded.ShouldBeTrue();
        (await CountAsync(world, "candidate")).ShouldBe(1);
    }

    [Theory]
    [InlineData("unknown_field")]
    [InlineData("unknown_metric")]
    [InlineData("invalid_amount")]
    [InlineData("duplicate_member")]
    [InlineData("invalid_json")]
    [InlineData("overlapping_range")]
    [InlineData("duplicate_version")]
    public async Task Load_InvalidSecondRow_RefusesNamedReasonAndWritesNeitherRow(string reason)
    {
        await using var world = await StartAsync();
        var row = reason switch
        {
            "unknown_field" => Row(metric: "ai_tokens", extra: ",\"unit\":\"tokens\""),
            "unknown_metric" => Row(metric: "unknown_metric"),
            "invalid_amount" => Row(metric: "ai_tokens", amount: "\"-0.01\""),
            "duplicate_member" => Row(metric: "ai_tokens", extra: ",\"currency\":\"USD\""),
            "invalid_json" => Row(metric: "ai_tokens", amount: "NaN"),
            "overlapping_range" => Row(),
            _ => Row(from: February),
        };
        var first = reason == "duplicate_version" ? Row(to: February) : Row();
        var result = await LoadAsync(world, Card("invalid", first, row), world.Org);
        result.Succeeded.ShouldBeFalse();
        result.Violations.ShouldContain(v => v.Reason == reason);
        (await CountAsync(world, "invalid")).ShouldBe(0);
        (await LoadAsync(world, Card("invalid", first, Row(metric: "ai_tokens")), world.Org)).Succeeded.ShouldBeTrue();
        (await CountAsync(world, "invalid")).ShouldBe(2);
    }

    [Theory]
    [InlineData("dangling")]
    [InlineData("foreign")]
    public async Task Ledger_InvalidPriceReference_RejectsAndOwnAndGlobalReferencesSucceed(string kind)
    {
        await using var world = await StartAsync();
        var mine = await LoadAsync(world, Card("own", Row()), world.Org);
        var other = await LoadAsync(world, Card("foreign", Row()), world.OtherOrg);
        var global = await LoadAsync(world, Card("global", Row()), null);
        mine.Succeeded.ShouldBeTrue();
        other.Succeeded.ShouldBeTrue();
        global.Succeeded.ShouldBeTrue();
        await WriteLedgerAsync(world, world.Org, mine.PriceIds.Single());
        await WriteLedgerAsync(world, world.Org, global.PriceIds.Single());
        var reference = kind == "dangling" ? Guid.CreateVersion7() : other.PriceIds.Single();
        await using var scope = await TenantScope.BeginAsync(fixture.AppConnectionString(world.Database), world.Org);
        var error = await scope.CaptureErrorAsync(LedgerSql, LedgerParameters(world.Org, reference));
        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("usage_ledger_entry_price_fk");
    }

    [Fact]
    public async Task Ledger_OwnerBackfillCannotPointAtForeignOverride()
    {
        await using var world = await StartAsync();
        var loaded = await LoadAsync(world, Card("own", Row()), world.Org);
        loaded.Succeeded.ShouldBeTrue();
        await WriteLedgerAsync(world, world.Org, loaded.PriceIds.Single(), owner: true);
        await using var owner = await TenantScope.BeginAsync(fixture.OwnerConnectionString(world.Database));
        var error = await owner.CaptureErrorAsync(LedgerSql, LedgerParameters(world.OtherOrg, loaded.PriceIds.Single()));
        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("usage_ledger_entry_price_fk");
    }

    [Fact]
    public async Task Resolve_PriceAndQuotaPreserveHighPrecisionDecimal()
    {
        await using var world = await StartAsync();
        var result = await LoadAsync(world, Card("precise", Row(
            amount: "\"1234567890123456.123456789012\"", quota: "1234567890123456.123456789012")), world.Org);
        result.Succeeded.ShouldBeTrue();
        var resolved = (await ResolveAsync(world, world.Org, January)).ShouldNotBeNull();
        resolved.UnitPrice.ShouldBe(1234567890123456.123456789012m);
        resolved.IncludedQuota.ShouldBe(1234567890123456.123456789012m);
        (await fixture.ScalarAsync<string>(world.Database,
            "select unit_price::text from public.usage_price where rate_card_version = 'precise'"))
            .ShouldBe("1234567890123456.123456789012");
    }

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database,
        Guid Org, Guid OtherOrg, DedicatedDatabase Dedicated) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
            await Dedicated.DisposeAsync();
        }
    }

    private Task<World> StartAsync() => fixture.StartDedicatedAsync(async dedicated =>
    {
        var api = TenancyApiFactory.RealResolver(fixture.AppConnectionString(dedicated.Name));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("price"));
        var other = await CreateOrgAsync(client, user, Unique.Slug("price-other"));
        return new World(api, client, dedicated.Name, org, other, dedicated);
    });

    private static string Row(string metric = "results", string amount = "\"0.0100\"",
        DateTimeOffset? from = null, DateTimeOffset? to = null, string quota = "null", string extra = "") =>
        "{\"metric\":\"" + metric + "\",\"unit_price\":" + amount + ",\"currency\":\"USD\",\"effective_from\":\""
        + Instant(from ?? January) + "\",\"effective_to\":" + (to is null ? "null" : "\"" + Instant(to.Value) + "\"")
        + ",\"included_quota\":" + quota + extra + "}";

    private static string Instant(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static string Card(string version, params string[] rows) =>
        "{\"rate_card_version\":\"" + version + "\",\"prices\":[" + string.Join(',', rows) + "]}";

    private static UsagePriceCardParseResult Parse(string json) => UsagePriceCardParser.Parse(Encoding.UTF8.GetBytes(json));

    private static void Refuses(string json, string reason, string path)
    {
        var result = Parse(json);
        result.Card.ShouldBeNull();
        result.Violations.ShouldContain(v => v.Reason == reason && v.Path == path);
    }

    private static async Task<UsagePriceLoadResult> LoadAsync(World world, string json, Guid? org)
    {
        var token = TestContext.Current.CancellationToken;
        await using var services = world.Api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var loader = provider.GetRequiredService<UsagePriceLoader>();
        if (org is null)
        {
            return await loader.LoadAsync(UsagePriceScope.Global, Encoding.UTF8.GetBytes(json), token);
        }
        await using var scope = await provider.GetRequiredService<ITenantScopeFactory>().BeginAsync(org.Value, null, false, token);
        var result = await loader.LoadAsync(UsagePriceScope.Org, Encoding.UTF8.GetBytes(json), token);
        await scope.CommitAsync(token);
        return result;
    }

    private static async Task<ResolvedUsagePrice?> ResolveAsync(World world, Guid org, DateTimeOffset at)
    {
        var token = TestContext.Current.CancellationToken;
        await using var services = world.Api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        await using var scope = await provider.GetRequiredService<ITenantScopeFactory>().BeginAsync(org, null, true, token);
        return await provider.GetRequiredService<IUsagePriceResolver>().ResolveAsync(UsageMetric.Results, at, token);
    }

    private Task<long> CountAsync(World world, string version) => fixture.ScalarAsync<long>(world.Database,
        $"select count(*) from public.usage_price where rate_card_version = '{version}'");

    private const string LedgerSql = """
        insert into public.usage_ledger_entry
            (id, org_id, metric, quantity, unit, period, billing_key, occurred_at, price_ref)
        values (@id, @org_id, 'results', 1, 'results', '2026-01', @billing_key, '2026-01-01T00:00:00Z', @price_ref)
        """;

    private static (string Name, object Value)[] LedgerParameters(Guid org, Guid reference)
    {
        var id = Guid.CreateVersion7();
        return [("id", id), ("org_id", org), ("billing_key", "price-boundary:" + id.ToString("N", CultureInfo.InvariantCulture)),
            ("price_ref", reference)];
    }

    private async Task WriteLedgerAsync(World world, Guid org, Guid reference, bool owner = false)
    {
        var connection = owner ? fixture.OwnerConnectionString(world.Database) : fixture.AppConnectionString(world.Database);
        await using var scope = await TenantScope.BeginAsync(connection, owner ? null : org);
        (await scope.ExecuteAsync(LedgerSql, LedgerParameters(org, reference))).ShouldBe(1);
        await scope.CommitAsync();
    }
}
