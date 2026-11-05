using System.Diagnostics;
using System.Diagnostics.Metrics;
using Dapper;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence.Outbox;

/// <summary>
/// The outcome of one dispatch batch (01.5.1 §7): how many outbox rows were
/// claimed-and-stamped and how many of them produced a new <c>task</c> row.
/// </summary>
/// <param name="Claimed">Rows locked by the batch claim and stamped <c>dispatched_at</c>.</param>
/// <param name="Enqueued">Rows that produced a new task row; <c>Claimed - Enqueued</c> are re-dispatches that hit the <c>task_idem_uq</c> dedupe key.</param>
public sealed record OutboxDispatchResult(int Claimed, int Enqueued)
{
    /// <summary>The empty batch.</summary>
    public static readonly OutboxDispatchResult Empty = new(0, 0);
}

/// <summary>
/// The outbox dispatcher (01.5.1-T3.1): moves committed <c>outbox</c> rows into
/// the <c>task</c> queue. One batch is one transaction and exactly three
/// statements — claim (<c>for update skip locked</c>), one
/// <c>insert … select</c> into <c>task</c>, one <c>update</c> stamping
/// <c>dispatched_at</c> — so a crash anywhere inside it is safe by construction:
/// the stamp and the task rows share fate, and a second attempt is deduped by
/// <c>task_idem_uq</c> (<c>'outbox:' || outbox.id</c>).
/// </summary>
/// <remarks>
/// Runs as <c>evidgate_app</c> on the application data source — the role
/// migration 0009 grants exactly `select` and `update (dispatched_at)` for, and
/// no test or handler ever needs more. Mechanism-side only: the per-queue
/// poller that drives it lives in the Worker host
/// (<c>OutboxDispatcherHostedService</c>), and the tenant boundary stays one hop
/// downstream where a handler opens <c>ITenantScope</c> from
/// <c>task.org_id</c> (00 §5.4, 01.5.1 §8 S1).
/// </remarks>
public sealed partial class OutboxDispatcher : IDisposable
{
    /// <summary>Operation name of one dispatch batch's span (10.3.1-T1.1).</summary>
    public const string DispatchOperationName = "outbox.dispatch";

    private readonly NpgsqlDataSource _dataSource;
    private readonly TasksOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;
    private readonly Meter _meter;
    private readonly Histogram<double> _dispatchLag;
    private readonly ObservableGauge<long> _undispatched;
    private readonly string[] _routeTaskTypes;
    private readonly string[] _routeQueues;
    private volatile bool _disposed;

    /// <summary>Creates the dispatcher over the registered app-role data source.</summary>
    public OutboxDispatcher(
        NpgsqlDataSource dataSource,
        IOptions<TasksOptions> options,
        ILogger<OutboxDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _dataSource = dataSource;
        _options = options.Value;
        _logger = logger;

        // The dispatch metric names are those of 00 §11/01.5.1 §7, on the
        // "Evidgate" meter the OTel pipeline already subscribes to
        // (AddMeter(EvidgateTelemetry.MeterName)). The constant is repeated here
        // verbatim — by value, not reference — because the Persistence assembly
        // deliberately does not depend on Observability; a rename on the
        // Observability side must update this constant too.
        _meter = new Meter(OutboxDispatchMetrics.MeterName);
        _dispatchLag = _meter.CreateHistogram<double>(
            OutboxDispatchMetrics.DispatchLagHistogramName,
            unit: "s",
            description: OutboxDispatchMetrics.DispatchLagDescription);
        _undispatched = _meter.CreateObservableGauge(
            OutboxDispatchMetrics.UndispatchedGaugeName,
            ObserveUndispatched,
            unit: "{row}",
            description: OutboxDispatchMetrics.UndispatchedDescription);

        // Routing is a deployment list, and the rule that applies it is stated once
        // (<see cref="TaskQueueRouting"/>): duplicate task_type entries are rejected by
        // options validation, so the parallel arrays here cannot disagree on which queue
        // a type lands in, and a producer that resolves a type for itself — the Api's
        // run-cancel kill writes its row without this dispatcher — cannot either.
        var routes = TaskQueueRouting.Routes(_options);
        _routeTaskTypes = routes.TaskTypes;
        _routeQueues = routes.Queues;
    }

