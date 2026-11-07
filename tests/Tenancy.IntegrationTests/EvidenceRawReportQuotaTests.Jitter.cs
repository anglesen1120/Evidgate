// The deferral jitter of the report quota block, asserted as pure arithmetic: the delay is the clamped wait minus a
// pull-back of at most ten percent (capped at half a minute), it never leaves the queue's floor or the clamp's
// ceiling, and a tenant's whole deferred backlog spreads out instead of waking on one instant.
using Evidgate.Modules.Evidence.Application;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportQuotaTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.999)]
    public void The_Deferral_Jitter_Shortens_By_The_Capped_Fraction_And_Nothing_Else(double sample)
    {
        // Ten minutes is long enough for the half-minute cap to bind: the pull-back is min(10 %, 30 s) × the sample.
        var delay = ParseRawReportHandler.QuotaRetryDelay(TimeSpan.FromMinutes(10), MidMarch, sample);

        delay.ShouldBe(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(30 * sample));
        delay.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        delay.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void A_Short_Deferral_Is_Pulled_Back_By_Ten_Percent_Not_By_The_Cap()
    {
        // Two minutes is short enough that the fraction binds: the pull-back is 12 s, of which a half sample is 6 s.
        var delay = ParseRawReportHandler.QuotaRetryDelay(TimeSpan.FromMinutes(2), MidMarch, 0.5);

        delay.ShouldBe(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void The_Jitter_Never_Undercuts_The_One_Second_Floor()
    {
        // The clamp's own floor is one second, and the pull-back of a one-second delay would leave 0.9 s.
        var delay = ParseRawReportHandler.QuotaRetryDelay(TimeSpan.FromSeconds(1), MidMarch, 0.999);

        delay.ShouldBe(TimeSpan.FromSeconds(1), "a sub-second wait would only spin the claim loop");
    }

    [Fact]
    public void The_Jitter_Stays_Inside_A_Period_That_Ends_Sooner()
    {
        // Half a minute before the rollover the ceiling is that half-minute, not the fifteen-minute cap.
        var lastHalfMinute = new DateTimeOffset(2026, 3, 31, 23, 59, 30, TimeSpan.Zero);

        var delay = ParseRawReportHandler.QuotaRetryDelay(TimeSpan.FromMinutes(10), lastHalfMinute, 0.5);

        delay.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
        delay.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void A_Sample_Outside_The_Unit_Interval_Is_A_Caller_Bug(double sample)
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => ParseRawReportHandler.QuotaRetryDelay(TimeSpan.FromMinutes(1), MidMarch, sample));
    }

    [Fact]
    public void Twenty_Deferred_Reports_Of_One_Tenant_Wake_At_Twenty_Distinct_Moments()
    {
        var wait = TimeSpan.FromMinutes(10);

        // Deterministic samples, one per task, the way twenty rows blocked in the same instant draw their own.
        var delays = Enumerable.Range(0, 20)
            .Select(index => ParseRawReportHandler.QuotaRetryDelay(wait, MidMarch, index / 20.0))
            .ToArray();

        delays.Distinct().Count().ShouldBe(
            20,
            "twenty reports blocked together would otherwise become twenty claims on the one unit of room a raise adds");
        delays.ShouldAllBe(delay => delay <= wait, "no task may be deferred past the clamp it was given");
        delays.ShouldAllBe(delay => delay >= TimeSpan.FromSeconds(1), "and none may fall under the queue's floor");

        var spread = delays.Max() - delays.Min();
        spread.ShouldBeGreaterThan(TimeSpan.Zero);
        spread.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(30), "the whole spread is the half-minute cap");
    }
}
