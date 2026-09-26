using Evidgate.BuildingBlocks.Persistence.Tasks;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// 01.5.1-T1.3: the retry backoff. Pure maths with an injected jitter sample, so the
/// shape (doubling, the ceiling, the ±20 % band) is asserted exactly instead of
/// statistically.
/// </summary>
public sealed class TaskBackoffTests
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(60);

    /// <summary>`base × 2^attempts`, so attempt 0 waits the base and every further attempt doubles.</summary>
    [Fact]
    public void Backoff_IsExponentialWithJitter()
    {
        // 0.5 is the middle of the sample band: no jitter either way.
        TaskBackoff.Compute(0, Base, Max, 0.5).ShouldBe(TimeSpan.FromSeconds(5));
        TaskBackoff.Compute(1, Base, Max, 0.5).ShouldBe(TimeSpan.FromSeconds(10));
        TaskBackoff.Compute(2, Base, Max, 0.5).ShouldBe(TimeSpan.FromSeconds(20));
        TaskBackoff.Compute(3, Base, Max, 0.5).ShouldBe(TimeSpan.FromSeconds(40));

        // The band is ±20 % of the same delay.
        TaskBackoff.Compute(2, Base, Max, 0).ShouldBe(TimeSpan.FromSeconds(16));
        TaskBackoff.Compute(2, Base, Max, 0.999999999).TotalSeconds.ShouldBe(24, 0.001);
    }

    /// <summary>
    /// The ceiling applies before the jitter, so a row that keeps failing settles on
    /// the cap instead of growing without bound.
    /// </summary>
    [Fact]
    public void Backoff_IsCapped()
    {
        TaskBackoff.Compute(10, Base, Max, 0.5).ShouldBe(Max);
        TaskBackoff.Compute(32, Base, Max, 0.5).ShouldBe(Max);
        TaskBackoff.Compute(1_000, Base, Max, 0.5).ShouldBe(Max);

        // …and the jitter still applies at the cap, within the band.
        TaskBackoff.Compute(10, Base, Max, 0).ShouldBe(TimeSpan.FromSeconds(48));
        TaskBackoff.Compute(10, Base, Max, 0.999999999).TotalSeconds.ShouldBe(72, 0.001);
    }

    /// <summary>The production overload draws a sample and therefore stays inside the band.</summary>
    [Fact]
    public void Backoff_Uses_TheBand_Without_A_Sample()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var delay = TaskBackoff.Compute(attempt, Base, Max).TotalSeconds;
            var plain = Math.Min(Base.TotalSeconds * Math.Pow(2, attempt), Max.TotalSeconds);

            delay.ShouldBeInRange(plain * 0.8, plain * 1.2);
        }
    }
}
