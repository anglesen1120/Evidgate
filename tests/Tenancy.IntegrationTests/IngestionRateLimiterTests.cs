// The fixed-window arithmetic of IngestionRateLimiter, driven directly with a controlled clock: the two
// budgets one request is charged to, the bounded and swept map, and the reset at a window boundary. A
// host-level HTTP test cannot prove this, because every host owns its limiter.
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
    public void AcquireBoth_TokenBudgetIsNarrowerThanTheProjectBudget()
    {
        var clock = new IngestRateLimitClock(Start);
        var limiter = Limiter(permitLimit: 1, projectPermitLimit: 3, windowSeconds: 60, maxPartitions: 10, clock);

        // One token is capped on its own budget long before the project's is spent, so its siblings keep a share.
        limiter.AcquireBoth(Project, "token-a").Allowed.ShouldBeTrue();
        limiter.AcquireBoth(Project, "token-a").Allowed.ShouldBeFalse("the token's own budget is spent, the project's is not");
        limiter.AcquireBoth(Project, "token-b").Allowed.ShouldBeTrue();
        limiter.AcquireBoth(Project, "token-c").Allowed.ShouldBeTrue();

        var fourth = limiter.AcquireBoth(Project, "token-d");

        fourth.Allowed.ShouldBeFalse("the project, not the token, refuses the fourth sibling");
        fourth.RetryAfterSeconds.ShouldBe(60);
    }

    [Fact]
    public void AcquireBoth_ProjectBudgetCapsEveryTokenOfThatProject()
    {
        var clock = new IngestRateLimitClock(Start);
        var limiter = Limiter(permitLimit: 2, projectPermitLimit: 2, windowSeconds: 60, maxPartitions: 10, clock);

        limiter.AcquireBoth(Project, "token-a").Allowed.ShouldBeTrue();
        limiter.AcquireBoth(Project, "token-a").Allowed.ShouldBeTrue();

        // The project's budget is what a token minting spree runs into: a brand-new token still cannot write.
        var fresh = limiter.AcquireBoth(Project, "token-brand-new");

        fresh.Allowed.ShouldBeFalse("the project budget is shared");
        fresh.RetryAfterSeconds.ShouldBe(60);

        // Another project is untouched.
        limiter.AcquireBoth(OtherProject, "token-brand-new").Allowed.ShouldBeTrue();
    }

    [Fact]
    public void AcquireBoth_RefusedTokenChargesNothingToTheProject()
    {
        var clock = new IngestRateLimitClock(Start);
        var limiter = Limiter(permitLimit: 1, projectPermitLimit: 2, windowSeconds: 60, maxPartitions: 10, clock);

        limiter.AcquireBoth(Project, "token-a").Allowed.ShouldBeTrue();
        limiter.AcquireBoth(Project, "token-a").Allowed.ShouldBeFalse("the token's own budget is spent");

        // Both budgets are charged in one step: had the refusal spent the project's budget as well,
        // token-b would already be over it.
        limiter.AcquireBoth(Project, "token-b").Allowed.ShouldBeTrue("the refusal charged neither budget");
        limiter.AcquireBoth(Project, "token-b").Allowed.ShouldBeFalse("token-b's own budget is spent");
        limiter.AcquireBoth(Project, "token-c").Allowed.ShouldBeFalse("the project budget is now spent");
    }

    [Fact]
    public void AcquireBoth_SameTokenDifferentProjects_HaveSeparateBudgets()
    {
        var clock = new IngestRateLimitClock(Start);
        var limiter = Limiter(permitLimit: 1, projectPermitLimit: 1, windowSeconds: 60, maxPartitions: 10, clock);

        limiter.AcquireBoth(Project, "token-a").Allowed.ShouldBeTrue();
        limiter.AcquireBoth(Project, "token-a").Allowed.ShouldBeFalse();

        // The partition names the project too, so the same token in another project has its own budget.
        limiter.AcquireBoth(OtherProject, "token-a").Allowed.ShouldBeTrue();
    }

    [Fact]
    public void AcquireBoth_AfterTheWindowEnds_ResetsBothBudgets()
    {
        var clock = new IngestRateLimitClock(Start);
        var limiter = Limiter(permitLimit: 1, projectPermitLimit: 1, windowSeconds: 60, maxPartitions: 10, clock);

        limiter.AcquireBoth(Project, "token-a").Allowed.ShouldBeTrue();
        limiter.AcquireBoth(Project, "token-a").Allowed.ShouldBeFalse();

        clock.Advance(TimeSpan.FromSeconds(61));

        limiter.AcquireBoth(Project, "token-a").Allowed.ShouldBeTrue();
        limiter.AcquireBoth(Project, "token-a").Allowed.ShouldBeFalse();
    }

    [Fact]
    public void AcquireBoth_FullMap_ChargesANewTokenToTheProjectOnly()
    {
        var clock = new IngestRateLimitClock(Start);
        var limiter = Limiter(permitLimit: 1, projectPermitLimit: 3, windowSeconds: 60, maxPartitions: 2, clock);

        // Both partitions of the map belong to this project: its own and token-a's.
        limiter.AcquireBoth(Project, "token-a").Allowed.ShouldBeTrue();

        // There is no room for a token partition, so the project's budget alone admits the request.
        limiter.AcquireBoth(Project, "token-b").Allowed.ShouldBeTrue("the project budget still admits without a token window");
        limiter.AcquireBoth(Project, "token-b").Allowed.ShouldBeTrue();

        var spent = limiter.AcquireBoth(Project, "token-b");

        spent.Allowed.ShouldBeFalse("the project budget is spent");
        spent.RetryAfterSeconds.ShouldBe(60);

        // A full map is not a leak: the ended windows are swept, so the next window admits again.
        clock.Advance(TimeSpan.FromSeconds(61));
        limiter.AcquireBoth(Project, "token-b").Allowed.ShouldBeTrue();
    }

    [Fact]
    public void AcquireBoth_FullMapRefusesANewProject()
    {
        var clock = new IngestRateLimitClock(Start);
        var limiter = Limiter(permitLimit: 5, projectPermitLimit: 5, windowSeconds: 60, maxPartitions: 2, clock);

        limiter.AcquireBoth(Project, "token-a").Allowed.ShouldBeTrue();

        // A project that cannot get a partition has no counter to be charged to, so it is refused rather
        // than admitted unbounded.
        var refused = limiter.AcquireBoth(OtherProject, "token-a");

        refused.Allowed.ShouldBeFalse("a new project with no partition of its own fails closed");
        refused.RetryAfterSeconds.ShouldBe(60);

        // Positive control: the refusal was map pressure, not a permanent deny.
        clock.Advance(TimeSpan.FromSeconds(61));
        limiter.AcquireBoth(OtherProject, "token-a").Allowed.ShouldBeTrue();
    }

    [Fact]
    public void AcquireBoth_ExpiredPartitionIsEvictedThenAdmittedOnce()
    {
        var clock = new IngestRateLimitClock(Start);
        var limiter = Limiter(permitLimit: 1, projectPermitLimit: 4, windowSeconds: 60, maxPartitions: 2, clock);

        limiter.AcquireBoth(Project, "token-a").Allowed.ShouldBeTrue();

        clock.Advance(TimeSpan.FromSeconds(61));

        // token-a's expired partition is swept, which is the only way token-b gets a window of its own.
        limiter.AcquireBoth(Project, "token-b").Allowed.ShouldBeTrue("the expired partition was evicted");
        limiter.AcquireBoth(Project, "token-b").Allowed.ShouldBeFalse("token-b's replacement window is live and spent");

        // An ended window admits the same identity once more, never the evicted one's leftovers.
        clock.Advance(TimeSpan.FromSeconds(61));
        limiter.AcquireBoth(Project, "token-b").Allowed.ShouldBeTrue();
        limiter.AcquireBoth(Project, "token-b").Allowed.ShouldBeFalse();
    }

    [Fact]
    public void AcquireBoth_SweepKeepsALiveResetWindow()
    {
        var clock = new IngestRateLimitClock(Start);
        var limiter = Limiter(permitLimit: 1, projectPermitLimit: 3, windowSeconds: 60, maxPartitions: 2, clock);

        limiter.AcquireBoth(Project, "token-a").Allowed.ShouldBeTrue();

        // The window ends and this caller's next request resets both of its windows: those fresh budgets
        // must survive the eviction pass a new identity triggers, or the same identity would get two.
        clock.Advance(TimeSpan.FromSeconds(61));
        limiter.AcquireBoth(Project, "token-a").Allowed.ShouldBeTrue();

        // Full map: token-b is charged to the project alone and triggers the sweep.
        limiter.AcquireBoth(Project, "token-b").Allowed.ShouldBeTrue();

        limiter.AcquireBoth(Project, "token-a").Allowed.ShouldBeFalse("a live window is spent once, not twice");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(59)]
    public void AcquireBoth_Refusal_CarriesAWholeSecondDelayInsideTheWindow(int secondsElapsed)
    {
        var clock = new IngestRateLimitClock(Start);
        var limiter = Limiter(permitLimit: 1, projectPermitLimit: 1, windowSeconds: 60, maxPartitions: 10, clock);

        limiter.AcquireBoth(Project, "token-a").Allowed.ShouldBeTrue();
        clock.Advance(TimeSpan.FromSeconds(secondsElapsed));

        var refused = limiter.AcquireBoth(Project, "token-a");

        refused.Allowed.ShouldBeFalse();
        refused.RetryAfterSeconds.ShouldBeGreaterThanOrEqualTo(1);
        refused.RetryAfterSeconds.ShouldBeLessThanOrEqualTo(60);
        refused.RetryAfterSeconds.ShouldBe(60 - secondsElapsed);
    }

    private static IngestionRateLimiter Limiter(
        int permitLimit,
        int projectPermitLimit,
        int windowSeconds,
        int maxPartitions,
        TimeProvider clock) =>
        new(
            Options.Create(new IngestionRateLimits
            {
                PermitLimit = permitLimit,
                ProjectPermitLimit = projectPermitLimit,
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
