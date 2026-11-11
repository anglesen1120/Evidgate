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
/// used again, and expired rows are deleted by later calls.
/// </summary>
[Trait("Category", "Docker")]
public sealed class ReplayCacheTests : SessionTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(600);

    public ReplayCacheTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    [Fact]
    public async Task A_Replay_Inside_The_Lifetime_Is_Refused_Even_From_Another_Instance()
    {
        var first = new LoginReplayCache(AppDataSource, new MutableClock(Now));
        var second = new LoginReplayCache(AppDataSource, new MutableClock(Now.AddSeconds(5)));

        (await first.TryConsumeAsync("fake:state-1", Lifetime, Token)).ShouldBeTrue();
        (await first.TryConsumeAsync("fake:state-1", Lifetime, Token)).ShouldBeFalse();
        (await second.TryConsumeAsync("fake:state-1", Lifetime, Token)).ShouldBeFalse();
        (await second.TryConsumeAsync("fake:state-2", Lifetime, Token)).ShouldBeTrue();
    }

    [Fact]
    public async Task An_Expired_Record_Can_Be_Used_Again()
    {
        var clock = new MutableClock(Now);
        var cache = new LoginReplayCache(AppDataSource, clock);

        (await cache.TryConsumeAsync("fake:state-1", Lifetime, Token)).ShouldBeTrue();

        clock.Advance(Lifetime + TimeSpan.FromSeconds(1));
        (await cache.TryConsumeAsync("fake:state-1", Lifetime, Token)).ShouldBeTrue();
        (await cache.TryConsumeAsync("fake:state-1", Lifetime, Token)).ShouldBeFalse();
    }

    [Fact]
    public async Task Expired_Rows_Are_Deleted_By_Later_Calls_And_Live_Rows_Stay()
    {
        var clock = new MutableClock(Now);
        var cache = new LoginReplayCache(AppDataSource, clock);

        for (var index = 0; index < 5; index++)
        {
            (await cache.TryConsumeAsync("fake:old-" + index, Lifetime, Token)).ShouldBeTrue();
        }

        clock.Advance(Lifetime + TimeSpan.FromSeconds(1));
        (await cache.TryConsumeAsync("fake:fresh", Lifetime, Token)).ShouldBeTrue();

        (await OwnerRowAsync("select count(*) from public.login_state_replay"))[0].ShouldBe(1L);
        (await cache.TryConsumeAsync("fake:fresh", Lifetime, Token)).ShouldBeFalse();
    }

    [Fact]
    public async Task Parallel_Consumers_Of_One_State_Admit_Exactly_One()
    {
        var caches = Enumerable.Range(0, 8).Select(_ => new LoginReplayCache(AppDataSource, new MutableClock(Now))).ToArray();

        var results = await Task.WhenAll(caches.Select(async cache =>
            await cache.TryConsumeAsync("fake:race", Lifetime, Token)));

        results.Count(accepted => accepted).ShouldBe(1);
    }

    [Fact]
    public async Task An_Empty_Key_Is_Refused()
    {
        var cache = new LoginReplayCache(AppDataSource, new MutableClock(Now));

        await Should.ThrowAsync<ArgumentException>(async () => await cache.TryConsumeAsync(string.Empty, Lifetime, Token));
    }

    [Fact]
    public async Task An_Unreachable_Database_Is_Never_Treated_As_First_Use()
    {
        await using var closed = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=x;Password=x;Timeout=1;Pooling=false");
        var cache = new LoginReplayCache(closed, new MutableClock(Now));

        await Should.ThrowAsync<Exception>(async () => await cache.TryConsumeAsync("fake:state-1", Lifetime, Token));
    }
}
