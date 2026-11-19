using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Options;
using Evidgate.Testing;
using Identity.IntegrationTests.Login.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// The limiter against a stalled store and against a crowd of new keys at the key bound: a held lock turns into the
/// gate's bounded 503 instead of a hung request or an admission, and parallel new keys converge to the bound
/// without an error or an eviction of the caller's own window.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RateLimitStoreFaultTests(DatabaseFixture fixture)
    : SessionTestBase(fixture, DatabaseIsolation.Dedicated)
{
    private const int MaxKeys = 50;
    private const int Callers = 16;
    private const int NewKeys = 300;

    [Fact]
    public async Task Acquire_AStoreStalledByAHeldLock_AnswersUnavailableWithinTheCommandTimeoutAndRecovers()
    {
        var limiter = Limiter(limit: 3, MaxKeys);
        var context = new DefaultHttpContext();

        await using var holder = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await holder.OpenAsync(Token);
        await using var transaction = await holder.BeginTransactionAsync(Token);
        await using (var hold = new NpgsqlCommand(
            "lock table public.rate_limit_window in access exclusive mode", holder, transaction))
        {
            await hold.ExecuteNonQueryAsync(Token);
        }

        var clock = Stopwatch.StartNew();
        var refused = await RateLimitGate.AcquireAsync(
            context, limiter, RateSurface.Login, "stalled-store", 1, "refused", Token);
        clock.Stop();

        refused.ShouldNotBeNull("a count that could not be taken never admits");
        refused.Code.ShouldBe(ApiErrorCode.Unavailable);
        refused.Status.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        refused.Retryable.ShouldBeTrue();
        context.Response.Headers.RetryAfter.ToString().ShouldBe("1");
        clock.Elapsed.ShouldBeGreaterThan(TimeSpan.FromSeconds(1), "the answer waited for the command timeout, not a fast failure");
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15), "the wait is bounded by the 2 s command timeout");

        await transaction.RollbackAsync(Token);

        (await RateLimitGate.AcquireAsync(
            new DefaultHttpContext(), limiter, RateSurface.Login, "stalled-store", 1, "refused", Token))
            .ShouldBeNull("once the lock is gone the same key is admitted again; the stalled call counted nothing");
        (await OwnerScalarAsync<int>(
            "select hit_count from public.rate_limit_window where key_hash = @hash",
            ("hash", Hash("stalled-store")))).ShouldBe(1);
    }

    /// <summary>
    /// 300 new keys from 16 parallel callers into a surface already at its bound of 50, one key in three carrying a
    /// digest the capacity sample accepts. Tolerance, from the migration comment: during the burst the surface may
    /// exceed the bound by the keys admitted between a sampled check's count and the end of the burst, a small
    /// multiple of the parallelism, taken here as three times it; sequential sampled inserts then drain the rest at
    /// up to 100 rows each and leave exactly the bound.
    /// </summary>
    /// <param name="surfaceName">The surface the burst drives; each has its own partition.</param>
    [Theory]
    [InlineData("Login")]
    [InlineData("CiExchange")]
    public async Task Acquire_ParallelNewKeysAtTheBound_ConvergeToTheBoundWithoutEvictingTheCaller(string surfaceName)
    {
        var surface = Enum.Parse<RateSurface>(surfaceName);
        var limiter = Limiter(limit: 10, MaxKeys);
        var surfaceId = surface == RateSurface.Login ? "login" : "ci_exchange";
        for (var index = 0; index < MaxKeys; index++)
        {
            (await limiter.AcquireAsync(surface, $"{surfaceId}-seed-{index}", Token)).Allowed.ShouldBeTrue();
        }

        var keys = BurstKeys(surfaceId, NewKeys);
        keys.Count(key => Hash(key)[0] < 16).ShouldBeGreaterThanOrEqualTo(NewKeys / 3);
        using var gate = new SemaphoreSlim(Callers);

        var outcomes = await Task.WhenAll(keys.Select(async key =>
        {
            await gate.WaitAsync(Token);
            try
            {
                var decision = await limiter.AcquireAsync(surface, key, Token);
                var ownRowSurvives = await OwnerScalarAsync<bool>(
                    "select exists (select 1 from public.rate_limit_window where surface = @surface and key_hash = @hash)",
                    ("surface", surfaceId),
                    ("hash", Hash(key)));

                return (decision.Allowed, ownRowSurvives);
            }
            finally
            {
                gate.Release();
            }
        }));

        outcomes.ShouldAllBe(outcome => outcome.Allowed, "a full surface never refuses a new key");
        outcomes.ShouldAllBe(outcome => outcome.ownRowSurvives, "a call never evicts its own window");

        var afterBurst = await RowCountAsync(surfaceId);
        afterBurst.ShouldBeLessThanOrEqualTo(MaxKeys + (3 * Callers),
            "an unsampled insert defers eviction, so the surface sits above the bound by a bounded margin");

        var drainKeys = Enumerable.Range(0, 100_000)
            .Select(index => $"{surfaceId}-drain-{index}")
            .Where(key => Hash(key)[0] < 16)
            .Take(10)
            .ToArray();
        var drains = 0;
        foreach (var key in drainKeys)
        {
            if (await RowCountAsync(surfaceId) <= MaxKeys)
            {
                break;
            }

            (await limiter.AcquireAsync(surface, key, Token)).Allowed.ShouldBeTrue();
            drains++;
        }

        drains.ShouldBeLessThanOrEqualTo((int)Math.Ceiling((afterBurst - MaxKeys) / 100d) + 1);
        (await RowCountAsync(surfaceId)).ShouldBeLessThanOrEqualTo(MaxKeys, "the surface converges to its bound");
    }

    /// <summary>
    /// Burst keys in which every third one carries a digest the capacity sample accepts, so the checks are spread
    /// through the burst instead of left to chance.
    /// </summary>
    private static string[] BurstKeys(string prefix, int count)
    {
        var sampled = Candidates(prefix, sampledDigest: true).Take(count / 3 + 1).GetEnumerator();
        var unsampled = Candidates(prefix, sampledDigest: false).Take(count).GetEnumerator();
        var keys = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            var source = index % 3 == 0 ? sampled : unsampled;
            source.MoveNext().ShouldBeTrue();
            keys.Add(source.Current);
        }

        return [.. keys];
    }

    private static IEnumerable<string> Candidates(string prefix, bool sampledDigest) =>
        Enumerable.Range(0, int.MaxValue)
            .Select(index => string.Create(CultureInfo.InvariantCulture, $"{prefix}-burst-{index}"))
            .Where(key => (Hash(key)[0] < 16) == sampledDigest);

    private ExchangeRateLimiter Limiter(int limit, int maxKeys) =>
        new(AppDataSource, Options.Create(new RateLimitOptions
        {
            LoginLimit = limit,
            LoginWindowSeconds = 3600,
            LoginMaxKeys = maxKeys,
            CiExchangeLimit = limit,
            CiExchangeWindowSeconds = 3600,
            CiExchangeMaxKeys = maxKeys,
        }));

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    private Task<long> RowCountAsync(string surface) =>
        OwnerScalarAsync<long>(
            "select count(*) from public.rate_limit_window where surface = @surface",
            ("surface", surface));
}
