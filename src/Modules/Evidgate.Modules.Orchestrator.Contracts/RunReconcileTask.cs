using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    /// The outbox idempotency key of one accepted evidence batch that closed a synthetic producer slot:
    /// <c>orchestrator:run:{run}:reconcile:{producer}:{batch}</c>.
    /// <para>
    /// The accepted batch's own identity, not a high-water mark: a second parse of one report can insert a case that sits
    /// <em>before</em> the stored ones, so the mark does not move while the evidence does, and only the batch names the
    /// commit that enqueued the trigger. A redelivery that stored nothing enqueues no trigger at all, so every retry of
    /// one batch carries the same identity. <c>producer_id</c> never contains the <c>:</c> separator (0034's charset
    /// bound), so the key stays unambiguous.
    /// </para>
    /// </summary>
    /// <param name="runId">The run the accepted evidence belongs to.</param>
    /// <param name="producerId">The closed synthetic slot's producer id.</param>
    /// <param name="batchId">The accepted batch's receipt id, which the same commit writes.</param>
    public static string BatchIdempotencyKey(Guid runId, string producerId, Guid batchId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"orchestrator:run:{runId:D}:reconcile:{producerId}:{batchId:D}");

    /// <summary>
    /// The late-evidence shape of <see cref="BatchIdempotencyKey"/>:
    /// <c>orchestrator:run:{run}:reconcile:late:{producer}:{batch}</c>. One trigger per accepted batch, for the same
    /// reason: late batches of one producer differ by their batch, not by a mark they may not move.
    /// </summary>
    /// <param name="runId">The run the late evidence belongs to.</param>
    /// <param name="producerId">The shard that delivered it late.</param>
    /// <param name="batchId">The accepted batch's receipt id, which the same commit writes.</param>
    public static string LateBatchIdempotencyKey(Guid runId, string producerId, Guid batchId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"orchestrator:run:{runId:D}:reconcile:late:{producerId}:{batchId:D}");

    /// <summary>
    /// The reprocess shape of <see cref="BatchIdempotencyKey"/>:
    /// <c>orchestrator:run:{run}:reconcile:reprocess:{producer}:{batch}</c>. One trigger per accepted reprocess set,
    /// for the same reason a late batch names its own: the reprocess writes its own synthetic producer and its own
    /// batch, and a trigger that may compare a <c>canceled</c> run is not an ordinary one.
    /// </summary>
    /// <param name="runId">The run the re-read evidence belongs to.</param>
    /// <param name="producerId">The synthetic producer the reprocess wrote its rows under.</param>
    /// <param name="batchId">The accepted set's own idempotency id, which the same commit mints.</param>
    public static string ReprocessBatchIdempotencyKey(Guid runId, string producerId, Guid batchId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"orchestrator:run:{runId:D}:reconcile:reprocess:{producerId}:{batchId:D}");

    /// <summary>
    /// The producer id the collection-deadline trigger writes in its payload: a sweep names no shard,
    /// so the reconciler reads no declaration (the deadline path never compares a shard's high-water
    /// mark).
    /// </summary>
    public const string DeadlineProducerId = "deadline";

    /// <summary>
    /// How many collection-deadline attempts one run may spend: the first, plus two re-arms after a spent attempt.
    /// <para>
    /// A deadline reconcile that ends without a verdict — dead-lettered, finished while the run is still overdue,
    /// or purged after dispatch — leaves the run open past its deadline, so without a re-arm the run would never
    /// reach a verdict. The bound is what stops a poison run — one whose attempt always fails the same way — from
    /// queueing a trigger on every fire for ever: three attempts ride out a transient failure (a restart, a lock
    /// timeout, an app that could not decide yet) and a permanent one stops on its own, named by the sweep's
    /// warning, for an operator to pick up.
    /// </para>
    /// </summary>
    public const int DeadlineMaxAttempts = 3;

    /// <summary>
    /// The outbox idempotency key of one collection-deadline attempt:
    /// <c>orchestrator:run:{run}:reconcile:deadline</c> for the first, and
    /// <c>orchestrator:run:{run}:reconcile:deadline:{attempt}</c> for a re-arm.
    /// <para>
    /// One key per attempt, not per fire: every sweep that finds the same run overdue writes the same key for
    /// the same attempt, so <c>outbox_org_idem_uq</c> keeps two sweeps from queueing two attempts. The first
    /// attempt keeps the key rows already carry, and a later attempt exists only after the one before it was spent
    /// (<see cref="DeadlineMaxAttempts"/>).
    /// </para>
    /// </summary>
    /// <param name="runId">The run the sweep found past its collection deadline.</param>
    /// <param name="attempt">1-based attempt number; 1 (or lower) is the key the first sweep writes.</param>
    public static string DeadlineIdempotencyKey(Guid runId, int attempt = 1) =>
        attempt <= 1
            ? string.Create(CultureInfo.InvariantCulture, $"orchestrator:run:{runId:D}:reconcile:{DeadlineProducerId}")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"orchestrator:run:{runId:D}:reconcile:{DeadlineProducerId}:{attempt}");

    /// <summary>
    /// The payload of the collection-deadline trigger: the run and the two declaration fields a payload
    /// of this type always carries, at zero, which the deadline path ignores.
    /// <para>
    /// Every attempt of one run carries this same document, so the only thing that tells two attempts apart is
    /// the key — and a key is never reused with a different payload.
    /// </para>
    /// </summary>
    /// <param name="runId">The run the sweep found past its collection deadline.</param>
    public static string DeadlinePayloadJson(Guid runId) =>
        PayloadJson(runId, DeadlineProducerId, declaredLastSequence: 0, declaredCount: 0);

    /// <summary>
    /// The outbox idempotency key of one accepted late batch:
    /// <c>orchestrator:run:{run}:reconcile:late:{producer}:{lastSequence}</c>.
    /// <para>
    /// The run finished as <c>incomplete</c>, so no finalize will ever close another slot and nothing else would
    /// reconcile the late evidence into a revision. The key names the shard and the high-water mark that batch added:
    /// two late batches carry two marks and enqueue two triggers, while a resend of evidence already stored inserts no
    /// row at all (the ingest handler enqueues only for accepted rows), so the duplicate can never append a second
    /// revision. <c>producer_id</c> never contains the <c>:</c> separator (0034's charset bound), so the key stays
    /// unambiguous.
    /// </para>
    /// </summary>
    /// <param name="runId">The run the late evidence belongs to.</param>
    /// <param name="producerId">The shard that delivered it late.</param>
    /// <param name="lastSequence">The highest sequence that batch newly stored for that shard.</param>
    public static string LateIdempotencyKey(Guid runId, string producerId, long lastSequence) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"orchestrator:run:{runId:D}:reconcile:late:{producerId}:{lastSequence}");

    /// <summary>
    /// The payload of the late-evidence trigger: the run and the delivering shard, with the two declaration fields a
    /// payload of this type always carries, at zero, and <see cref="RunReconcilePayload.LateEvidence"/> set. The reconciler
    /// re-reads the slot's stored declaration under its own lock, so a declaration copied here could only drift from it
    /// (the deadline trigger takes the same shape).
    /// </summary>
    /// <param name="runId">The run the late evidence belongs to.</param>
    /// <param name="producerId">The shard that delivered it late.</param>
    public static string LatePayloadJson(Guid runId, string producerId) =>
        JsonSerializer.Serialize(
            new RunReconcilePayload(runId, producerId, 0, 0) { LateEvidence = true },
            PayloadOptions);

    /// <summary>
    /// The payload of the reprocess trigger: the run and the synthetic producer the re-read rows were written under, with
    /// the two declaration fields a payload of this type always carries, at zero, and
    /// <see cref="RunReconcilePayload.ReprocessEvidence"/> set. Only the reprocess task builds it, and it sets the marker
    /// on every trigger it writes, whatever state its run is in: a canceled or complete run, and a live run, each take a
    /// new revision from it without their run state moving.
    /// </summary>
    /// <param name="runId">The run the re-read evidence belongs to.</param>
    /// <param name="producerId">The synthetic producer the reprocess wrote its rows under.</param>
    public static string ReprocessPayloadJson(Guid runId, string producerId) =>
        JsonSerializer.Serialize(
            new RunReconcilePayload(runId, producerId, 0, 0) { ReprocessEvidence = true },
            PayloadOptions);

    /// <summary>
    /// The task type (and schedule name) of the collection deadline sweep: a platform task that scans
    /// every tenant's overdue runs and enqueues one <see cref="OutboxTaskType"/> row per run. It is a
    /// separate type from the trigger it produces, so an unregistered handler parks the sweep without
    /// touching the finalize path.
    /// </summary>
    public const string DeadlineSweepTaskType = "orchestrator.run.reconcile.deadline";

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
    int DeclaredCount)
{
    /// <summary>
    /// <c>late_evidence</c>: set only by the trigger the ingest handler enqueues for a late batch it accepted. A finalize
    /// close and a deadline sweep leave it absent, so the reconciler can tell "late evidence arrived" from "an ordinary
    /// trigger was redelivered or replayed" — only the former may compare a run that already finished, and the marker
    /// alone says so, because the window was decided when the batch was accepted.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LateEvidence { get; init; }

    /// <summary>
    /// <c>reprocess_evidence</c>: set by every trigger the reprocess task writes, whichever state its run was in. An
    /// ordinary, late or finalize trigger leaves it absent, so this marker is what tells the reconciler those rows were a
    /// deliberate re-reading of the run's own stored report: a canceled or complete run appends a revision and keeps its
    /// state, an incomplete run records the evidence as late, and a live run appends beside its revision — or waits for the
    /// collection settlement to freeze it — and never has a settlement decided by it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReprocessEvidence { get; init; }
}
