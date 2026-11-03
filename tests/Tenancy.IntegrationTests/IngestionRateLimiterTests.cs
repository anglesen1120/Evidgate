// The fixed-window arithmetic of IngestionRateLimiter, driven directly with a controlled clock: the
// partition key, the bounded map, the reset at a window boundary and the eviction pass cannot hand one
// identity a second budget. A host-level HTTP test cannot prove this, because every host owns its limiter.
using Evidgate.Modules.Ingestion.Application;
using Evidgate.Modules.Ingestion.Endpoints;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <see cref="IngestionRateLimiter"/> on its own: the unit of the authenticated admission decision behind
/// <c>POST /v1/runs/{r}/events</c>.
/// </summary>
[Collection(ExclusiveTenancySuite.Name)]
public sealed class IngestionRateLimiterTests
{
    private static readonly Guid Project = Guid.Parse("00000000-0000-0000-0000-00000000a001");
    private static readonly Guid OtherProject = Guid.Parse("00000000-0000-0000-0000-00000000a002");

    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Fact]
    public void Acquire_SameProjectDifferentTokens_HaveSeparateBudgets()
    {
        var clock = new IngestRateLimitClock(Start);
        var limiter = Limiter(permitLimit: 2, windowSeconds: 60, maxPartitions: 10, clock);

        // token-a spends its own budget, then the project's shared budget runs out for everyone.
        limiter.Acquire(Project, "token-a").Allowed.ShouldBeTrue();
        limiter.Acquire(Project, "token-a").Allowed.ShouldBeTrue();
        limiter.Acquire(Project, "token-a").Allowed.ShouldBeFalse("the token's own budget is spent");

        clock.Advance(TimeSpan.FromSeconds(61));

        // A second token has a budget of its own and is admitted on it.
        limiter.Acquire(Project, "token-b").Allowed.ShouldBeTrue();
        limiter.Acquire(Project, "token-b").Allowed.ShouldBeTrue();
        limiter.Acquire(Project, "token-b").Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Acquire_ProjectBudgetCapsEveryTokenOfThatProject()
    {
        var clock = new IngestRateLimitClock(Start);
        var limiter = Limiter(permitLimit: 2, windowSeconds: 60, maxPartitions: 10, clock);

        limiter.Acquire(Project, tokenId: null).Allowed.ShouldBeTrue();
        limiter.Acquire(Project, tokenId: null).Allowed.ShouldBeTrue();

        // The project's budget is what a token minting spree runs into: a brand-new token still cannot write.
        limiter.Acquire(Project, "token-brand-new").Allowed.ShouldBeFalse("the project budget is shared");
        limiter.Acquire(Project, tokenId: null).Allowed.ShouldBeFalse();

        // Another project is untouched.
        limiter.Acquire(OtherProject, "token-brand-new").Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Acquire_SameTokenDifferentProjects_HaveSeparateBudgets()
    {
        var clock = new IngestRateLimitClock(Start);
        var limiter = Limiter(permitLimit: 1, windowSeconds: 60, maxPartitions: 10, clock);

        limiter.Acquire(Project, "token-a").Allowed.ShouldBeTrue();
        limiter.Acquire(Project, "token-a").Allowed.ShouldBeFalse();

        // The partition names the project too, so the same token in another project has its own budget.
        limiter.Acquire(OtherProject, "token-a").Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Acquire_AfterTheWindowEnds_ResetsTheBudget()
    {
        var clock = new IngestRateLimitClock(Start);
        var limiter = Limiter(permitLimit: 1, windowSeconds: 60, maxPartitions: 10, clock);

        limiter.Acquire(Project, "token-a").Allowed.ShouldBeTrue();
        limiter.Acquire(Project, "token-a").Allowed.ShouldBeFalse();

        clock.Advance(TimeSpan.FromSeconds(61));

        limiter.Acquire(Project, "token-a").Allowed.ShouldBeTrue();
        limiter.Acquire(Project, "token-a").Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Acquire_FullMap_RefusesANewIdentity()
    {
        var clock = new IngestRateLimitClock(Start);
        var limiter = Limiter(permitLimit: 5, windowSeconds: 60, maxPartitions: 1, clock);

        limiter.Acquire(Project, "token-a").Allowed.ShouldBeTrue();

        var refused = limiter.Acquire(Project, "token-b");

        refused.Allowed.ShouldBeFalse();
        refused.RetryAfterSeconds.ShouldBe(60, "a full map refuses for one whole window");
    }

    [Fact]
    public void Acquire_ResetWindowIsNotEvictedByAFullMap()
    {
        var clock = new IngestRateLimitClock(Start);
        var limiter = Limiter(permitLimit: 1, windowSeconds: 60, maxPartitions: 1, clock);

        limiter.Acquire(Project, "token-a").Allowed.ShouldBeTrue();

        // The window ends and this caller's next request resets it: that fresh budget must survive the
        // eviction pass a new identity triggers, or the same identity would end up with two.
        clock.Advance(TimeSpan.FromSeconds(61));
        limiter.Acquire(Project, "token-a").Allowed.ShouldBeTrue();

        limiter.Acquire(Project, "token-b").Allowed.ShouldBeFalse("token-a's window is live, so nothing is swept");
        limiter.Acquire(Project, "token-a").Allowed.ShouldBeFalse("a live window is spent once, not twice");
    }

    [Fact]
    public void Acquire_ExpiredPartitionIsEvictedThenAdmittedOnce()
    {
        var clock = new IngestRateLimitClock(Start);
        var limiter = Limiter(permitLimit: 1, windowSeconds: 60, maxPartitions: 1, clock);

        limiter.Acquire(Project, "token-a").Allowed.ShouldBeTrue();

        clock.Advance(TimeSpan.FromSeconds(61));

        // The expired partition is swept, so the new identity is admitted exactly once.
        limiter.Acquire(Project, "token-b").Allowed.ShouldBeTrue();
        limiter.Acquire(Project, "token-b").Allowed.ShouldBeFalse();

        // The evicted identity starts again from one fresh budget, not from a surviving second one.
        limiter.Acquire(Project, "token-a").Allowed.ShouldBeTrue();
        limiter.Acquire(Project, "token-a").Allowed.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(59)]
    public void Acquire_Refusal_CarriesAWholeSecondDelayInsideTheWindow(int secondsElapsed)
    {
        var clock = new IngestRateLimitClock(Start);
        var limiter = Limiter(permitLimit: 1, windowSeconds: 60, maxPartitions: 10, clock);

        limiter.Acquire(Project, "token-a").Allowed.ShouldBeTrue();
        clock.Advance(TimeSpan.FromSeconds(secondsElapsed));

        var refused = limiter.Acquire(Project, "token-a");

        refused.Allowed.ShouldBeFalse();
        refused.RetryAfterSeconds.ShouldBeGreaterThanOrEqualTo(1);
        refused.RetryAfterSeconds.ShouldBeLessThanOrEqualTo(60);
        refused.RetryAfterSeconds.ShouldBe(60 - secondsElapsed);
    }

    private static IngestionRateLimiter Limiter(int permitLimit, int windowSeconds, int maxPartitions, TimeProvider clock) =>
        new(
            Options.Create(new IngestionRateLimits
            {
                PermitLimit = permitLimit,
                WindowSeconds = windowSeconds,
                MaxPartitions = maxPartitions,
            }),
            clock);
}

/// <summary>A clock the limiter reads, advanced by the test instead of by elapsed time (X3: no sleeps).</summary>
internal sealed class IngestRateLimitClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    /// <summary>Moves the clock forward, so a fixed window ends without waiting.</summary>
    internal void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
