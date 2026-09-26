using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.Options;

namespace Evidgate.Worker.Tasks;

/// <summary>
/// Drives the outbox dispatch loop (01.5.1-T3): every poll drains the outbox in
/// batches of <see cref="TasksOptions.DispatchBatchSize"/> until the batch comes
/// back short — a poll that claims nothing leaves the interval, a poll that
/// claims a full batch tries again immediately rather than waiting, so the
/// interval bounds backlog latency, not throughput.
/// </summary>
/// <remarks>
/// Cancellation mid-batch rolls the in-flight transaction back, leaving the
/// rows undispatched and claimable by the next boot's first pass — the
/// graceful-stop contract is the mechanism's own transactional atomicity (00
/// §5.5, plan §7), not extra code here.
/// </remarks>
internal sealed partial class OutboxDispatcherHostedService : BackgroundService
{
    private readonly OutboxDispatcher _dispatcher;
    private readonly TasksOptions _options;
    private readonly ILogger<OutboxDispatcherHostedService> _logger;

    public OutboxDispatcherHostedService(
        OutboxDispatcher dispatcher,
        IOptions<TasksOptions> options,
        ILogger<OutboxDispatcherHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _dispatcher = dispatcher;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reportedFailure = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                OutboxDispatchResult batch;
                do
                {
                    batch = await _dispatcher.DispatchBatchAsync(stoppingToken);
                    if (batch.Claimed > 0)
                    {
                        LogDispatched(batch.Claimed, batch.Enqueued);
                    }
                }
                while (batch.Claimed == _options.DispatchBatchSize && !stoppingToken.IsCancellationRequested);

                reportedFailure = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Same treatment as the schedule runner's pass: a database that is
                // not reachable yet must not stop the worker, and must not fill the
                // log — one warning on the transition, Debug while it persists.
                if (reportedFailure)
                {
                    LogDispatchFailedAgain(exception);
                }
                else
                {
                    LogDispatchFailed(exception);
                    reportedFailure = true;
                }
            }

            try
            {
                await Task.Delay(_options.DispatchPollMilliseconds, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(
        EventId = 2350,
        Level = LogLevel.Debug,
        Message = "Dispatched {Claimed} outbox rows into {Enqueued} task rows")]
    private partial void LogDispatched(int claimed, int enqueued);

    [LoggerMessage(
        EventId = 2351,
        Level = LogLevel.Warning,
        Message = "Outbox dispatch pass failed; retrying after the poll interval")]
    private partial void LogDispatchFailed(Exception exception);

    [LoggerMessage(
        EventId = 2352,
        Level = LogLevel.Debug,
        Message = "Outbox dispatch pass still failing")]
    private partial void LogDispatchFailedAgain(Exception exception);
}
