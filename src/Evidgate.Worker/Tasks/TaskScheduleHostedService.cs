using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.Options;

namespace Evidgate.Worker.Tasks;

/// <summary>
/// Fires the registered cron schedules (01.5.1-T1.4): every pass asks the runner for
/// the occurrences due in the lookback window. There is no leader — every Worker
/// runs this service, and 00 §5.3's unique <c>(schedule_name, fire_at)</c> key is
/// what makes the duplicate attempt harmless.
/// </summary>
internal sealed partial class TaskScheduleHostedService : BackgroundService
{
    private readonly TaskScheduleRunner _runner;
    private readonly IScheduleRegistry _schedules;
    private readonly TasksOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TaskScheduleHostedService> _logger;

    public TaskScheduleHostedService(
        TaskScheduleRunner runner,
        IScheduleRegistry schedules,
        IOptions<TasksOptions> options,
        TimeProvider timeProvider,
        ILogger<TaskScheduleHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _schedules = schedules;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_schedules.Schedules.Count == 0)
        {
            LogNoSchedules();
            return;
        }

        var reportedFailure = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _runner.RunOnceAsync(_timeProvider.GetUtcNow(), stoppingToken);
                reportedFailure = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Same treatment as a consumer's claim pass: a database that is not
                // reachable yet must not stop the worker, and must not fill the log.
                if (reportedFailure)
                {
                    LogRunFailedAgain(exception);
                }
                else
                {
                    LogRunFailed(exception);
                    reportedFailure = true;
                }
            }

            try
            {
                await Task.Delay(_options.SchedulePollMilliseconds, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(
        EventId = 2340,
        Level = LogLevel.Warning,
        Message = "Schedule pass failed; retrying after the poll interval")]
    private partial void LogRunFailed(Exception exception);

    [LoggerMessage(
        EventId = 2341,
        Level = LogLevel.Debug,
        Message = "Schedule pass still failing")]
    private partial void LogRunFailedAgain(Exception exception);

    [LoggerMessage(
        EventId = 2342,
        Level = LogLevel.Debug,
        Message = "No schedule is registered: the schedule runner stays idle")]
    private partial void LogNoSchedules();
}
