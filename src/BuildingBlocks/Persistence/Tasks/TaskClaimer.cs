using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// One claimed task row, in the column order of
/// <see cref="TaskQueueSql.ClaimBatch"/> (Dapper binds a positional record by
/// position and name, so the two are kept in step).
/// </summary>
/// <param name="Id">Task id (UUIDv7).</param>
/// <param name="OrgId">Tenant the handler must open its scope with, or null for a platform task.</param>
/// <param name="Queue">Queue the row was claimed from.</param>
/// <param name="TaskType">Handler key.</param>
/// <param name="Payload">The payload JSON, exactly as stored.</param>
/// <param name="IdempotencyKey">Natural idempotency key of the work.</param>
/// <param name="ScheduleName">Schedule that fired it, or null for an outbox/module task.</param>
/// <param name="FireAt">The fired occurrence, or null.</param>
/// <param name="State">Always `active` for a returned claim.</param>
/// <param name="Attempts">Attempts made, this one included.</param>
/// <param name="MaxAttempts">Retry cap; the last attempt lands in `dead_letter`.</param>
/// <param name="AvailableAt">When the row was due.</param>
/// <param name="LockedUntil">Lease deadline the claim took; the completion is conditional on it.</param>
/// <param name="Version">
/// The row's optimistic-concurrency token as the claim left it (the shared trigger
/// bumps it on every update). It is the lease token: a completion or failure
/// transition only applies while the row still carries it, so a stale attempt cannot
/// overwrite the newer attempt's outcome, whatever its `locked_until` happens to be.
/// </param>
public sealed record TaskClaim(
    Guid Id,
    Guid? OrgId,
    string Queue,
    string TaskType,
    string Payload,
    string IdempotencyKey,
    string? ScheduleName,
    DateTime? FireAt,
    string State,
    int Attempts,
    int MaxAttempts,
    DateTime AvailableAt,
    DateTime LockedUntil,
    int Version);

/// <summary>
/// The claim side of the queue (01.5.1-T1.1, 01.4.2-T2.1 / AC7): a `for update skip
/// locked` batch claim for one queue, plus the lease-expiry reclaim that makes a
/// crashed consumer's row claimable again.
/// </summary>
public interface ITaskClaimer
{
    /// <summary>
    /// Reclaims expired leases of <paramref name="queue"/>, then claims up to
    /// <paramref name="batchSize"/> due rows and takes each row's lease. Returns the
    /// claimed rows, oldest due first; an empty list is "no work right now", never a
    /// failure.
    /// </summary>
    /// <param name="queue">Queue to poll.</param>
    /// <param name="taskTypes">
    /// Handler keys this consumer can run. A row whose type is not in the set stays
    /// `queued` for the consumer that can (01.5.1 §11).
    /// </param>
    /// <param name="batchSize">Largest number of rows to claim.</param>
    /// <param name="cancellationToken">Cancels the claim pass.</param>
    Task<IReadOnlyList<TaskClaim>> ClaimAsync(
        string queue,
        IReadOnlyCollection<string> taskTypes,
        int batchSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the visibility timeout to <paramref name="queue"/>: rows whose lease
    /// ran out go back to `queued` when they have attempts left, and to
    /// `dead_letter` when they do not. Returns how many rows came back.
    /// </summary>
    Task<int> ReclaimExpiredAsync(string queue, CancellationToken cancellationToken = default);
}

/// <summary>
/// One row of the <c>RETURNING</c> clause of <see cref="TaskQueueSql.DeadLetterExpiredLeases"/>:
/// the projection the alert hook (01.5.1 §7) turns into one
/// <see cref="TaskDeadLetterAlert"/> per dead-lettered row once the reclaim has
/// committed. Internal: it exists to shape one SQL result, not to leave the
/// mechanism.
/// </summary>
internal sealed record DeadLetteredLeaseRow(
    Guid Id,
    Guid? OrgId,
    string Queue,
    string TaskType,
    int Attempts,
    int MaxAttempts,
    string? LastError);

/// <summary>
/// The production claim path: Dapper over the app-role <c>NpgsqlDataSource</c>
/// (01.5.1 §8 S1 — parameterised SQL, no string building). The claim is its own
/// short transaction, and the handler runs outside it (§8 P5): no row lock is held
/// while a handler works, which is exactly what `for update skip locked` plus an
/// explicit lease buys.
/// </summary>
public sealed partial class TaskClaimer : ITaskClaimer
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ITaskAlertSink _alerts;
    private readonly int _visibilityTimeoutSeconds;
    private readonly int _reclaimBatchSize;
    private readonly ILogger<TaskClaimer> _logger;

