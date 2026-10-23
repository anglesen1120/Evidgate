using Dapper;
using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// The outcome of <c>POST /internal/operator/tasks/dead-letter/{id}/replay</c>
/// (01.5.1 §5): <see cref="Replayed"/> carries the row's new
/// <c>available_at</c>, <see cref="NotFound"/> and <see cref="NotDeadLetter"/>
/// discriminate the two ways the guarded update matched no row.
/// </summary>
public enum ReplayDeadLetterOutcome
{
    /// <summary>The row was `dead_letter` and is now `queued` with its attempts reset.</summary>
    Replayed,

    /// <summary>No task row exists with that id at all.</summary>
    NotFound,

    /// <summary>The row exists but is not `dead_letter` (e.g. it was already replayed or reclaimed).</summary>
    NotDeadLetter,
}

/// <summary>The row the replay returned, or the failure outcome.</summary>
public sealed record ReplayDeadLetterResult(ReplayDeadLetterOutcome Outcome, DateTime? AvailableAt)
{
    /// <summary>A successful replay; <see cref="AvailableAt"/> is the row's new value.</summary>
    public static ReplayDeadLetterResult Replayed(DateTime availableAt) =>
        new(ReplayDeadLetterOutcome.Replayed, availableAt);

    /// <summary>No row with that id exists.</summary>
    public static ReplayDeadLetterResult NotFound() =>
        new(ReplayDeadLetterOutcome.NotFound, null);

    /// <summary>The row exists in a state that cannot be replayed.</summary>
    public static ReplayDeadLetterResult NotDeadLetter() =>
        new(ReplayDeadLetterOutcome.NotDeadLetter, null);
}

/// <summary>
/// The replay of 01.5.1 §5/T4.2 (`dead_letter → queued`, §6): one guarded
/// <c>UPDATE … RETURNING</c> whose predicate is the capability — a row that is
/// not <c>dead_letter</c> simply does not match, which is why the endpoint is
/// idempotent and needs no <c>ETag</c> or <c>Idempotency-Key</c>.
/// </summary>
/// <remarks>
/// Deliberately NOT an <c>ICommand</c> through the Mediator C2 pipeline (01.5.1
/// §5 "operator routes carry no tenant context"): a Result-contract command
/// would trip <c>TenantTransactionBehaviour</c>'s fail-closed Require() on a
/// request that legitimately has no org. Operator surface → app-role data
/// source → this service — the same posture the queue's own writers take.
/// </remarks>
public sealed class ReplayDeadLetterTask
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the replay service over the registered app-role data source.</summary>
    public ReplayDeadLetterTask(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
    }

    /// <summary>
    /// Requeues the dead-lettered row <paramref name="taskId"/>: clears the lease,
    /// resets the attempts the row spent on its last handler run and makes it
    /// immediately claimable. The row keeps its <c>last_error</c> — a replay is
    /// a re-queue, not an acquittal, and the operator's audit of why it
    /// dead-lettered stays on the row.
    /// </summary>
    /// <param name="taskId">The dead-lettered row's id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task<ReplayDeadLetterResult> ExecuteAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var availableAt = await connection.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(
            TaskQueueSql.ReplayDeadLetter,
            new { id = taskId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (availableAt is { } replayed)
        {
            return ReplayDeadLetterResult.Replayed(replayed);
        }

        // The update's guard is the only state that ever matters to the caller:
        // no dead-letter row matched, so the row is either absent (404) or in
        // another state (409). Same connection, so a concurrently replaying
        // operator sees the state after the guards — the transaction isolation
        // does not matter for a read of a row another transaction may have moved.
        var state = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            TaskQueueSql.TaskStateById,
            new { id = taskId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return state is null
            ? ReplayDeadLetterResult.NotFound()
            : ReplayDeadLetterResult.NotDeadLetter();
    }
}
