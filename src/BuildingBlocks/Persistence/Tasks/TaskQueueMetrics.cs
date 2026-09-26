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
}
