using System.Diagnostics.Metrics;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

public sealed partial class TaskConsumer
{
    private static readonly Meter DeferralMeter = new(TaskQueueMetrics.MeterName);

    /// <summary>Committed deferrals only, tagged by task type — never by org or payload.</summary>
    private static readonly Counter<long> Deferrals = DeferralMeter.CreateCounter<long>(
        TaskQueueMetrics.DeferredCounterName,
        unit: TaskQueueMetrics.DeferredUnit,
        description: TaskQueueMetrics.DeferredDescription);

    /// <summary>
    /// The deferral transition: the handler asked to wait, so the row moves to
    /// `retry` at the requested time and the attempt it was claimed with is given
    /// back. Bounded and shutdown-independent like the failure transition; a lease
    /// that was already lost leaves the newer attempt's row alone.
    /// </summary>
    private async Task DeferAsync(TaskClaim claim, TaskDeferredException deferred)
    {
        var delay = TaskDeferredException.Clamp(deferred.Delay);

        // A bounded, shutdown-independent budget: a deferral must land inside the
        // host's shutdown window even when the database is unreachable.
        using var budget = new CancellationTokenSource(TransitionWriteBudget);

        try
        {
            var written = await _leases
                .DeferAsync(claim, delay, TaskDeferredException.PersistedError, budget.Token)
                .ConfigureAwait(false);

            if (!written)
            {
                LogLeaseLost(claim.Id, claim.TaskType);
                return;
            }

            Deferrals.Add(1, new KeyValuePair<string, object?>("task_type", claim.TaskType));
            LogDeferred(claim.Id, claim.TaskType, delay.TotalSeconds);
        }
        catch (Exception transitionFailure)
        {
            // The lease is the backstop: an unwritten deferral leaves the row `active`
            // until `locked_until` passes, and the reclaim redelivers it.
            LogTransitionFailed(claim.Id, transitionFailure);
        }
    }
}
