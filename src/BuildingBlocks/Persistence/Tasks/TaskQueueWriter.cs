using System.Globalization;
using System.Text;
using Dapper;
using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// One piece of work to put on the queue (01.5.1 §5). <see cref="IdempotencyKey"/>
/// is the caller's natural key for the work, and it is what makes at-least-once
/// delivery (A10) safe: enqueueing the same key twice inserts one row.
/// </summary>
public sealed record TaskEnqueue
{
    /// <summary>Queue to enqueue into; the consumer of that queue must be running.</summary>
    public required string Queue { get; init; }

    /// <summary>Handler key (`task_type`); a consumer only claims types it has a handler for.</summary>
    public required string TaskType { get; init; }

    /// <summary>Payload document; references aggregates by id, never a secret (00 §5.7 S3).</summary>
    public required string PayloadJson { get; init; }

    /// <summary>
    /// Natural idempotency key, unique across the table. Conventions: the
    /// dispatcher derives <c>'outbox:' || outbox.id</c> (01.5.1-T3), a schedule fire
    /// uses <see cref="TaskIdempotencyKeys.ScheduleFire"/>, and a module uses
    /// <c>&lt;module&gt;:&lt;aggregate&gt;:&lt;op&gt;</c> like the outbox key
    /// (01.5.1-T2.2).
    /// </summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Tenant the handler opens its scope with, or null for a platform task.</summary>
    public Guid? OrgId { get; init; }

    /// <summary>Schedule that fired this row, when it came from a cron.</summary>
    public string? ScheduleName { get; init; }

    /// <summary>The fired occurrence; set together with <see cref="ScheduleName"/>.</summary>
    public DateTime? FireAt { get; init; }

    /// <summary>Retry cap (01.5.1 §6).</summary>
    public int MaxAttempts { get; init; } = TaskQueueWriter.DefaultMaxAttempts;

    /// <summary>Earliest claim time; defaults to now.</summary>
    public DateTime? AvailableAt { get; init; }
}

/// <summary>
/// Schedule idempotency keys, in one place so a producer and its test cannot drift.
/// </summary>
public static class TaskIdempotencyKeys
{
    /// <summary>
    /// The key of one cron fire: deterministic in the pair that the unique fire
    /// index also keys on, which is what makes the second insert a no-op whether it
    /// comes from another worker in the same millisecond or from this worker an hour
    /// later after a restart.
    /// </summary>
    public static string ScheduleFire(string scheduleName, DateTimeOffset fireAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleName);

        return $"schedule:{scheduleName}:{fireAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}";
    }
}

/// <summary>
/// The enqueue side of the queue (01.5.1 §5). A module never writes this table
/// directly: it either writes an outbox row inside its own tenant transaction
/// (01.5.1-T2, the C7 path) or, for a platform task, calls this writer on the
/// connection/transaction it already owns.
/// </summary>
public interface ITaskQueueWriter
{
    /// <summary>
    /// Inserts one task row on <paramref name="connection"/> — and, when given, in
    /// <paramref name="transaction"/>. Passing the caller's transaction is the
    /// point: the task row commits with the business change, or with it rolls back,
    /// so "the change is durable" and "the work will run" are the same event.
    /// Returns the new task id, or null when the idempotency key (or the schedule
    /// fire key) was already enqueued.
    /// </summary>
    Task<Guid?> EnqueueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        TaskEnqueue task,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts one task row in its own transaction.</summary>
    Task<Guid?> EnqueueAsync(TaskEnqueue task, CancellationToken cancellationToken = default);
}

/// <summary>
/// The production writer. Both unique keys of the table are dedupe keys and the
/// insert says so: <c>on conflict do nothing</c> makes a re-enqueue of the same
/// outbox row, the same module key or the same cron occurrence a silent no-op
/// instead of a 23505 the caller would have to interpret.
/// </summary>
/// <remarks>
/// The payload it binds is the producer's document with the correlation carrier
/// (10.3.1-T1.2) when both fit the column's 64 KiB contract, and the producer's
/// document alone when they do not: a trace link is best effort and never a producer
/// failure, exactly as on the outbox path. The document is stamped once per enqueue —
/// the same string is size-checked and inserted.
/// </remarks>
public sealed class TaskQueueWriter : ITaskQueueWriter
{
    /// <summary>Retry cap a task gets when its producer does not choose one (01.5.1 §6).</summary>
    public const int DefaultMaxAttempts = 8;

    /// <summary>Matches `task_payload_size_check` (64 KiB of jsonb on disk — pg_column_size, so the wire string is bounded tighter).</summary>
    private const int MaxPayloadBytes = 65536;

    /// <summary>Matches the length checks of 0008; a longer key is a producer bug, failed here rather than by the database.</summary>
    private const int MaxIdempotencyKeyLength = 256;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Creates the writer over the app-role data source. It only ever inserts, and
    /// <c>INSERT</c> on <c>public.task</c> is the one DML the app role keeps, so the
    /// Api can compose this writer without the worker credential.
    /// </summary>
    public TaskQueueWriter(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async Task<Guid?> EnqueueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        TaskEnqueue task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Validate(task);

        var id = Guid.CreateVersion7();
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            InsertSql,
            Parameters(task, StoredPayload(task), id),
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return inserted == 1 ? id : null;
    }

