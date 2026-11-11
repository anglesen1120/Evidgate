using System.Security.Cryptography;
using System.Text;
using Evidgate.Modules.Identity.Login;
using Evidgate.Testing;
using Identity.IntegrationTests.Login.Persistence;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login;

/// <summary>
/// The one-time <c>state</c> record against the real server: first use wins, a replay inside the lifetime is
/// refused (also from a second cache instance, which is what a second replica is), an expired record can be
/// used again, and expired rows are deleted by later calls. Both timestamps of a record are the database's,
/// so a case arranges an expiry by moving the stored <c>expires_at</c> through the owner connection; nothing
/// in the process decides when a record dies.
/// </summary>
[Trait("Category", "Docker")]
public sealed class ReplayCacheTests : SessionTestBase
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(600);

    /// <summary>The retention the function adds beyond the cookie's lifetime, so a still-valid cookie never loses its record.</summary>
    private const double RetentionMarginSeconds = 120;

    public ReplayCacheTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    [Fact]
    public async Task A_Replay_Inside_The_Lifetime_Is_Refused_Even_From_Another_Instance()
    {
        var first = new LoginReplayCache(AppDataSource);
        var second = new LoginReplayCache(AppDataSource);

        (await first.TryConsumeAsync("fake:state-1", Lifetime, Token)).ShouldBeTrue();
        (await first.TryConsumeAsync("fake:state-1", Lifetime, Token)).ShouldBeFalse();
        (await second.TryConsumeAsync("fake:state-1", Lifetime, Token)).ShouldBeFalse();
        (await second.TryConsumeAsync("fake:state-2", Lifetime, Token)).ShouldBeTrue();
    }

    [Fact]
    public async Task An_Expired_Record_Can_Be_Used_Again()
    {
        var cache = new LoginReplayCache(AppDataSource);

        (await cache.TryConsumeAsync("fake:state-1", Lifetime, Token)).ShouldBeTrue();
        await ExpireAsync("fake:state-1");

        (await cache.TryConsumeAsync("fake:state-1", Lifetime, Token)).ShouldBeTrue();
        (await cache.TryConsumeAsync("fake:state-1", Lifetime, Token)).ShouldBeFalse();
    }

    /// <summary>A record is live until the database's own instant reaches <c>expires_at</c>: a row still in the future is refused.</summary>
    [Fact]
    public async Task A_Live_Record_Is_Still_Refused_Before_Its_Expiry()
    {
        var cache = new LoginReplayCache(AppDataSource);

        (await cache.TryConsumeAsync("fake:live", Lifetime, Token)).ShouldBeTrue();

        await OwnerExecuteAsync(
            "update public.login_state_replay set expires_at = clock_timestamp() + interval '30 seconds' where state_hash = @hash",
            ("hash", Hash("fake:live")));

        (await cache.TryConsumeAsync("fake:live", Lifetime, Token)).ShouldBeFalse();
    }

    /// <summary>The stored expiry is <c>now + lifetime + 120 s</c>, so a cookie accepted at the end of its life still finds its record.</summary>
    [Fact]
    public async Task A_Record_Outlives_The_Cookie_By_The_Retention_Margin()
    {
        var cache = new LoginReplayCache(AppDataSource);

        (await cache.TryConsumeAsync("fake:margin", Lifetime, Token)).ShouldBeTrue();

        var remaining = await OwnerScalarAsync<double>(
            "select (extract(epoch from (expires_at - clock_timestamp())))::double precision "
            + "from public.login_state_replay where state_hash = @hash",
            ("hash", Hash("fake:margin")));

        remaining.ShouldBe(Lifetime.TotalSeconds + RetentionMarginSeconds, tolerance: 30);
    }

    [Fact]
    public async Task Expired_Rows_Are_Deleted_By_Later_Calls_And_Live_Rows_Stay()
    {
        var cache = new LoginReplayCache(AppDataSource);

        for (var index = 0; index < 5; index++)
        {
            (await cache.TryConsumeAsync("fake:old-" + index, Lifetime, Token)).ShouldBeTrue();
        }

        await ExpireAsync([.. Enumerable.Range(0, 5).Select(index => "fake:old-" + index)]);
        (await cache.TryConsumeAsync("fake:fresh", Lifetime, Token)).ShouldBeTrue();

        (await OwnerRowAsync("select count(*) from public.login_state_replay"))[0].ShouldBe(1L);
        (await cache.TryConsumeAsync("fake:fresh", Lifetime, Token)).ShouldBeFalse();
    }

    /// <summary>One call sweeps at most 100 expired rows of other hashes: that bound is the whole cleanup path.</summary>
    [Fact]
    public async Task A_Sweep_Removes_At_Most_One_Hundred_Expired_Rows_Per_Call()
    {
        var cache = new LoginReplayCache(AppDataSource);

        await OwnerExecuteAsync(
            "insert into public.login_state_replay (state_hash, expires_at) "
            + "select sha256(convert_to('fake:sweep-' || g, 'UTF8')), now() - interval '1 second' "
            + "from generate_series(1, 101) g");

        (await cache.TryConsumeAsync("fake:sweep-fresh", Lifetime, Token)).ShouldBeTrue();

        var expiredLeft = await OwnerScalarAsync<long>(
            "select count(*) from public.login_state_replay where expires_at <= now()");
        expiredLeft.ShouldBe(1L, "a call deletes up to 100 expired rows and no more");

        (await cache.TryConsumeAsync("fake:sweep-fresh", Lifetime, Token)).ShouldBeFalse();
    }

    [Fact]
    public async Task Parallel_Consumers_Of_One_State_Admit_Exactly_One()
    {
        var caches = Enumerable.Range(0, 8).Select(_ => new LoginReplayCache(AppDataSource)).ToArray();

        var results = await Task.WhenAll(caches.Select(async cache =>
            await cache.TryConsumeAsync("fake:race", Lifetime, Token)));

        results.Count(accepted => accepted).ShouldBe(1);
    }

    /// <summary>
    /// Two consumers that each replace an expired record of their own hash, in parallel and repeatedly: the
    /// bounded sweep runs after the write and skips locked rows, so neither call can wait on the other's hash.
    /// A deadlock would surface here as a <c>40P01</c> <see cref="PostgresException"/> instead of a result.
    /// </summary>
    [Fact]
    public async Task Concurrent_Consumers_Of_Two_Expired_Records_Each_Get_The_First_Use()
    {
        var first = new LoginReplayCache(AppDataSource);
        var second = new LoginReplayCache(AppDataSource);

        for (var round = 0; round < 20; round++)
        {
            var left = "fake:cycle-left-" + round;
            var right = "fake:cycle-right-" + round;
            await InsertExpiredAsync(left);
            await InsertExpiredAsync(right);

            var results = await Task.WhenAll(
                Task.Run(() => first.TryConsumeAsync(left, Lifetime, Token).AsTask(), Token),
                Task.Run(() => second.TryConsumeAsync(right, Lifetime, Token).AsTask(), Token));

            results.ShouldAllBe(consumed => consumed, $"round {round}: each expired hash is a first use again");
        }
    }

    [Fact]
    public async Task An_Empty_Key_Is_Refused()
    {
        var cache = new LoginReplayCache(AppDataSource);

        await Should.ThrowAsync<ArgumentException>(async () => await cache.TryConsumeAsync(string.Empty, Lifetime, Token));
    }

    [Fact]
    public async Task An_Unreachable_Database_Is_Never_Treated_As_First_Use()
    {
        await using var closed = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=x;Password=x;Timeout=1;Pooling=false");
        var cache = new LoginReplayCache(closed);

        await Should.ThrowAsync<Exception>(async () => await cache.TryConsumeAsync("fake:state-1", Lifetime, Token));
    }

    /// <summary>What the reader must refuse: the SHA-256 of the key the cache writes (never the key itself).</summary>
    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>Moves a stored record one second past expiry, the state later calls must clean up.</summary>
    private Task ExpireAsync(params string[] keys) =>
        OwnerExecuteAsync(
            "update public.login_state_replay set expires_at = now() - interval '1 second' "
            + "where state_hash = any(@hashes)",
            ("hashes", keys.Select(Hash).ToArray()));

    /// <summary>A row that an earlier consume already left behind, now expired.</summary>
    private Task InsertExpiredAsync(string key) =>
        OwnerExecuteAsync(
            "insert into public.login_state_replay (state_hash, expires_at) values (@hash, now() - interval '1 second') "
            + "on conflict (state_hash) do update set expires_at = excluded.expires_at",
            ("hash", Hash(key)));
}
