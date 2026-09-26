using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.Options;

namespace Evidgate.Worker.Tasks;

/// <summary>
/// Drives one claim loop per consumed queue (01.5.1-T1.2). The queue set is the
/// deployment's `EVIDGATE__TASKS__QUEUES__*` list plus every queue a registered
/// schedule fires into, so a schedule can never fire into a queue nobody polls.
/// </summary>
/// <remarks>
/// Per-queue concurrency is a small number of independent claim loops, not one loop
/// with parallel handlers: the concurrency limit of a queue is then visible in the
/// database as "at most N rows of this queue are `active` on this worker", and every
/// loop keeps the claim/handler/completion separation one row at a time.
/// </remarks>
internal sealed partial class TaskQueueHostedService : BackgroundService
{
    private readonly TaskConsumer _consumer;
    private readonly TasksOptions _options;
    private readonly IScheduleRegistry _schedules;
    private readonly ITaskHandlerRegistry _handlers;
    private readonly ILogger<TaskQueueHostedService> _logger;

    public TaskQueueHostedService(
        TaskConsumer consumer,
        IOptions<TasksOptions> options,
        IScheduleRegistry schedules,
        ITaskHandlerRegistry handlers,
        ILogger<TaskQueueHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(logger);

        _consumer = consumer;
        _options = options.Value;
        _schedules = schedules;
        _handlers = handlers;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_handlers.TaskTypes.Count == 0)
        {
            // Nothing to claim: the claim filters on the registered types, so a
            // queue poll would be a round trip that can only return zero rows.
            LogNoHandlers();
            return;
        }

        var plan = QueuePlan();
        if (plan.Count == 0)
        {
            LogNoQueues();
            return;
        }

        foreach (var (queue, concurrency) in plan)
        {
            LogPolling(queue, concurrency);
        }

        // RunAsync completes only when every in-flight handler of every queue has
        // finished, which is the whole graceful-shutdown contract: the host's
        // ShutdownTimeout bounds it, and a handler still running when it expires is
        // redelivered by its lease (01.5.1-T1.2/T1.3).
        await Task.WhenAll(plan.Select(entry => _consumer.RunAsync(entry.Queue, entry.Concurrency, stoppingToken)));
    }

    /// <summary>
    /// The queues this worker polls, once each: the configured ones with their own
    /// concurrency, then the ones only a schedule declares at the default
    /// concurrency.
    /// </summary>
    private List<(string Queue, int Concurrency)> QueuePlan()
    {
        var plan = new List<(string Queue, int Concurrency)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var queue in _options.Queues)
        {
            if (seen.Add(queue.Name))
            {
                plan.Add((queue.Name, queue.Concurrency));
            }
        }

        foreach (var schedule in _schedules.Schedules)
        {
            if (seen.Add(schedule.Queue))
            {
                plan.Add((schedule.Queue, _options.DefaultQueueConcurrency));
            }
        }

        return plan;
    }

    [LoggerMessage(
        EventId = 2330,
        Level = LogLevel.Information,
        Message = "Task consumer polling queue {Queue} with {Concurrency} claim loop(s)")]
    private partial void LogPolling(string queue, int concurrency);

    [LoggerMessage(
        EventId = 2331,
        Level = LogLevel.Warning,
        Message = "No task handler is registered: the worker polls no queue (a module handler registers itself with AddTaskHandler)")]
    private partial void LogNoHandlers();

    [LoggerMessage(
        EventId = 2332,
        Level = LogLevel.Warning,
        Message = "No task queue is configured: set EVIDGATE__TASKS__QUEUES__* or register a schedule")]
    private partial void LogNoQueues();
}
