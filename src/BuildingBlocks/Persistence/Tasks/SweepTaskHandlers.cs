using Dapper;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// `idempotency.purge` (01.5.1-T6.2): calls the owner
/// <c>SECURITY DEFINER</c> function <c>evidgate.purge_expired_idempotency</c> as
/// <c>evidgate_worker_sched</c> (TL T80), in bounded batches, until the backlog is
/// drained or the fire's batch budget is spent.
/// </summary>
/// <remarks>
/// The app role has no <c>EXECUTE</c> on that function, so this handler is the only
/// path to it: the sweep runs on the dedicated connection of
/// <see cref="IWorkerSchedDataSource"/>. T80 also asks for every call to be
/// audited — the audit table is 02.7.1's `audit_log` (M2), so until it exists the
/// audit trail is this Information log line, which carries the function, the batch
/// limit and the number of rows removed (recorded as a deviation for M1).
/// </remarks>
public sealed partial class IdempotencyPurgeTaskHandler : ITaskHandler<IdempotencyPurgePayload>
{
    /// <inheritdoc />
    public bool RequiresTenant => false;

    /// <summary>The one call this handler makes, as the frozen 01.5.1-T6.1 signature.</summary>
    internal const string PurgeFunctionCall = "select evidgate.purge_expired_idempotency(@limit)";

    private readonly IWorkerSchedDataSource _sweep;
    private readonly ILogger<IdempotencyPurgeTaskHandler> _logger;

    public IdempotencyPurgeTaskHandler(IWorkerSchedDataSource sweep, ILogger<IdempotencyPurgeTaskHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentNullException.ThrowIfNull(logger);

        _sweep = sweep;
        _logger = logger;
    }

    /// <inheritdoc />
    public string TaskType => EvidgateSweeps.IdempotencyPurgeTaskType;

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        IdempotencyPurgePayload payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // A platform task: `scope` is null because the row carries no org_id. The
        // purge is cross-tenant by design — the owner function's `for update skip
        // locked` batch is what keeps a concurrent purge safe.
        await using var connection = await _sweep.OpenAsync(cancellationToken).ConfigureAwait(false);

        // The owner function caps its own batch (`least(greatest(p_limit, 0), 10000)`,
        // 0007), so the loop works in the function's units: `deleted < limit` is only
        // a drained backlog when `limit` is what the function actually deleted up to.
        // A payload asking for more than the cap would stop after the first capped
        // batch, and a non-positive one would ask for zero rows and spend the whole
        // batch budget deleting nothing (a task row is replayable by an operator, so
        // the payload is not trusted to stay in range).
        var limit = Math.Clamp(payload.Limit, 1, EvidgateSweeps.PurgeMaxBatchSize);

        var removed = 0;
        for (var batch = 0; batch < EvidgateSweeps.PurgeMaxBatchesPerFire; batch++)
        {
            var deleted = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                PurgeFunctionCall,
                new { limit },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            removed += deleted;

            // A short batch means the backlog is drained; looping again would be a
            // second round trip that can only return zero.
            if (deleted < limit)
            {
                break;
            }
        }

        LogPurged(removed, limit);
    }

    [LoggerMessage(
        EventId = 2320,
        Level = LogLevel.Information,
        Message = "Audited sweep evidgate.purge_expired_idempotency as evidgate_worker_sched removed {Removed} expired records (batch limit {Limit})")]
    private partial void LogPurged(int removed, int limit);
}

/// <summary>
/// `task.sweep` (T11 / 01.5.1 §7): deletes terminal task rows (`done`,
/// `dead_letter`) older than the retention window, in bounded batches.
/// </summary>
/// <remarks>
/// This one runs on the ordinary app-role connection: `task` is the mechanism's
/// own table, its DELETE grant is exactly this retention path (0008), and nothing
/// else in the system deletes a task row.
/// </remarks>
public sealed partial class TaskSweepTaskHandler : ITaskHandler<TaskSweepPayload>
{
    /// <inheritdoc />
    public bool RequiresTenant => false;

    /// <summary>
    /// The retention delete. `state` and `created_at` are the predicate of
    /// `task_terminal_idx`, and the bounded `for update skip locked` batch keeps the
    /// statement short when a backlog is large.
    /// </summary>
    internal const string SweepSql = """
        delete from public.task
         where id in (
                 select id
                   from public.task
                  where state in ('done', 'dead_letter')
                    and created_at < now() - make_interval(days => @days)
                  order by created_at
                  limit @batch_size
                    for update skip locked)
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<TaskSweepTaskHandler> _logger;

    public TaskSweepTaskHandler(NpgsqlDataSource dataSource, ILogger<TaskSweepTaskHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(logger);

        _dataSource = dataSource;
        _logger = logger;
    }

    /// <inheritdoc />
    public string TaskType => EvidgateSweeps.TaskSweepTaskType;

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        TaskSweepPayload payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // The window is bounded below at one day: a task row is replayable by an
        // operator, so an out-of-range payload (e.g. a negative day count, which
        // would compute `created_at < now() + n days` and delete every recent
        // terminal row) must not turn the retention sweep into a data-loss path.
        var days = Math.Max(payload.OlderThanDays, 1);

        var removed = 0;
        for (var batch = 0; batch < EvidgateSweeps.TaskSweepMaxBatchesPerFire; batch++)
        {
            var deleted = await connection.ExecuteAsync(new CommandDefinition(
                SweepSql,
                new { days, batch_size = EvidgateSweeps.TaskSweepBatchSize },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            removed += deleted;

            if (deleted < EvidgateSweeps.TaskSweepBatchSize)
            {
                break;
            }
        }

        LogSwept(removed, days);
    }

    [LoggerMessage(
        EventId = 2321,
        Level = LogLevel.Information,
        Message = "task.sweep removed {Removed} terminal task rows older than {Days} days")]
    private partial void LogSwept(int removed, int days);
}
