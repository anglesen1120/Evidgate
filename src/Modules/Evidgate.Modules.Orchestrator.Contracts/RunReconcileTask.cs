using System.Globalization;
using System.Text.Json;

namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// The stable names of the reconcile trigger (05.3.2-T1, 00 §5.3/§7): the outbox task type a finalize
/// enqueues, its aggregate and idempotency key, and the payload it carries — one home so 05.3.2-T2's
/// reconciler consumes exactly what the finalize handler wrote instead of a second copy that could drift
/// (00 §5.5 C6).
/// <para>
/// One trigger exists per <b>newly closed</b> producer slot (05.3.2-T1 [DECISION] Q3): a duplicate
/// finalize of an already-closed slot closes nothing and enqueues nothing, so a repeated request costs
/// no background work, and the key's <c>outbox_org_idem_uq</c> dedupe catches a retry of the same close.
/// The row stays <c>queued</c> in the <c>task</c> queue until a deployment registers a handler for the
/// type (01.5.1 §11, <c>TaskQueueSql.ClaimBatch</c>'s <c>task_type = any(@task_types)</c>): an
/// unregistered type is never claimed, so it cannot burn attempts or reach <c>dead_letter</c>.
/// </para>
/// </summary>
public static class RunReconcileContract
{
    /// <summary>The outbox task type of a newly closed producer slot (05.3.2 §7, <c>ReconcileRunTask</c>).</summary>
    public const string OutboxTaskType = "orchestrator.run.reconcile";

    /// <summary>The aggregate type every reconcile row names: the run (<c>run</c>).</summary>
    public const string AggregateType = "run";

    /// <summary>snake_case, the wire convention every payload of this platform writes (00 §9.4).</summary>
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// The outbox idempotency key of one closed slot: <c>orchestrator:run:{run}:reconcile:{producer}</c>.
    /// The run id is the <c>D</c> (hyphenated, lower-case) form the other Orchestrator keys use.
    /// </summary>
    /// <param name="runId">The run whose slot was closed.</param>
    /// <param name="producerId">The shard's deterministic producer id (T54).</param>
    public static string IdempotencyKey(Guid runId, string producerId) =>
        string.Create(CultureInfo.InvariantCulture, $"orchestrator:run:{runId:D}:reconcile:{producerId}");

    /// <summary>
    /// The payload of one reconcile trigger as JSON: the run, the shard and the values that shard declared
    /// at its close (T68), which is what the reconciler compares against the acknowledged events.
    /// </summary>
    /// <param name="runId">The run whose slot was closed.</param>
    /// <param name="producerId">The shard's deterministic producer id (T54).</param>
    /// <param name="declaredLastSequence">The high-water mark the shard declared (<c>&gt;= 0</c>).</param>
    /// <param name="declaredCount">How many events the shard declared (<c>&gt;= 0</c>).</param>
    public static string PayloadJson(Guid runId, string producerId, long declaredLastSequence, int declaredCount) =>
        JsonSerializer.Serialize(
            new RunReconcilePayload(runId, producerId, declaredLastSequence, declaredCount),
            PayloadOptions);
}

/// <summary>The payload members of one <see cref="RunReconcileContract.OutboxTaskType"/> row, as written (snake_case on the wire).</summary>
/// <param name="RunId"><c>run_id</c>: the run whose producer slot was closed.</param>
/// <param name="ProducerId"><c>producer_id</c>: the closed shard's deterministic producer id (T54).</param>
/// <param name="DeclaredLastSequence"><c>declared_last_sequence</c>: the stored declaration (T68) — the first close's value on a repeat.</param>
/// <param name="DeclaredCount"><c>declared_count</c>: the stored declaration (T68) — the first close's value on a repeat.</param>
public sealed record RunReconcilePayload(
    Guid RunId,
    string ProducerId,
    long DeclaredLastSequence,
    int DeclaredCount);
