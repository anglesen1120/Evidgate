using Microsoft.Extensions.Logging;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// The default <see cref="ITaskAlertSink"/> (01.5.1 §7): a warning log line per
/// `dead_letter` transition. It carries no external dependency, so the queue
/// reports a dead letter even with nothing else wired — 10.3.1-T5's real sink
/// plugs into the same seam.
/// </summary>
/// <remarks>
/// The <c>task_dead_letter_total</c> counter deliberately does NOT live here
/// (QA-H defect H-1): <see cref="TaskAlertNotifier"/> increments it on every
/// committed transition so a custom sink cannot silence the metric that
/// 10.3.1-T5's alert rules consume.
/// </remarks>
public sealed partial class LoggingTaskAlertSink : ITaskAlertSink
{
    private readonly ILogger<LoggingTaskAlertSink> _logger;

    /// <summary>Creates the default sink.</summary>
    public LoggingTaskAlertSink(ILogger<LoggingTaskAlertSink> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask NotifyAsync(TaskDeadLetterAlert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        // A log line cannot fail or block, so the ITaskAlertSink contract (never
        // throw into the consumer loop) is met by construction — the guard in
        // TaskAlertNotifier stays for third-party sinks.
        LogDeadLetter(alert.TaskId, alert.Queue, alert.TaskType, alert.Attempts, alert.MaxAttempts, alert.Source);

        return ValueTask.CompletedTask;
    }

    [LoggerMessage(
        EventId = 2320,
        Level = LogLevel.Warning,
        Message = "Task {TaskId} ({TaskType}) of queue {Queue} dead-lettered after {Attempts}/{MaxAttempts} attempts ({Source})")]
    private partial void LogDeadLetter(
        Guid taskId,
        string queue,
        string taskType,
        int attempts,
        int maxAttempts,
        TaskDeadLetterSource source);
}
