using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Options;
using Evidgate.Testing;
using Identity.IntegrationTests.Login.Persistence;
using Identity.IntegrationTests.Sessions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// The weighted attempt against the real server: a call worth <c>cost</c> units decides exactly as that many
/// sequential unit calls did (the first unit decides, an admitted call clamps at limit+1), a cost-2 attempt costs one
/// round trip, a refusal does not rewrite the refused row, the cost bound is the store's, and the 5-argument call an
/// old build still makes keeps serving the cost-1 decisions.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RateLimitStoreCostTests(DatabaseFixture fixture)
    : SessionTestBase(fixture, DatabaseIsolation.Dedicated)
{
    private const int WindowSeconds = 3600;

    [Theory]
    [InlineData(3, 1, 2)]
    [InlineData(3, 1, 3)]
    [InlineData(3, 1, 4)]
    [InlineData(3, 2, 2)]
    [InlineData(3, 2, 3)]
    [InlineData(3, 2, 4)]
    [InlineData(1, 2, 0)]
    [InlineData(1, 2, 1)]
    [InlineData(1, 2, 2)]
    public async Task Acquire_ACallAtEachStoredCountBoundary_HoldsTheSequentialReference(
        int limit, int cost, int seeded)
    {
        var limiter = Limiter(limit);
        var key = string.Create(CultureInfo.InvariantCulture, $"boundary-{limit}-{cost}-{seeded}");
        if (seeded > 0)
        {
            await SeedWindowAsync(key, seeded);
        }

        var reference = new SequentialReference(limit, seeded);
        var actual = await limiter.AcquireAsync(RateSurface.Login, key, cost, Token);
        var expected = reference.Next(cost);
        var because = $"a cost-{cost} call over a stored count of {seeded} against limit {limit}";

        actual.Allowed.ShouldBe(expected.Allowed, because);
        if (expected.Allowed)
        {
            actual.RetryAfterSeconds.ShouldBe(0, because);
        }
        else
        {
            actual.RetryAfterSeconds.ShouldBeGreaterThanOrEqualTo(1, because);
        }

        (await HitCountAsync(key)).ShouldBe(reference.StoredCount, because);
    }

    [Fact]
    public async Task Acquire_CostOneAndCostTwoCalls_HoldOneSequenceOfTheSequentialReference()
    {
        const int limit = 3;
        var limiter = Limiter(limit);
        var reference = new SequentialReference(limit, 0);

        foreach (var cost in new[] { 1, 2, 1, 2, 1, 2 })
        {
            var before = reference.StoredCount;
            var actual = await limiter.AcquireAsync(RateSurface.Login, "sequence", cost, Token);
            var expected = reference.Next(cost);
            var because = $"a cost-{cost} call over a stored count of {before} against limit {limit}";

            actual.Allowed.ShouldBe(expected.Allowed, because);
            if (expected.Allowed)
            {
                actual.RetryAfterSeconds.ShouldBe(0, because);
            }
            else
            {
                actual.RetryAfterSeconds.ShouldBeGreaterThanOrEqualTo(1, because);
            }
        }

        (await HitCountAsync("sequence")).ShouldBe(reference.StoredCount);
    }

    [Fact]
    public async Task Acquire_AfterTheWindowRollsOver_RepeatsTheReferenceFromAnEmptyWindow()
    {
        const int limit = 2;
        var limiter = Limiter(limit);

        (await limiter.AcquireAsync(RateSurface.Login, "rollover", 2, Token)).ShouldBe(new RateDecision(true, 0));
        (await limiter.AcquireAsync(RateSurface.Login, "rollover", 2, Token)).Allowed
            .ShouldBeFalse("the first unit of the second attempt sees a window at its limit");

        await ExpireWindowAsync("rollover");

        var reference = new SequentialReference(limit, 0);
        var reopened = await limiter.AcquireAsync(RateSurface.Login, "rollover", 2, Token);
        reopened.Allowed.ShouldBe(reference.Next(2).Allowed, "an expired window decides as an absent one");
        reopened.RetryAfterSeconds.ShouldBe(0);
        (await HitCountAsync("rollover")).ShouldBe(reference.StoredCount, "the rollover stores the cost, not the old count");
    }

    [Fact]
    public async Task Acquire_ACostTwoLimiterCall_SendsOneStatementWhereTwoUnitCallsSendTwo()
    {
        var (limiter, log, counting) = CountingLimiter(Fixture.AppConnectionString, limit: 3);
        await using var source = counting;

        (await limiter.AcquireAsync(RateSurface.Login, "one-statement", 2, Token)).Allowed.ShouldBeTrue();
        log.Containing("rate_limit_acquire").Count.ShouldBe(1, "a weighted attempt is one statement");

        log.Clear();
        for (var unit = 0; unit < 2; unit++)
        {
            (await limiter.AcquireAsync(RateSurface.Login, "two-statements", 1, Token)).Allowed.ShouldBeTrue();
        }

        log.Containing("rate_limit_acquire").Count.ShouldBe(2, "the counter sees a second round trip");
    }

    [Fact]
    public async Task Acquire_ACostTwoGateCall_SendsOneStatement()
    {
        var (limiter, log, counting) = CountingLimiter(Fixture.AppConnectionString, limit: 3);
        await using var source = counting;

        var refused = await RateLimitGate.AcquireAsync(
            new DefaultHttpContext(), limiter, RateSurface.Login, "gate-statement", 2, "refused", Token);

        refused.ShouldBeNull("the weighted attempt is admitted");
        log.Containing("rate_limit_acquire").Count.ShouldBe(1, "the gate spends both units in one store call");
    }

    [Fact]
    public async Task Acquire_RefusalsOnALiveWindow_LeaveTheRowVersionUnchangedUntilTheRolloverRewritesIt()
    {
        const int limit = 2;
        var limiter = Limiter(limit);
        (await limiter.AcquireAsync(RateSurface.Login, "unchanged-row", 1, Token)).Allowed.ShouldBeTrue();
        (await limiter.AcquireAsync(RateSurface.Login, "unchanged-row", 1, Token)).Allowed.ShouldBeTrue();
        var before = await WindowIdentityAsync("unchanged-row");

        for (var refusal = 0; refusal < 20; refusal++)
        {
            var refused = await limiter.AcquireAsync(RateSurface.Login, "unchanged-row", 1, Token);
            refused.Allowed.ShouldBeFalse();
            refused.RetryAfterSeconds.ShouldBeGreaterThanOrEqualTo(1);
        }

        (await WindowIdentityAsync("unchanged-row")).ShouldBe(before,
            "a refused live window is read and never written, so no new row version exists");
        (await HitCountAsync("unchanged-row")).ShouldBe(limit, "the stored count rests at the limit");

        await ExpireWindowAsync("unchanged-row");
        var expired = await WindowIdentityAsync("unchanged-row");
        (await limiter.AcquireAsync(RateSurface.Login, "unchanged-row", 2, Token)).ShouldBe(new RateDecision(true, 0));
        var rolled = await WindowIdentityAsync("unchanged-row");

        rolled.ShouldNotBe(expired, "an admitted rollover rewrites the row");
        rolled.WindowStart.ShouldNotBe(expired.WindowStart);
        rolled.HitCount.ShouldBeInRange(1, 2, "the rollover stores the cost, clamped at the limit plus one");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(-1)]
    public async Task TheSixArgumentFunction_RefusesACostOutsideOneToSixteen(int cost)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"invalid-cost-{cost}");
        (await SixArgumentAcquireAsync(key, 1, limit: 3, windowSeconds: WindowSeconds)).Allowed
            .ShouldBeTrue("the same call is served when only the cost is legal");

        var error = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await SixArgumentAcquireAsync(key, cost, limit: 3, windowSeconds: WindowSeconds);
        });

        error.SqlState.ShouldBe(PostgresErrorCodes.InvalidParameterValue);
    }

    [Fact]
    public async Task Acquire_ACostOfZero_SurfacesTheStoreRefusal()
    {
        var limiter = Limiter(limit: 3);

        var error = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await limiter.AcquireAsync(RateSurface.Login, "cost-zero", 0, Token);
        });
        error.SqlState.ShouldBe(PostgresErrorCodes.InvalidParameterValue);

        (await limiter.AcquireAsync(RateSurface.Login, "cost-one", 1, Token)).Allowed
            .ShouldBeTrue("only the cost is wrong: the same store call with cost 1 is admitted");
    }

    [Fact]
    public async Task TheFiveArgumentFunction_KeepsTheCostOneDecisionsForAnOldCaller()
    {
        const int limit = 3;
        var reference = new SequentialReference(limit, 0);

        for (var call = 0; call < limit + 2; call++)
        {
            var before = reference.StoredCount;
            var actual = await FiveArgumentAcquireAsync("wrapper", limit, WindowSeconds);
            var expected = reference.Next(1);
            var because = $"call {call} of the 5-argument wrapper over a stored count of {before}";

            actual.Allowed.ShouldBe(expected.Allowed, because);
            if (expected.Allowed)
            {
                actual.RetryAfterSeconds.ShouldBe(0, because);
            }
            else
            {
                actual.RetryAfterSeconds.ShouldBeGreaterThanOrEqualTo(1, because);
            }
        }

        (await HitCountAsync("wrapper")).ShouldBe(limit, "the wrapper refuses without spending past the limit");
    }

    [Fact]
    public async Task CapacityChecks_AreChosenByTheDatabaseNotByTheKeyDigest()
    {
        const int maxKeys = 2;
        var limiter = Limiter(limit: 10, maxKeys);
        (await limiter.AcquireAsync(RateSurface.Login, "digest-seed", 1, Token)).Allowed.ShouldBeTrue();
        var keys = DigestKeys("digest", 300);

        keys.Length.ShouldBe(300, "the case needs 300 keys the old digest-byte rule never sampled");
        keys.ShouldAllBe(key => Hash(key)[0] >= 16);

        foreach (var key in keys)
        {
            (await limiter.AcquireAsync(RateSurface.Login, key, 1, Token)).Allowed.ShouldBeTrue();
        }

        // Every insert samples independently of the key, so the oldest row survives only if all 300 samples miss:
        // (15/16)^300 = 3.9e-9. Under a digest-chosen sample no insert here would ever run a capacity check.
        (await RowExistsAsync("digest-seed")).ShouldBeFalse("a digest above the old cut-off still runs capacity checks");

        await RateLimitStoreTests.AssertCapacityConvergesAsync(
            limiter, RateSurface.Login, maxKeys, () => CountWindowsAsync("login"), index => $"digest-drain-{index}", Token);
    }

    private static (ExchangeRateLimiter Limiter, StatementLog Log, NpgsqlDataSource Source) CountingLimiter(
        string connectionString, int limit)
    {
        var log = new StatementLog();
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseLoggerFactory(log);
        var source = builder.Build();

        return (new ExchangeRateLimiter(source, OptionsFor(limit)), log, source);
    }

    private ExchangeRateLimiter Limiter(int limit, int maxKeys = 50000) =>
        new(AppDataSource, OptionsFor(limit, maxKeys));

    /// <summary>Every surface carries the same bounds, so a case names only the limit it drives.</summary>
    private static IOptions<RateLimitOptions> OptionsFor(int limit, int maxKeys = 50000) =>
        Options.Create(new RateLimitOptions
        {
            LoginLimit = limit,
            LoginWindowSeconds = WindowSeconds,
            LoginMaxKeys = maxKeys,
            LinkLimit = limit,
            LinkWindowSeconds = WindowSeconds,
            LinkMaxKeys = maxKeys,
            InviteLimit = limit,
            InviteWindowSeconds = WindowSeconds,
            InviteMaxKeys = maxKeys,
            CiExchangeLimit = limit,
            CiExchangeWindowSeconds = WindowSeconds,
            CiExchangeMaxKeys = maxKeys,
        });

    /// <summary>The 0110 semantics as sequential unit calls: the first unit decides, an admitted call clamps at limit+1.</summary>
    private sealed class SequentialReference(int limit, int storedCount)
    {
        public int StoredCount { get; private set; } = storedCount;

        public RateDecision Next(int cost)
        {
            if (StoredCount >= limit)
            {
                return new RateDecision(false, 1);
            }

            StoredCount = Math.Min(StoredCount + cost, limit + 1);

            return new RateDecision(true, 0);
        }
    }

    private sealed record WindowIdentity(string Xmin, string Ctid, string WindowStart, int HitCount);

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>Keys whose digest opens above the cut-off the old sample used, so only <c>random()</c> can sample them.</summary>
    private static string[] DigestKeys(string prefix, int count) =>
        [.. Enumerable.Range(0, int.MaxValue)
            .Select(index => string.Create(CultureInfo.InvariantCulture, $"{prefix}-{index}"))
            .Where(key => Hash(key)[0] >= 16)
            .Take(count)];

    /// <summary>Arranges a live window at an exact stored count, the state the boundary cases start from.</summary>
    private Task SeedWindowAsync(string key, int hitCount) =>
        OwnerExecuteAsync(
            "insert into public.rate_limit_window (surface, key_hash, window_start, hit_count) "
            + "values ('login', @hash, clock_timestamp(), @hit_count)",
            ("hash", Hash(key)),
            ("hit_count", hitCount));

    private async Task<WindowIdentity> WindowIdentityAsync(string key)
    {
        var row = await OwnerRowAsync(
            "select xmin::text, ctid::text, window_start::text, hit_count "
            + "from public.rate_limit_window where key_hash = @hash",
            ("hash", Hash(key)));

        return new WindowIdentity((string)row[0]!, (string)row[1]!, (string)row[2]!, (int)row[3]!);
    }

    private Task<int> HitCountAsync(string key) =>
        OwnerScalarAsync<int>(
            "select hit_count from public.rate_limit_window where key_hash = @hash",
            ("hash", Hash(key)));

    private Task<bool> RowExistsAsync(string key) =>
        OwnerScalarAsync<bool>(
            "select exists (select 1 from public.rate_limit_window where key_hash = @hash)",
            ("hash", Hash(key)));

    private Task<long> CountWindowsAsync(string surface) =>
        OwnerScalarAsync<long>(
            "select count(*) from public.rate_limit_window where surface = @surface",
            ("surface", surface));

    /// <summary>Moves the stored window past its expiry, the state a later admission must reset.</summary>
    private Task ExpireWindowAsync(string key) =>
        OwnerExecuteAsync(
            "update public.rate_limit_window set window_start = clock_timestamp() - interval '2 hours' "
            + "where key_hash = @hash",
            ("hash", Hash(key)));

    private Task<RateDecision> SixArgumentAcquireAsync(string key, int cost, int limit, int windowSeconds) =>
        FunctionAcquireAsync(key, limit, windowSeconds, cost);

    private Task<RateDecision> FiveArgumentAcquireAsync(string key, int limit, int windowSeconds) =>
        FunctionAcquireAsync(key, limit, windowSeconds, cost: null);

    /// <summary>Calls the store as <c>evidgate_app</c>: the 5-argument signature when <paramref name="cost"/> is null.</summary>
    private async Task<RateDecision> FunctionAcquireAsync(string key, int limit, int windowSeconds, int? cost)
    {
        await using var command = AppDataSource.CreateCommand(
            "select allowed, retry_after_seconds from evidgate_resolve.rate_limit_acquire("
            + "@surface, @hash, @limit, @window_seconds, @max_keys" + (cost is null ? ")" : ", @cost)"));
        command.Parameters.AddWithValue("surface", "login");
        command.Parameters.AddWithValue("hash", Hash(key));
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("window_seconds", windowSeconds);
        command.Parameters.AddWithValue("max_keys", 1000);
        if (cost is { } value)
        {
            command.Parameters.AddWithValue("cost", value);
        }

        await using var reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).ShouldBeTrue();

        return new RateDecision(reader.GetBoolean(0), reader.GetInt32(1));
    }
}
