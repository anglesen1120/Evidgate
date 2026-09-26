using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// The mechanism side of the alert hook (01.5.1 §7): the call into
/// <see cref="ITaskAlertSink"/> in the one shape both dead-letter paths use, with
/// the guard the interface contract assumes — a sink implementation that throws
/// or hangs must never break the consumer loop it runs inside.
/// </summary>
internal static partial class TaskAlertNotifier
{
    /// <summary>
    /// The counter lives in the mechanism, not in the sink (QA-H defect H-1):
    /// 01.5.1 §7 promises <c>task_dead_letter_total</c> from day one and a custom
    /// <see cref="ITaskAlertSink"/> registered over the default would otherwise
    /// silence the metric the 10.3.1-T5 alert rules consume.
    /// </summary>
    private static readonly Meter Meter = new(TaskQueueMetrics.MeterName);

    /// <summary>One count per committed dead-letter transition, whatever the sink does.</summary>
    private static readonly Counter<long> DeadLetters = Meter.CreateCounter<long>(
        TaskQueueMetrics.DeadLetterCounterName,
        unit: TaskQueueMetrics.DeadLetterUnit,
        description: TaskQueueMetrics.DeadLetterDescription);
    /// <summary>
    /// Delivers <paramref name="alert"/> to <paramref name="sink"/> after the
    /// transition has been committed, catching anything the sink throws. A null or
    /// failed sink is a logged event, never a failure of the task row — the row
    /// and its `last_error` are the record, the alert is only the notification
    /// (01.5.1 §7).
    /// </summary>
    internal static async ValueTask NotifySafelyAsync(
        ITaskAlertSink? sink,
        ILogger logger,
        TaskDeadLetterAlert alert,
        CancellationToken cancellationToken = default)
    {
        // Count the committed transition before the sink is asked — the alert
        // rule must not depend on which sink is registered.
        DeadLetters.Add(1);

        if (sink is null)
        {
            return;
        }

        try
        {
            await sink.NotifyAsync(alert, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogAlertSinkFailed(logger, alert.TaskId, exception);
        }
    }

    [LoggerMessage(
        EventId = 2321,
        Level = LogLevel.Error,
        Message = "Task {TaskId}: the dead-letter alert sink threw; the row's state and last_error are the record")]
    private static partial void LogAlertSinkFailed(ILogger logger, Guid taskId, Exception exception);
}
