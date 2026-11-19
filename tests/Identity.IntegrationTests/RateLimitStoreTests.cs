using System.Security.Cryptography;
using System.Text;
using Evidgate.Modules.Identity.Ci;
using Evidgate.Modules.Identity.Options;
using Evidgate.Testing;
using Identity.IntegrationTests.Login.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// <see cref="ExchangeRateLimiter"/> against the real server: one atomic statement per admission on one key,
/// so parallel callers never overshoot the limit; the window is the database's own clock; a full surface admits
/// a new key and evicts its oldest window instead of refusing; a surface never touches another surface's rows;
/// and an unreachable database throws instead of admitting.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RateLimitStoreTests : SessionTestBase
{
    public RateLimitStoreTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    [Fact]
    public async Task Acquire_Beyond_The_Limit_Refuses_With_A_Positive_RetryAfter()
    {
        var limiter = Limiter(limit: 2, windowSeconds: 60);
        var key = "192.0.2.10";

        var first = await limiter.AcquireAsync(RateSurface.Login, key, Token);
        var second = await limiter.AcquireAsync(RateSurface.Login, key, Token);
        var third = await limiter.AcquireAsync(RateSurface.Login, key, Token);

        first.ShouldBe(new RateDecision(true, 0));
        second.ShouldBe(new RateDecision(true, 0));
        third.Allowed.ShouldBeFalse();
        third.RetryAfterSeconds.ShouldBeInRange(1, 60);
        (await HitCountAsync(key)).ShouldBe(3, "the stored count is capped at the limit plus one");
    }

    [Fact]
    public async Task Parallel_Acquires_On_One_Key_Admit_Exactly_The_Limit()
    {
        const int limit = 4;
        var limiter = Limiter(limit, windowSeconds: 60, maxKeys: 50000);
        var key = "198.51.100.7";

        var decisions = await Task.WhenAll(
            Enumerable.Range(0, 32)
                .Select(_ => Task.Run(() => limiter.AcquireAsync(RateSurface.CiExchange, key, Token).AsTask(), Token)));

        decisions.Count(decision => decision.Allowed).ShouldBe(limit);
        decisions.Count(decision => !decision.Allowed).ShouldBe(32 - limit);
        decisions.Where(decision => !decision.Allowed).ShouldAllBe(decision => decision.RetryAfterSeconds >= 1);
        (await HitCountAsync(key)).ShouldBe(limit + 1, "the cap holds under concurrency too");
    }

    [Fact]
    public async Task An_Expired_Window_Starts_A_New_Count()
    {
        var limiter = Limiter(limit: 2, windowSeconds: 1);
        var key = "203.0.113.9";

        (await limiter.AcquireAsync(RateSurface.Login, key, Token)).Allowed.ShouldBeTrue();
        (await limiter.AcquireAsync(RateSurface.Login, key, Token)).Allowed.ShouldBeTrue();
        (await limiter.AcquireAsync(RateSurface.Login, key, Token)).Allowed.ShouldBeFalse();

        await ExpireWindowAsync(key);

        (await limiter.AcquireAsync(RateSurface.Login, key, Token)).ShouldBe(new RateDecision(true, 0));
        (await limiter.AcquireAsync(RateSurface.Login, key, Token)).Allowed.ShouldBeTrue();
        (await limiter.AcquireAsync(RateSurface.Login, key, Token)).Allowed.ShouldBeFalse("the reset window holds its own limit");
    }

    [Fact]
    public async Task A_New_Key_At_The_Bound_Is_Admitted_And_Evicts_The_Oldest_Window_Of_Its_Surface()
    {
        var limiter = Limiter(limit: 10, windowSeconds: 3600, maxKeys: 2);

        (await limiter.AcquireAsync(RateSurface.Login, "key-a", Token)).Allowed.ShouldBeTrue();
        (await limiter.AcquireAsync(RateSurface.Login, "key-b", Token)).Allowed.ShouldBeTrue();
        (await limiter.AcquireAsync(RateSurface.Link, "key-link", Token)).Allowed.ShouldBeTrue();
        var linkWindow = await WindowStartAsync("key-link");

        (await limiter.AcquireAsync(RateSurface.Login, "key-c", Token)).Allowed
            .ShouldBeTrue("a full surface never refuses a new key");

        var loginSurface = await SurfaceOfAsync("key-b");
        (await RowCountAsync(loginSurface)).ShouldBe(2L, "the surface stays at its key bound");
        (await RowExistsAsync("key-a")).ShouldBeFalse("the least recent window of that surface is evicted");
        (await RowExistsAsync("key-b")).ShouldBeTrue();
        (await RowExistsAsync("key-c")).ShouldBeTrue();

        (await RowCountAsync(await SurfaceOfAsync("key-link"))).ShouldBe(1L, "another surface loses no row");
        (await WindowStartAsync("key-link")).ShouldBe(linkWindow, "another surface is not rewritten");
    }

    [Fact]
    public async Task Filling_One_Surface_Leaves_Another_Surfaces_Admission_And_Rows_Untouched()
    {
        var limiter = new ExchangeRateLimiter(
            AppDataSource,
            OptionsFor(limit: 10, windowSeconds: 3600, maxKeys: 1, inviteMaxKeys: 10));

        (await limiter.AcquireAsync(RateSurface.Login, "evict-1", Token)).Allowed.ShouldBeTrue();
        (await limiter.AcquireAsync(RateSurface.Invite, "invite-key", Token)).Allowed.ShouldBeTrue();
        var inviteWindow = await WindowStartAsync("invite-key");
        var inviteSurface = await SurfaceOfAsync("invite-key");

        // The second login key evicts the first login window; the invite surface keeps its own bound and rows.
        (await limiter.AcquireAsync(RateSurface.Login, "evict-2", Token)).Allowed.ShouldBeTrue();
        (await limiter.AcquireAsync(RateSurface.Invite, "invite-key-2", Token)).Allowed.ShouldBeTrue();

        (await RowExistsAsync("evict-1")).ShouldBeFalse();
        (await RowExistsAsync("invite-key")).ShouldBeTrue("the invite window is not the login surface's to evict");
        (await RowExistsAsync("invite-key-2")).ShouldBeTrue();
        (await RowCountAsync(inviteSurface)).ShouldBe(2L);
        (await WindowStartAsync("invite-key")).ShouldBe(inviteWindow);
    }

    [Theory]
    [InlineData("", 1, 60, 10, 32)]
    [InlineData("login", 0, 60, 10, 32)]
    [InlineData("login", 1, 0, 10, 32)]
    [InlineData("login", 1, 86401, 10, 32)]
    [InlineData("login", 1, 60, 0, 32)]
    [InlineData("login", 1, 60, 10, 31)]
    public async Task The_Function_Refuses_Invalid_Arguments(
        string surface,
        int limit,
        int windowSeconds,
        int maxKeys,
        int hashBytes)
    {
        await using var command = AppDataSource.CreateCommand(
            "select allowed, retry_after_seconds from evidgate_resolve.rate_limit_acquire("
            + "@surface, @hash, @limit, @window_seconds, @max_keys)");
        command.Parameters.AddWithValue("surface", surface);
        command.Parameters.AddWithValue("hash", new byte[hashBytes]);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("window_seconds", windowSeconds);
        command.Parameters.AddWithValue("max_keys", maxKeys);

        var error = await Should.ThrowAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync(Token));

        error.SqlState.ShouldBe(PostgresErrorCodes.InvalidParameterValue);
    }

    [Fact]
    public async Task An_Unreachable_Store_Throws_Instead_Of_Allowing()
    {
        await using var closed = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=x;Timeout=1;Pooling=false");
        var limiter = new ExchangeRateLimiter(closed, OptionsFor(limit: 10, windowSeconds: 60, maxKeys: 100));

        await Should.ThrowAsync<Exception>(async () =>
        {
            await limiter.AcquireAsync(RateSurface.Login, "192.0.2.1", Token);
        });
    }

    private ExchangeRateLimiter Limiter(int limit, int windowSeconds, int maxKeys = 50000) =>
        new(AppDataSource, OptionsFor(limit, windowSeconds, maxKeys));

    /// <summary>Every surface carries the same bounds, so a case names only the one it drives.</summary>
    private static IOptions<RateLimitOptions> OptionsFor(
        int limit,
        int windowSeconds,
        int maxKeys,
        int? inviteMaxKeys = null)
    {
        var invite = inviteMaxKeys ?? maxKeys;

        return Options.Create(new RateLimitOptions
        {
            LoginLimit = limit,
            LoginWindowSeconds = windowSeconds,
            LoginMaxKeys = maxKeys,
            LinkLimit = limit,
            LinkWindowSeconds = windowSeconds,
            LinkMaxKeys = maxKeys,
            InviteLimit = limit,
            InviteWindowSeconds = windowSeconds,
            InviteMaxKeys = invite,
            CiExchangeLimit = limit,
            CiExchangeWindowSeconds = windowSeconds,
            CiExchangeMaxKeys = maxKeys,
        });
    }

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    private Task<int> HitCountAsync(string key) =>
        OwnerScalarAsync<int>(
            "select hit_count from public.rate_limit_window where key_hash = @hash",
            ("hash", Hash(key)));

    private Task<bool> RowExistsAsync(string key) =>
        OwnerScalarAsync<bool>(
            "select exists (select 1 from public.rate_limit_window where key_hash = @hash)",
            ("hash", Hash(key)));

    private Task<string> SurfaceOfAsync(string key) =>
        OwnerScalarAsync<string>(
            "select surface from public.rate_limit_window where key_hash = @hash",
            ("hash", Hash(key)));

    private Task<long> RowCountAsync(string surface) =>
        OwnerScalarAsync<long>(
            "select count(*) from public.rate_limit_window where surface = @surface",
            ("surface", surface));

    private Task<string> WindowStartAsync(string key) =>
        OwnerScalarAsync<string>(
            "select window_start::text from public.rate_limit_window where key_hash = @hash",
            ("hash", Hash(key)));

    /// <summary>Moves the stored window past its expiry, the state a later admission must reset.</summary>
    private Task ExpireWindowAsync(string key) =>
        OwnerExecuteAsync(
            "update public.rate_limit_window set window_start = clock_timestamp() - interval '5 seconds' "
            + "where key_hash = @hash",
            ("hash", Hash(key)));
}
