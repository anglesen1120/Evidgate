using System.Diagnostics.Metrics;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Evidgate.BuildingBlocks.Persistence.Outbox;

public sealed partial class OutboxDispatcher
{
    /// <summary>How often <see cref="RunBacklogRefreshAsync"/> re-counts the undispatched rows.</summary>
    public static readonly TimeSpan BacklogRefreshInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Age past which a cached count is no longer reported: two refresh intervals,
    /// so one slow or failed refresh still shows the last value while a refresher
    /// that has stopped never keeps a fresh-looking stale number on the gauge.
    /// </summary>
    public static readonly TimeSpan BacklogMaxAge = BacklogRefreshInterval * 2;

    /// <summary>The last successful count and the instant it was taken (one object, so a reader never sees a torn pair).</summary>
    private sealed record Backlog(long Count, long Timestamp);

    private volatile Backlog? _backlog;

    /// <summary>
    /// Keeps the <c>outbox_undispatched</c> cache fresh until cancelled: counts once
    /// immediately, then every <see cref="BacklogRefreshInterval"/>. Independent of
    /// the dispatch loop on purpose — a cache refreshed only after a dispatch would
    /// show a stale backlog exactly while nothing is dispatching. A failed count
    /// keeps the previous value (which ages out) and logs; it never throws.
    /// </summary>
    public async Task RunBacklogRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(BacklogRefreshInterval, _time);
            do
            {
                await RefreshBacklogAsync(cancellationToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Graceful stop.
        }
    }

    private async Task RefreshBacklogAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            // Bounded twice: the SQL caps the index walk at UndispatchedGaugeCap
            // rows, and a 5 s command timeout (the ReadinessProbe precedent)
            // keeps a stalled count from holding the refresher on Npgsql's 30 s default.
            var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                OutboxDispatchSql.CountUndispatched,
                new { gauge_cap = OutboxDispatchSql.UndispatchedGaugeCap },
                commandTimeout: 5,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            _backlog = new Backlog(count, _time.GetTimestamp());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogUndispatchedCountFailed(exception);
        }
    }

    /// <summary>
    /// The <c>outbox_undispatched</c> gauge callback: reads the cached count and
    /// does no I/O, so a metrics scrape never parks a thread on the database. No
    /// measurement while nothing has been counted yet or the cache is older than
    /// <see cref="BacklogMaxAge"/>.
    /// </summary>
    private IEnumerable<Measurement<long>> ObserveUndispatched()
    {
        var backlog = _backlog;
        if (_disposed || backlog is null || _time.GetElapsedTime(backlog.Timestamp) > BacklogMaxAge)
        {
            return [];
        }

        return [new Measurement<long>(backlog.Count)];
    }

    [LoggerMessage(
        EventId = 2360,
        Level = LogLevel.Warning,
        Message = "outbox_undispatched count refresh failed; the gauge keeps the last value until it ages out")]
    private partial void LogUndispatchedCountFailed(Exception exception);
}