    /// <inheritdoc />
    public async Task<Guid?> EnqueueAsync(TaskEnqueue task, CancellationToken cancellationToken = default)
    {
        Validate(task);

        var id = Guid.CreateVersion7();
        var parameters = Parameters(task, StoredPayload(task), id);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            InsertSql,
            parameters,
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return inserted == 1 ? id : null;
    }

    /// <summary>
    /// The insert, with every value a bound parameter (00 §5.7 S1) and the id
    /// generated by the application as a UUIDv7 (00 §7). The affected row count
    /// — 1 or 0, because the conflict clause makes the same work impossible to
    /// enqueue twice — is what tells the caller whether the row is new. A
    /// <c>returning</c> clause would need <c>SELECT</c> on <c>public.task</c>,
    /// which the app role does not hold: it may only insert, so the id travels
    /// back from here, never from the statement.
    /// </summary>
    private const string InsertSql = """
        insert into public.task
          (id, org_id, queue, task_type, payload, idempotency_key, schedule_name, fire_at,
           state, attempts, max_attempts, available_at)
        values
          (@id, @org_id, @queue, @task_type, @payload::jsonb, @idempotency_key, @schedule_name, @fire_at,
           'queued', 0, @max_attempts, coalesce(@available_at, now()))
        on conflict do nothing
        """;

    private static object Parameters(TaskEnqueue task, string payload, Guid id) => new
    {
        id,
        org_id = task.OrgId,
        queue = task.Queue,
        task_type = task.TaskType,
        // The producer's W3C context and correlation ids travel inside the payload: the
        // outbox and task tables have no headers column (10.3.1-T1.2, AC1), so a direct
        // or scheduled enqueue stamps exactly like the dispatcher's path does. The
        // stamped document is produced once per enqueue, by StoredPayload, and bound
        // here as it is: the size check and the insert see the same string.
        payload,
        idempotency_key = task.IdempotencyKey,
        schedule_name = task.ScheduleName,
        fire_at = task.FireAt,
        max_attempts = task.MaxAttempts,
        available_at = task.AvailableAt,
    };

    /// <summary>
    /// The payload the row stores: the stamped document when the carrier fits the column's
    /// contract, otherwise the producer's own document. Losing a trace link must never
    /// fail a producer, which is the fallback <see cref="Outbox.OutboxWriter"/> applies
    /// too; a document that is already too large on its own is still a producer bug and
    /// fails here, named, instead of as a bare 23514.
    /// </summary>
    /// <param name="task">The work being enqueued.</param>
    /// <returns>The JSON to bind to <c>payload</c>.</returns>
    private static string StoredPayload(TaskEnqueue task)
    {
        var stamped = TaskCorrelation.Stamp(task.PayloadJson);
        if (Encoding.UTF8.GetByteCount(stamped) <= MaxPayloadBytes)
        {
            return stamped;
        }

        // Matches `task_payload_size_check` (pg_column_size <= 64 KiB): a task is a
        // pointer to work, not the work itself — a byte count is a cheap upper bound
        // on the storage cost the database constraint measures precisely.
        if (Encoding.UTF8.GetByteCount(task.PayloadJson) > MaxPayloadBytes)
        {
            throw new ArgumentException(
                $"the task payload is longer than {MaxPayloadBytes} bytes (0008 `task_payload_size_check`)",
                nameof(task));
        }

        return task.PayloadJson;
    }

    private static void Validate(TaskEnqueue task)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.Queue);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.TaskType);
        ArgumentNullException.ThrowIfNull(task.PayloadJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.IdempotencyKey);

        if (task.IdempotencyKey.Length > MaxIdempotencyKeyLength)
        {
            throw new ArgumentException(
                $"the task idempotency key is longer than {MaxIdempotencyKeyLength} characters (0008 `task_idempotency_key_length_check`): '{task.IdempotencyKey.Length}' characters",
                nameof(task));
        }

        if (task.Queue.Length > 64)
        {
            throw new ArgumentException(
                "the queue name is longer than 64 characters (0008 `task_queue_length_check`)",
                nameof(task));
        }

        if (task.TaskType.Length > 128)
        {
            throw new ArgumentException(
                "the task_type is longer than 128 characters (0008 `task_type_length_check`)",
                nameof(task));
        }

        if (task.ScheduleName is { Length: > 128 })
        {
            throw new ArgumentException(
                "the schedule_name is longer than 128 characters (0008 `task_schedule_name_length_check`)",
                nameof(task));
        }

        // The payload's size contract is enforced by StoredPayload, on the one string the
        // insert actually binds (carrier included, with the unstamped fallback), so the
        // document is stamped exactly once per enqueue.

        // 0008's `task_fire_pair_check`: a fire key is the pair, and a half-set pair
        // would insert a row no schedule runner could ever match again.
        if ((task.ScheduleName is null) != (task.FireAt is null))
        {
            throw new ArgumentException(
                "a scheduled task carries both schedule_name and fire_at, or neither",
                nameof(task));
        }

        if (task.MaxAttempts < 1)
        {
            throw new ArgumentException("max_attempts must be at least 1", nameof(task));
        }
    }
}
