using Evidgate.BuildingBlocks.Persistence.Tasks;

namespace Evidgate.Modules.Policy.Webhooks.Application;

/// <summary>
/// When a failed delivery tries again: exponential from one minute, capped at one hour, with the platform's task jitter,
/// and nothing after the 24-hour window that starts at the delivery's creation. The last attempt falls exactly on the
/// deadline, so a receiver that recovers late in the window still gets one chance.
/// </summary>
internal static class WebhookRetryPolicy
{
    /// <summary>How long after creation a delivery keeps retrying.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>The wait after the first failed attempt.</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromMinutes(1);

    /// <summary>The longest wait between two attempts.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromHours(1);

    /// <summary>The next attempt time, or null when the window is spent and the delivery is dead-lettered.</summary>
    /// <param name="createdAt">When the delivery row was created.</param>
    /// <param name="now">The instant the attempt failed.</param>
    /// <param name="attempt">The attempt that just failed, counted from 1.</param>
    /// <param name="jitterSample">A sample in [0, 1) that scales the delay by ±20 %.</param>
    public static DateTimeOffset? Next(DateTimeOffset createdAt, DateTimeOffset now, int attempt, double jitterSample)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        var deadline = createdAt + Window;
        if (now >= deadline)
        {
            return null;
        }

        // The shared backoff may overshoot its own cap by the jitter, which this schedule does not allow.
        var delay = TaskBackoff.Compute(attempt - 1, BaseDelay, MaxDelay, jitterSample);
        if (delay > MaxDelay)
        {
            delay = MaxDelay;
        }

        var next = now + delay;

        return next > deadline ? deadline : next;
    }

    /// <summary>The next attempt time with a fresh random jitter sample.</summary>
    /// <param name="createdAt">When the delivery row was created.</param>
    /// <param name="now">The instant the attempt failed.</param>
    /// <param name="attempt">The attempt that just failed, counted from 1.</param>
    public static DateTimeOffset? Next(DateTimeOffset createdAt, DateTimeOffset now, int attempt) =>
        Next(createdAt, now, attempt, Random.Shared.NextDouble());
}
