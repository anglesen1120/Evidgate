namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// A handler's "not now": the work waits for a window the handler names (a quota
/// period, an upstream reset) instead of counting as a failure. The consumer turns
/// it into a `retry` transition that gives the attempt back, so the row neither
/// burns a retry nor dead-letters however often it defers.
/// </summary>
public sealed class TaskDeferredException : Exception
{
    /// <summary>
    /// The shortest deferral the queue honours: below it the row would only spin the
    /// claim loop. The handler's own window is the upper bound, and only it knows that.
    /// </summary>
    public static readonly TimeSpan MinimumDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Fixed text, so a handler cannot put tenant data or a secret into `last_error`;
    /// the row records that the work waited, not the details of why.
    /// </summary>
    public const string DeferredMessage = "deferred at the handler's request";

    /// <summary>Creates the deferral for <paramref name="delay"/>, clamped to <see cref="MinimumDelay"/>.</summary>
    public TaskDeferredException(TimeSpan delay)
        : base(DeferredMessage) => Delay = Clamp(delay);

    /// <summary>How long the row waits before it is claimable again.</summary>
    public TimeSpan Delay { get; }

    /// <summary>The delay the queue uses for a requested <paramref name="delay"/>.</summary>
    internal static TimeSpan Clamp(TimeSpan delay) => delay < MinimumDelay ? MinimumDelay : delay;
}
