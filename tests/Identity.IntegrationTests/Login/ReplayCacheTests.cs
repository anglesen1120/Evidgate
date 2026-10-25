using Evidgate.Modules.Identity.Login;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login;

/// <summary>
/// 02.3.1-T1.1 (F1/M1/CR4): the one-time <c>state</c> record's cap, its expiry and its recovery. The set
/// fails closed at <see cref="LoginReplayCache.MaxEntries"/>, a live record is never dropped by a sweep (that
/// would readmit a replay) and an expired one is purged, so a set that filled up recovers on its own.
/// </summary>
public sealed class ReplayCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(600);

    [Fact]
    public void A_Replay_Inside_The_Lifetime_Is_Refused()
    {
        var cache = new LoginReplayCache(new MutableClock(Now));

        cache.TryConsume("fake:state-1", Lifetime).ShouldBeTrue();
        cache.TryConsume("fake:state-1", Lifetime).ShouldBeFalse();
    }

    [Fact]
    public void An_Expired_Record_Is_Purged_So_The_Same_State_Can_Be_Used_Again()
    {
        var clock = new MutableClock(Now);
        var cache = new LoginReplayCache(clock);

        cache.TryConsume("fake:state-1", Lifetime).ShouldBeTrue();

        // An expired record must not readmit the state, but it must also not stay in the set forever: the
        // cookie it belongs to has expired with it, so the key may be recorded again.
        clock.Advance(Lifetime + TimeSpan.FromSeconds(1));
        cache.TryConsume("fake:state-1", Lifetime).ShouldBeTrue();
    }

    [Fact]
    public void A_Full_Set_Refuses_And_Recovers_Once_Its_Records_Expire()
    {
        var clock = new MutableClock(Now);
        var cache = new LoginReplayCache(clock);

        for (var index = 0; index < LoginReplayCache.MaxEntries; index++)
        {
            cache.TryConsume("fake:state-" + index, Lifetime).ShouldBeTrue();
        }

        // At the cap the set fails closed: a state that cannot be recorded is refused, never trusted.
        cache.TryConsume("fake:state-overflow", Lifetime).ShouldBeFalse();

        // Past the lifetime every record is expired, so the next attempt sweeps them and is recorded.
        clock.Advance(Lifetime + TimeSpan.FromSeconds(1));
        cache.TryConsume("fake:state-overflow", Lifetime).ShouldBeTrue();

        // And the set is a working set again, not one that keeps refusing.
        cache.TryConsume("fake:state-after-recovery", Lifetime).ShouldBeTrue();
        cache.TryConsume("fake:state-after-recovery", Lifetime).ShouldBeFalse();
    }

    [Fact]
    public void A_Sweep_Never_Drops_A_Live_Record()
    {
        var clock = new MutableClock(Now);
        var cache = new LoginReplayCache(clock);

        cache.TryConsume("fake:state-1", Lifetime).ShouldBeTrue();

        // Past the sweep interval, an unrelated success runs the opportunity sweep — which must stop at the
        // live record at the head of the queue instead of evicting it.
        clock.Advance(LoginReplayCache.SweepInterval + TimeSpan.FromSeconds(1));
        cache.TryConsume("fake:state-2", Lifetime).ShouldBeTrue();

        cache.TryConsume("fake:state-1", Lifetime).ShouldBeFalse();
        cache.TryConsume("fake:state-2", Lifetime).ShouldBeFalse();
    }

    [Fact]
    public void The_Cap_Is_Strict_Under_Parallel_TryConsume()
    {
        var cache = new LoginReplayCache(new MutableClock(Now));
        var attempts = LoginReplayCache.MaxEntries + 5_000;
        var accepted = 0;

        // Every record has the same (live) lifetime, so the set can never make room: exactly the cap is
        // accepted, never one more, whatever the interleaving of the reservations.
        Parallel.For(
            0,
            attempts,
            index =>
            {
                if (cache.TryConsume("fake:state-" + index, Lifetime))
                {
                    Interlocked.Increment(ref accepted);
                }
            });

        accepted.ShouldBe(LoginReplayCache.MaxEntries);
        cache.TryConsume("fake:state-after-the-flood", Lifetime).ShouldBeFalse();
    }

    [Fact]
    public void An_Empty_Key_Is_Rejected()
    {
        var cache = new LoginReplayCache(new MutableClock(Now));

        Should.Throw<ArgumentException>(() => cache.TryConsume(string.Empty, Lifetime));
    }
}
