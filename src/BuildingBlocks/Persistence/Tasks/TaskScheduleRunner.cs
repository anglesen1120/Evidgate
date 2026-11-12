using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// The cron side of the mechanism (01.5.1-T1.4): every Worker computes the
/// occurrences due in a lookback window and inserts one task row per occurrence,
/// inside one transaction per schedule. There is no leader and no schedule state
/// row — the unique <c>(schedule_name, fire_at)</c> key is the whole election
/// (00 §5.3): two workers computing the same occurrence both try to insert it and
/// exactly one row exists, so a fire is neither lost nor doubled.
/// </summary>
/// <remarks>
/// `pg_try_advisory_xact_lock(hashtext(schedule_name))` is a fast path in front of
/// that guarantee, not the guarantee itself: a worker that fails to take the lock
/// simply skips the schedule this pass instead of racing its siblings through the
/// insert. The lock is transaction-scoped, so a worker that dies mid-fire releases
/// it with its transaction, and the next pass (whose lookback window still covers
/// the occurrence, because the window is wider than the poll interval) fires it.
/// </remarks>
public sealed partial class TaskScheduleRunner
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IScheduleRegistry _registry;
    private readonly ITaskQueueWriter _writer;
    private readonly TimeSpan _lookback;
    private readonly ILogger<TaskScheduleRunner> _logger;

    /// <summary>Creates the runner over the keyed worker-role data source.</summary>
    public TaskScheduleRunner(
        [FromKeyedServices(TaskWorkerDataSource.Key)] NpgsqlDataSource dataSource,
        IScheduleRegistry registry,
        ITaskQueueWriter writer,
        IOptions<TasksOptions> options,
        ILogger<TaskScheduleRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _dataSource = dataSource;
        _registry = registry;
        _writer = writer;
        _lookback = TimeSpan.FromSeconds(options.Value.ScheduleLookbackSeconds);
        _logger = logger;
    }

    /// <summary>
    /// Fires every occurrence due in <c>(nowUtc - lookback, nowUtc]</c> for every
    /// registered schedule, and returns how many task rows this pass inserted
    /// (zero is the normal case: nothing was due, or a sibling already fired it).
    /// </summary>
    /// <param name="nowUtc">
    /// The instant the pass runs as. Explicit so the window is testable without a
    /// clock and so a caller cannot accidentally mix local time into a UTC cron.
    /// </param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    public async Task<int> RunOnceAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var schedules = _registry.Schedules;
        if (schedules.Count == 0)
        {
            return 0;
        }

        var to = nowUtc.UtcDateTime;
        var from = to - _lookback;
        var fired = 0;

        foreach (var schedule in schedules)
        {
            fired += await FireAsync(schedule, from, to, cancellationToken).ConfigureAwait(false);
        }

        return fired;
    }

    /// <summary>
    /// One schedule's pass: the due occurrences of the window, one transactional
    /// insert each. The whole pass is one transaction, so the advisory lock covers
    /// exactly the inserts it guards and nothing else.
    /// </summary>
    private async Task<int> FireAsync(
        ScheduleDefinition schedule,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        // The window is (from, to]: strictly after the last lookback boundary so a
        // pass never re-fires an occurrence it already saw, and up to and including
        // `now` so the tick that just happened is not pushed to the next pass.
        var occurrences = schedule.Expression
            .GetOccurrences(from, to, fromInclusive: false, toInclusive: true)
            .ToList();

        if (occurrences.Count == 0)
        {
            return 0;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var ownsFire = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select pg_try_advisory_xact_lock(hashtext(@schedule_name))",
            new { schedule_name = schedule.Name },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (!ownsFire)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            LogFireSkipped(schedule.Name);
            return 0;
        }

        var fired = 0;
        foreach (var occurrence in occurrences)
        {
            var fireAt = new DateTimeOffset(DateTime.SpecifyKind(occurrence, DateTimeKind.Utc));
            var id = await _writer.EnqueueAsync(
                connection,
                transaction,
                new TaskEnqueue
                {
                    Queue = schedule.Queue,
                    TaskType = schedule.TaskType,
                    PayloadJson = schedule.PayloadFactory(fireAt),
                    IdempotencyKey = TaskIdempotencyKeys.ScheduleFire(schedule.Name, fireAt),
                    OrgId = null,
                    ScheduleName = schedule.Name,
                    FireAt = fireAt.UtcDateTime,
                },
                cancellationToken).ConfigureAwait(false);

            if (id is not null)
            {
                fired++;
                LogFired(schedule.Name, schedule.Queue, fireAt);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return fired;
    }

    [LoggerMessage(
        EventId = 2310,
        Level = LogLevel.Information,
        Message = "Schedule {Schedule} fired for {FireAt:O} into queue {Queue}")]
    private partial void LogFired(string schedule, string queue, DateTimeOffset fireAt);

    [LoggerMessage(
        EventId = 2311,
        Level = LogLevel.Debug,
        Message = "Schedule {Schedule} skipped this pass: another worker holds the fire lock")]
    private partial void LogFireSkipped(string schedule);
}
