using System.Diagnostics;
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
/// without an error and without refusing the caller.
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

        // A local deadline fails the test instead of hanging it if the command timeout ever stops bounding the wait.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var clock = Stopwatch.StartNew();
        var refused = await RateLimitGate.AcquireAsync(
            context, limiter, RateSurface.Login, "stalled-store", 1, "refused", deadline.Token);
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
    /// 300 new keys from 16 parallel callers into a surface already at its bound of 50. The capacity check is
    /// chosen by the database's own <c>random()</c>, so the burst is left to chance rather than steered by key
    /// digests: every caller is admitted, and a bounded drain afterwards converges the surface.
    /// </summary>
    /// <param name="surfaceName">The surface the burst drives; each has its own partition.</param>
    [Theory]
    [InlineData("Login")]
    [InlineData("CiExchange")]
    public async Task Acquire_ParallelNewKeysAtTheBound_AdmitEveryCallerAndConvergeAfterTheBurst(string surfaceName)
    {
        var surface = Enum.Parse<RateSurface>(surfaceName);
        var limiter = Limiter(limit: 10, MaxKeys);
        var surfaceId = surface == RateSurface.Login ? "login" : "ci_exchange";
        for (var index = 0; index < MaxKeys; index++)
        {
            (await limiter.AcquireAsync(surface, $"{surfaceId}-seed-{index}", 1, Token)).Allowed.ShouldBeTrue();
        }

        var keys = Enumerable.Range(0, NewKeys).Select(index => $"{surfaceId}-burst-{index}").ToArray();
        using var gate = new SemaphoreSlim(Callers);

        var decisions = await Task.WhenAll(keys.Select(async key =>
        {
            await gate.WaitAsync(Token);
            try
            {
                return await limiter.AcquireAsync(surface, key, 1, Token);
            }
            finally
            {
                gate.Release();
            }
        }));

        decisions.ShouldAllBe(decision => decision.Allowed, "a full surface never refuses a new key");
        decisions.ShouldAllBe(decision => decision.RetryAfterSeconds == 0);

        await RateLimitStoreTests.AssertCapacityConvergesAsync(
            limiter, surface, MaxKeys, () => RowCountAsync(surfaceId), index => $"{surfaceId}-drain-{index}", Token);
    }

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