    /// <summary>
    /// Runs one dispatch batch: claims at most <see cref="TasksOptions.DispatchBatchSize"/>
    /// undispatched rows, inserts their task rows and stamps them — all in one
    /// transaction. Returns the batch's outcome; an empty outbox returns
    /// <see cref="OutboxDispatchResult.Empty"/>.
    /// </summary>
    /// <remarks>
    /// Cancellation rolls the batch's transaction back: a cancelled stamp leaves
    /// the rows undispatched and claimable by the next pass (graceful stop).
    /// </remarks>
    public async Task<OutboxDispatchResult> DispatchBatchAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var claimed = (await connection.QueryAsync<Guid>(new CommandDefinition(
            OutboxDispatchSql.ClaimUndispatched,
            new { batch = _options.DispatchBatchSize },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();

        if (claimed.Length == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return OutboxDispatchResult.Empty;
        }

        var taskIds = new Guid[claimed.Length];
        for (var i = 0; i < taskIds.Length; i++)
        {
            taskIds[i] = Guid.CreateVersion7();
        }

        var enqueued = (await connection.QueryAsync<Guid>(new CommandDefinition(
            OutboxDispatchSql.EnqueueTasks,
            new
            {
                outbox_ids = new UuidArrayParameter(claimed),
                task_ids = new UuidArrayParameter(taskIds),
                route_task_types = new TextArrayParameter(_routeTaskTypes),
                route_queues = new TextArrayParameter(_routeQueues),
                default_queue = _options.DispatchDefaultQueue,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();

        var lags = (await connection.QueryAsync<double>(new CommandDefinition(
            OutboxDispatchSql.MarkDispatched,
            new { outbox_ids = new UuidArrayParameter(claimed) },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Lag is recorded only for rows whose dispatch committed, so a rolled-back
        // batch never leaves a measurement for work that did not happen.
        foreach (var lag in lags)
        {
            _dispatchLag.Record(lag);
        }

        // 10.3.1-T1.1: one span per batch that moved rows, tagged with the batch's own
        // counts (bounded numbers, never an id — the per-row trace link travels inside
        // each payload instead, because one dispatcher serves rows of many producer
        // traces). An idle poll gets no span at all: the poller runs every second, so a
        // span per empty batch would bury the real traces.
        using var activity = TaskCorrelation.Source.StartActivity(DispatchOperationName, ActivityKind.Internal);
        activity?.SetTag("outbox.claimed", claimed.Length);
        activity?.SetTag("outbox.enqueued", enqueued.Length);

        return new OutboxDispatchResult(claimed.Length, enqueued.Length);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _meter.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The <c>outbox_undispatched</c> gauge callback: a short count over
    /// <c>outbox_undispatched_idx</c>. A failed collection yields no measurement
    /// instead of propagating — a metrics observer must never become a load that
    /// takes the worker down with it.
    /// </summary>
    /// <remarks>
    /// The callback is synchronous because this framework's
    /// <see cref="Meter.CreateObservableGauge{T}(string, Func{IEnumerable{Measurement{T}}}, string?, string?)"/>
    /// has no async form (System.Diagnostics.Metrics in .NET 10 offers only
    /// delegate-based callbacks): the gauge is collected on the metrics export
    /// interval, the count is one index-only scan on a pooled connection, and the
    /// command timeout bounds the worst case. Caching a value from the dispatch loop
    /// instead would report a stale backlog exactly when it matters — while nothing
    /// is dispatching.
    /// </remarks>
    private IEnumerable<Measurement<long>> ObserveUndispatched()
    {
        if (_disposed)
        {
            return [];
        }

        try
        {
            using var connection = _dataSource.OpenConnection();
            // Bounded twice: the SQL caps the index walk at UndispatchedGaugeCap
            // rows, and a 5 s command timeout (the ReadinessProbe precedent)
            // keeps a stalled collection from parking the export thread on
            // Npgsql's 30 s default.
            var count = connection.ExecuteScalar<long>(new CommandDefinition(
                OutboxDispatchSql.CountUndispatched,
                new { gauge_cap = OutboxDispatchSql.UndispatchedGaugeCap },
                commandTimeout: 5));

            return [new Measurement<long>(count)];
        }
        catch (Exception exception)
        {
            LogUndispatchedCountFailed(exception);
            return [];
        }
    }

    [LoggerMessage(
        EventId = 2360,
        Level = LogLevel.Warning,
        Message = "outbox_undispatched gauge collection failed; the metric reports nothing this pass")]
    private partial void LogUndispatchedCountFailed(Exception exception);
}