    /// <summary>Creates the claimer over the registered app-role data source.</summary>
    public TaskClaimer(
        NpgsqlDataSource dataSource,
        ITaskAlertSink alerts,
        IOptions<TasksOptions> options,
        ILogger<TaskClaimer> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _dataSource = dataSource;
        _alerts = alerts;
        _visibilityTimeoutSeconds = options.Value.VisibilityTimeoutSeconds;
        _reclaimBatchSize = options.Value.BatchSize;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TaskClaim>> ClaimAsync(
        string queue,
        IReadOnlyCollection<string> taskTypes,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentNullException.ThrowIfNull(taskTypes);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        string[] types = [.. taskTypes];

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var reclaim = await ReclaimAsync(connection, transaction, queue, cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<TaskClaim>(new CommandDefinition(
            TaskQueueSql.ClaimBatch,
            new
            {
                queue,
                task_types = new TextArrayParameter(types),
                batch_size = batchSize,
                visibility_timeout = _visibilityTimeoutSeconds,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // The alert hook of 01.5.1 §7 fires only after the commit AND after the
        // pooled connection has been returned: a slow or non-compliant sink is
        // bounded by nothing but the caller's token, so it must not hold a pool
        // slot while it runs (perf review wave 5).
        await transaction.DisposeAsync().ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
        await NotifyDeadLetteredAsync(reclaim.DeadLettered, cancellationToken).ConfigureAwait(false);

        return rows as IReadOnlyList<TaskClaim> ?? [.. rows];
    }

    /// <inheritdoc />
    public async Task<int> ReclaimExpiredAsync(string queue, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var reclaim = await ReclaimAsync(connection, transaction, queue, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        await transaction.DisposeAsync().ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
        await NotifyDeadLetteredAsync(reclaim.DeadLettered, cancellationToken).ConfigureAwait(false);

        return reclaim.Requeued;
    }

    /// <summary>
    /// The two halves of the visibility timeout (§6), in the caller's transaction:
    /// rows with attempts left come back as `queued`, rows at the cap give up as
    /// `dead_letter` — never silently, so a crash loop ends where the operator can
    /// see it. The dead-lettered rows come back with the commit so the alert hook
    /// can name each row after it is durable, not before.
    /// </summary>
    private async Task<(int Requeued, IReadOnlyList<DeadLetteredLeaseRow> DeadLettered)> ReclaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string queue,
        CancellationToken cancellationToken)
    {
        var batch = new { queue, batch_size = _reclaimBatchSize };

        var requeued = await connection.ExecuteAsync(new CommandDefinition(
            TaskQueueSql.ReclaimExpiredLeases,
            batch,
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var deadLettered = await connection.QueryAsync<DeadLetteredLeaseRow>(new CommandDefinition(
            TaskQueueSql.DeadLetterExpiredLeases,
            batch,
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var rows = deadLettered as IReadOnlyList<DeadLetteredLeaseRow> ?? [.. deadLettered];

        if (rows.Count > 0)
        {
            LogLeaseExpiredAtCap(queue, rows.Count);
        }

        return (requeued, rows);
    }

    /// <summary>
    /// Tells the alert sink about every row a committed reclaim dead-lettered
    /// (01.5.1 §7). Delivery is per row because a batch can hold more than one
    /// poison task, and it is guarded because a sink failure must never break the
    /// claim path it runs inside.
    /// </summary>
    private async Task NotifyDeadLetteredAsync(
        IReadOnlyList<DeadLetteredLeaseRow> rows,
        CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            await TaskAlertNotifier
                .NotifySafelyAsync(
                    _alerts,
                    _logger,
                    new TaskDeadLetterAlert(
                        TaskId: row.Id,
                        Queue: row.Queue,
                        TaskType: row.TaskType,
                        OrgId: row.OrgId,
                        Attempts: row.Attempts,
                        MaxAttempts: row.MaxAttempts,
                        LastError: row.LastError,
                        Source: TaskDeadLetterSource.ExpiredLease),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    [LoggerMessage(
        EventId = 2350,
        Level = LogLevel.Error,
        Message = "{Count} task row(s) of queue {Queue} dead-lettered: their lease expired with every attempt already used (a consumer that died, or a handler that killed the process)")]
    private partial void LogLeaseExpiredAtCap(string queue, int count);
}
