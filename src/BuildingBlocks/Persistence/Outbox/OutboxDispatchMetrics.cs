namespace Evidgate.BuildingBlocks.Persistence.Outbox;

/// <summary>
/// The dispatcher's metric names and units (00 §11 SLIs, 01.5.1 §7), in one place
/// so a producer, the OTel pipeline and a test cannot drift. These are the
/// names the 10.3.1-T5 alert rules consume.
/// </summary>
/// <remarks>
/// <see cref="MeterName"/> intentionally equals
/// <c>EvidgateTelemetry.MeterName</c> ("Evidgate"): the Persistence assembly does
/// not reference Observability, so the same meter name is stated here by value
/// — and a rename on the Observability side must update this constant too.
/// </remarks>
public static class OutboxDispatchMetrics
{
    /// <summary>The application meter the OTel pipeline subscribes to (mirrors <c>EvidgateTelemetry.MeterName</c>).</summary>
    public const string MeterName = "Evidgate";

    /// <summary>
    /// Observable gauge of outbox rows the dispatcher has not moved yet
    /// (committed work the queue cannot see). Alert-side: a rising value is the
    /// first signal the dispatcher is down or losing to a backlog.
    /// </summary>
    public const string UndispatchedGaugeName = "outbox_undispatched";

    /// <summary>
    /// Histogram of the time between an outbox row's <c>created_at</c> and its
    /// dispatch, in seconds — the 00 §11 SLI for "the mechanism added no
    /// meaningful delay to a committed piece of work".
    /// </summary>
    public const string DispatchLagHistogramName = "task_dispatch_lag_seconds";

    /// <summary>Description of <see cref="UndispatchedGaugeName"/>.</summary>
    public const string UndispatchedDescription =
        "Outbox rows committed but not yet moved into the task queue (dispatched_at is null).";

    /// <summary>Description of <see cref="DispatchLagHistogramName"/>.</summary>
    public const string DispatchLagDescription =
        "Seconds between an outbox row's business commit (created_at) and its dispatch into the task queue.";
}
