namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// The task queue's metric names and units (00 §11 SLIs, 01.5.1 §7), in one place
/// so a producer, the OTel pipeline and a test cannot drift: the dead-letter
/// counter is what the 10.3.1-T5 alert rules consume
/// (<c>task_dead_letter_total</c>, alongside <c>task_dispatch_lag_seconds</c> and
/// the <c>outbox_undispatched</c> gauge the dispatcher publishes).
/// </summary>
/// <remarks>
/// <see cref="MeterName"/> intentionally equals
/// <c>EvidgateTelemetry.MeterName</c> ("Evidgate"): the Persistence assembly does
/// not reference Observability, so the same meter name is stated here by value —
/// and a rename on the Observability side must update this constant too.
/// </remarks>
public static class TaskQueueMetrics
{
    /// <summary>The application meter the OTel pipeline subscribes to (mirrors <c>EvidgateTelemetry.MeterName</c>).</summary>
    public const string MeterName = "Evidgate";

    /// <summary>
    /// Counter of `dead_letter` transitions (01.5.1 §7). Alert-side: it moves for
    /// both reasons a row can end up there — a handler that keeps throwing at the
    /// cap, and a lease that expired at the cap (a consumer that died) — so a
    /// poison task is visible without scraping the table.
    /// </summary>
    public const string DeadLetterCounterName = "task_dead_letter_total";

    /// <summary>Description of <see cref="DeadLetterCounterName"/>.</summary>
    public const string DeadLetterDescription =
        "Task rows that reached state dead_letter (retry cap reached, or a lease that expired with every attempt used).";

    /// <summary>Unit of <see cref="DeadLetterCounterName"/>.</summary>
    public const string DeadLetterUnit = "{row}";

    /// <summary>
    /// Counter of committed deferrals: a handler asked to wait, so the row kept its
    /// attempt and nothing failed. Alert-side: a long run of them means the window the
    /// handler waits for never opens — a dependency or quota problem, not a poison task.
    /// </summary>
    public const string DeferredCounterName = "task_deferred_total";

    /// <summary>Description of <see cref="DeferredCounterName"/>.</summary>
    public const string DeferredDescription =
        "Task attempts deferred to a later window at the handler's request (attempt kept, no dead letter, no alert).";

    /// <summary>Unit of <see cref="DeferredCounterName"/>.</summary>
    public const string DeferredUnit = "{attempt}";

    /// <summary>
    /// Gauge of how long the oldest due-but-unclaimed row has waited, in seconds. Zero means nothing is
    /// due; a value above the alert threshold is the queue-age signal the on-call runbook reads.
    /// </summary>
    public const string OldestAgeGaugeName = "task_queue_oldest_age_seconds";

    /// <summary>Description of <see cref="OldestAgeGaugeName"/>.</summary>
    public const string OldestAgeDescription =
        "Seconds between now and the available_at of the oldest queued or retry task row whose available_at has passed.";

    /// <summary>Unit of <see cref="OldestAgeGaugeName"/>.</summary>
    public const string OldestAgeUnit = "s";

    /// <summary>Gauge of rows currently parked in <c>dead_letter</c>, the depth an operator drains.</summary>
    public const string DeadLetterDepthGaugeName = "task_dead_letter_depth";

    /// <summary>Description of <see cref="DeadLetterDepthGaugeName"/>.</summary>
    public const string DeadLetterDepthDescription =
        "Task rows currently in state dead_letter (terminal, awaiting replay or deletion).";

    /// <summary>Unit of <see cref="DeadLetterDepthGaugeName"/>.</summary>
    public const string DeadLetterDepthUnit = "{row}";

    /// <summary>
    /// Counter of leases that expired before their handler finished: the row came back as <c>queued</c>,
    /// or went to <c>dead_letter</c> when its attempts were used up. A rising value means consumers are
    /// dying or hanging, not that the queue is slow.
    /// </summary>
    public const string LeaseLostCounterName = "task_lease_lost_total";

    /// <summary>Description of <see cref="LeaseLostCounterName"/>.</summary>
    public const string LeaseLostDescription =
        "Task rows whose lease expired before completion (requeued, or dead-lettered at the attempt cap).";

    /// <summary>Unit of <see cref="LeaseLostCounterName"/>.</summary>
    public const string LeaseLostUnit = "{row}";
}
