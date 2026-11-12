using Dapper;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// `task.sweep` (T11 / 01.5.1 §7): deletes terminal task rows (`done`,
/// `dead_letter`) older than the retention window, in bounded batches.
/// </summary>
/// <remarks>
/// This one runs on the keyed worker-role connection: the retention delete is the only
/// path that removes a task row, so <c>DELETE</c> on <c>public.task</c> belongs to
/// <c>evidgate_worker</c> and the app role never holds it.
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

    public TaskSweepTaskHandler(
        [FromKeyedServices(TaskWorkerDataSource.Key)] NpgsqlDataSource dataSource,
        ILogger<TaskSweepTaskHandler> logger)
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
        EventId = 2323,
        Level = LogLevel.Information,
        Message = "task.sweep removed {Removed} terminal task rows older than {Days} days")]
    private partial void LogSwept(int removed, int days);
}
