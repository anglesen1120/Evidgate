using System.Diagnostics.Metrics;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// Publishes the two queue-state gauges an on-call responder needs without querying the table by hand:
/// how long the oldest due row has waited and how deep the dead-letter queue is.
/// </summary>
/// <remarks>
/// The gauges read a cache a single refresher keeps fresh on its own timer, so a metrics scrape never
/// runs SQL. The refresh is one aggregate query with a short command timeout; a failure keeps the last
/// value (which ages out), so a database outage shows as absent measurements rather than a stale zero.
/// No tenant, queue or task-type label is attached: both numbers are per deployment.
/// </remarks>
public sealed partial class TaskQueueStateGauge : IDisposable
{
    /// <summary>How often the snapshot is re-read.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Age past which a cached snapshot is no longer reported: two refresh intervals, so a single slow
    /// or failed pass still shows its last value while a stopped refresher never keeps a stale number.
    /// </summary>
    public static readonly TimeSpan MaxAge = RefreshInterval * 2;

    /// <summary>One committed snapshot and the instant it was taken (one object, so a scrape never sees a torn pair).</summary>
    private sealed record Snapshot(double OldestAgeSeconds, long DeadLetters, long Timestamp);

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _time;
    private readonly ILogger<TaskQueueStateGauge> _logger;
    private readonly Meter _meter;
    private readonly ObservableGauge<double> _oldestAge;
    private readonly ObservableGauge<long> _deadLetters;
    private volatile Snapshot? _snapshot;
    private bool _refreshFailing;
    private volatile bool _disposed;

    /// <summary>Creates the gauges with the system clock.</summary>
    public TaskQueueStateGauge(
        NpgsqlDataSource dataSource,
        ILogger<TaskQueueStateGauge> logger)
        : this(dataSource, logger, TimeProvider.System)
    {
    }

    /// <summary>Creates the gauges with an explicit clock (the cached snapshot's age is measured on it).</summary>
    public TaskQueueStateGauge(
        NpgsqlDataSource dataSource,
        ILogger<TaskQueueStateGauge> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dataSource = dataSource;
        _logger = logger;
        _time = timeProvider;

        _meter = new Meter(TaskQueueMetrics.MeterName);
        _oldestAge = _meter.CreateObservableGauge(
            TaskQueueMetrics.OldestAgeGaugeName,
            ObserveOldestAge,
            unit: TaskQueueMetrics.OldestAgeUnit,
            description: TaskQueueMetrics.OldestAgeDescription);
        _deadLetters = _meter.CreateObservableGauge(
            TaskQueueMetrics.DeadLetterDepthGaugeName,
            ObserveDeadLetters,
            unit: TaskQueueMetrics.DeadLetterDepthUnit,
            description: TaskQueueMetrics.DeadLetterDepthDescription);
    }

    /// <summary>
    /// Keeps the snapshot fresh until cancelled: reads once immediately, then every
    /// <see cref="RefreshInterval"/>. A failed read keeps the previous value (which ages out) and logs
    /// once per outage; it never throws into the host's stopping path.
    /// </summary>
    /// <param name="cancellationToken">Stops the refresher.</param>
    public async Task RunRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(RefreshInterval, _time);
            do
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Graceful stop.
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = await connection.QuerySingleAsync<TaskQueueSnapshot>(new CommandDefinition(
                TaskQueueSql.QueueStateSnapshot,
                commandTimeout: 5,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            _snapshot = new Snapshot(snapshot.OldestAgeSeconds, snapshot.DeadLetters, _time.GetTimestamp());
            _refreshFailing = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // One warning on the transition, Debug while the outage persists: the refresher ticks every
            // interval whether or not anything scrapes it.
            if (_refreshFailing)
            {
                LogRefreshFailedAgain(exception);
            }
            else
            {
                LogRefreshFailed(exception);
                _refreshFailing = true;
            }
        }
    }

    /// <summary>The oldest-due age callback: no I/O, and no measurement while the cache is missing or stale.</summary>
    private IEnumerable<Measurement<double>> ObserveOldestAge()
    {
        var snapshot = FreshSnapshot();
        return snapshot is null ? [] : [new Measurement<double>(snapshot.OldestAgeSeconds)];
    }

    /// <summary>The dead-letter depth callback: no I/O, and no measurement while the cache is missing or stale.</summary>
    private IEnumerable<Measurement<long>> ObserveDeadLetters()
    {
        var snapshot = FreshSnapshot();
        return snapshot is null ? [] : [new Measurement<long>(snapshot.DeadLetters)];
    }

    private Snapshot? FreshSnapshot()
    {
        var snapshot = _snapshot;
        return _disposed || snapshot is null || _time.GetElapsedTime(snapshot.Timestamp) > MaxAge
            ? null
            : snapshot;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _meter.Dispose();
    }

    [LoggerMessage(
        EventId = 2362,
        Level = LogLevel.Warning,
        Message = "task queue state refresh failed; both gauges keep the last value until they age out")]
    private partial void LogRefreshFailed(Exception exception);

    [LoggerMessage(
        EventId = 2363,
        Level = LogLevel.Debug,
        Message = "task queue state refresh still failing")]
    private partial void LogRefreshFailedAgain(Exception exception);
}
