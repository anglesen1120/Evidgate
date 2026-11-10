using System.Diagnostics.Metrics;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// The lease-loss instrument of the claim path, kept beside the claimer so the counter is created in
/// the assembly that owns the queue mechanism.
/// </summary>
public sealed partial class TaskClaimer
{
    private static readonly Meter Meter = new(TaskQueueMetrics.MeterName);

    private static readonly Counter<long> LeaseLosses = Meter.CreateCounter<long>(
        TaskQueueMetrics.LeaseLostCounterName,
        unit: TaskQueueMetrics.LeaseLostUnit,
        description: TaskQueueMetrics.LeaseLostDescription);

    /// <summary>
    /// Counts the leases a committed reclaim found expired: requeued rows plus the rows that had used
    /// every attempt and went to <c>dead_letter</c>. Nothing is counted for a rolled-back reclaim.
    /// </summary>
    /// <param name="requeued">Rows the reclaim put back in <c>queued</c>.</param>
    /// <param name="deadLettered">Rows the reclaim moved to <c>dead_letter</c>.</param>
    private static void RecordLeaseLoss(int requeued, int deadLettered)
    {
        var lost = requeued + deadLettered;
        if (lost > 0)
        {
            LeaseLosses.Add(lost);
        }
    }
}
