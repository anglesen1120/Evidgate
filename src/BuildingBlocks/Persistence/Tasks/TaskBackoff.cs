namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// The retry backoff of 01.5.1-T1.3: `available_at = now() + base × 2^attempts`,
/// capped at a ceiling and jittered by ±20 %. Pure and deterministic given a
/// jitter sample, so the scheduling maths is asserted without a clock
/// (`Backoff_IsExponentialWithJitter`) while the Worker passes
/// <see cref="Random.Shared"/>.
/// </summary>
public static class TaskBackoff
{
    /// <summary>Half-width of the jitter band, as a fraction of the delay (01.5.1-T1.3: ±20 %).</summary>
    public const double JitterFraction = 0.2;

    /// <summary>Exponent at which the exponential term is replaced by the ceiling outright.</summary>
    private const int MaxExponent = 32;

    /// <summary>
    /// The delay before attempt number <paramref name="attempts"/> + 1, where
    /// <paramref name="attempts"/> is the count of attempts already made
    /// (<c>task.attempts</c>, incremented by the claim). The jitter matters more
    /// than the shape: without it, every row that failed at the same moment comes
    /// back at the same moment, and a dependency outage turns into a thundering
    /// herd on recovery.
    /// </summary>
    /// <param name="attempts">Attempts already made; the exponent of the backoff.</param>
    /// <param name="baseDelay">Delay of the first retry, doubled per further attempt.</param>
    /// <param name="maxDelay">Ceiling; the jitter is applied after the cap.</param>
    /// <param name="jitterSample">Uniform sample in [0, 1). 0.5 means no jitter.</param>
    public static TimeSpan Compute(int attempts, TimeSpan baseDelay, TimeSpan maxDelay, double jitterSample)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempts);
        ArgumentOutOfRangeException.ThrowIfLessThan(baseDelay, TimeSpan.FromMilliseconds(1));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDelay, baseDelay);
        ArgumentOutOfRangeException.ThrowIfNegative(jitterSample);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(jitterSample, 1);

        var seconds = attempts >= MaxExponent
            ? maxDelay.TotalSeconds
            : baseDelay.TotalSeconds * Math.Pow(2, attempts);

        var capped = Math.Min(seconds, maxDelay.TotalSeconds);
        var factor = 1 + (JitterFraction * ((2 * jitterSample) - 1));

        return TimeSpan.FromSeconds(capped * factor);
    }

    /// <summary>The production form: the same delay with a real jitter sample.</summary>
    public static TimeSpan Compute(int attempts, TimeSpan baseDelay, TimeSpan maxDelay) =>
        Compute(attempts, baseDelay, maxDelay, Random.Shared.NextDouble());
}
