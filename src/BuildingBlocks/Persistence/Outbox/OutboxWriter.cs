using System.Text;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.BuildingBlocks.Persistence.Outbox;

/// <summary>
/// Dapper over the ambient tenant scope (01.5.1-T2.2). Stateless: the scope is
/// resolved per call, so one registered instance serves every command flow, and
/// the INSERT always lands on the scope's connection and transaction (00 §5.5 C3)
/// — never on a connection of its own, which is what makes the outbox
/// transactional rather than best-effort.
/// </summary>
public sealed class OutboxWriter : IOutboxWriter
{
    /// <summary>
    /// `org_id` is taken from the scope, never from the caller, so a command can
    /// only ever enqueue against its own tenant. The JSON payload is cast at the
    /// statement, keeping the call site free of Npgsql type plumbing; an invalid
    /// document is rejected by the column type (22P02).
    /// </summary>
    private const string InsertSql = """
        insert into public.outbox
          (id, org_id, aggregate_type, aggregate_id, task_type, payload, idempotency_key)
        values
          (@Id, @OrgId, @AggregateType, @AggregateId, @TaskType, @Payload::jsonb, @IdempotencyKey)
        """;

    /// <summary>Matches 0006's length/size checks; a producer bug is failed here, named, rather than by a bare 23514.</summary>
    private const int MaxTypeLength = 128;

    private const int MaxIdempotencyKeyLength = 256;

    private const int MaxPayloadBytes = 65536;

    private readonly ITenantDb _db;
    private readonly ITenantScopeAccessor _scopes;

    /// <summary>Creates the writer over the Dapper path and the ambient scope.</summary>
    public OutboxWriter(ITenantDb db, ITenantScopeAccessor scopes)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scopes);
        _db = db;
        _scopes = scopes;
    }

    /// <inheritdoc />
    public async Task<Guid> EnqueueAsync(
        string taskType,
        string payloadJson,
        string aggregateType,
        Guid aggregateId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateType);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        if (taskType.Length > MaxTypeLength)
        {
            throw new ArgumentException(
                $"the outbox task_type is longer than {MaxTypeLength} characters (0006 `outbox_task_type_length_check`)",
                nameof(taskType));
        }

        if (aggregateType.Length > MaxTypeLength)
        {
            throw new ArgumentException(
                $"the outbox aggregate_type is longer than {MaxTypeLength} characters (0006 `outbox_aggregate_type_length_check`)",
                nameof(aggregateType));
        }

        if (idempotencyKey.Length > MaxIdempotencyKeyLength)
        {
            throw new ArgumentException(
                $"the outbox idempotency key is longer than {MaxIdempotencyKeyLength} characters (0006 `outbox_idempotency_key_length_check`)",
                nameof(idempotencyKey));
        }

        if (Encoding.UTF8.GetByteCount(payloadJson) > MaxPayloadBytes)
        {
            throw new ArgumentException(
                $"the outbox payload is longer than {MaxPayloadBytes} bytes (0006 `outbox_payload_size_check`)",
                nameof(payloadJson));
        }

        // 10.3.1-T1.2 (AC1): the producing activity's W3C context and the correlation
        // ids travel inside the payload, because neither `outbox` nor `task` has a
        // headers column and this task adds no migration. Stamping stays inside the
        // column's size contract: a payload that would cross the cap with the carrier
        // is stored without it — losing a trace link never fails a producer.
        var stamped = TaskCorrelation.Stamp(payloadJson);
        var stored = Encoding.UTF8.GetByteCount(stamped) > MaxPayloadBytes ? payloadJson : stamped;

        // The scope is required before the statement runs, so the failure is
        // TenantScopeMissingException rather than a write on some other connection.
        var scope = _scopes.Require();
        var id = Guid.CreateVersion7();

        var affected = await _db.ExecuteAsync(
            InsertSql,
            new
            {
                Id = id,
                OrgId = scope.OrgId,
                AggregateType = aggregateType,
                AggregateId = aggregateId,
                TaskType = taskType,
                Payload = stored,
                IdempotencyKey = idempotencyKey,
            },
            cancellationToken).ConfigureAwait(false);

        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"enqueuing outbox row {id:D} affected {affected} rows instead of one.");
        }

        return id;
    }
}
