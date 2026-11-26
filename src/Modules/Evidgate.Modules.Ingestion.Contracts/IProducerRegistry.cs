using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Ingestion.Contracts;

/// <summary>Registers manifest shards and binds each slot to the authenticated owner on its first write.</summary>
public interface IProducerRegistry
{
    /// <summary>
    /// Registers exactly <paramref name="shardCount"/> slots for <paramref name="runId"/> - one per
    /// manifest shard, <c>shard-1</c>..<c>shard-N</c> (T54) - inside the caller's tenant transaction,
    /// so the slots commit with the run row or not at all (T13).
    /// </summary>
    /// <param name="runId">The run the slots belong to.</param>
    /// <param name="shardCount">The manifest's shard count: 1..<see cref="ProducerSlotLimits.MaxShards"/>.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>
    /// The registered slots, or a failure when the count is out of range or the run already has slots.
    /// <para>
    /// A failure means the caller MUST roll back its transaction: a concurrent registration with a
    /// different count can leave a partial slot set inside it (the statement inserts what it can and
    /// the conflict is reported afterwards), so the caller must not commit the run row on a failure.
    /// </para>
    /// </returns>
    ValueTask<Result<IReadOnlyList<ProducerSlotRef>>> RegisterSlotsAsync(
        Guid runId,
        int shardCount,
        CancellationToken cancellationToken);

    /// <summary>
    /// Binds the slot of <paramref name="producerId"/> to the caller's stable credential id on the first
    /// accepted write, or checks that the caller is the owner already bound (T9). The bind is one
    /// conditional <c>UPDATE … WHERE bound_token_jti IS NULL</c> (the owner column keeps its historical
    /// name), so two racing first writers cannot both win.
    /// </summary>
    /// <param name="runId">The run the slot belongs to.</param>
    /// <param name="producerId">The manifest shard's deterministic producer id (T54).</param>
    /// <param name="ownerId">Stable authenticated credential identity, never a bearer token or JWT id.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>
    /// The slot's binding, or a failure: <c>request_invalid</c> (422) when an argument is outside the
    /// column bounds, <c>unknown_producer</c> (422) when the run has no such slot,
    /// <c>producer_slot_taken</c> (403) when the slot belongs to another owner.
    /// </returns>
    ValueTask<Result<ProducerSlotBinding>> BindOrCheckAsync(
        Guid runId,
        string producerId,
        string ownerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Closes the slot of <paramref name="producerId"/> once, storing the shard's declared values (T68,
    /// AC9). The close is idempotent on <c>producer_slot.closed_at</c>: a repeat call by the same
    /// identity returns the values already stored, with no update and no version bump, so a duplicate
    /// finalize is a 202-equivalent no-op (05.3.2 AC6/D3). The statement is one conditional
    /// <c>UPDATE … WHERE closed_at IS NULL</c> whose <c>WHERE</c> carries the whole identity guard, so
    /// two racing finalizes cannot both write.
    /// <para>
    /// A repeat call answers with the STORED values, never with the ones this caller sent (L3), so the
    /// caller MUST compare the returned declared values with its own (05.3.2-T2.2): an equal pair is a
    /// duplicate finalize of the same declaration, a different pair is this shard's declarations
    /// disagreeing with the row and is the caller's to reconcile.
    /// </para>
    /// </summary>
    /// <param name="runId">The run the slot belongs to.</param>
    /// <param name="producerId">The manifest shard's deterministic producer id (T54).</param>
    /// <param name="ownerId">Stable authenticated credential identity.</param>
    /// <param name="declaredLastSequence">The highest producer sequence the shard declares it sent; <c>&gt;= 0</c>.</param>
    /// <param name="declaredCount">How many events the shard declares it sent; <c>&gt;= 0</c>.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>
    /// The slot's closure (the stored declared values and <c>closed_at</c>, with <see cref="ProducerSlotClosure.Closed"/>
    /// true only when this call wrote them), or a failure: <c>request_invalid</c> (422) when a declared value is
    /// outside the column bound, <c>unknown_producer</c> (422) when the run has no such slot,
    /// <c>producer_slot_taken</c> (403) when the slot is unbound or belongs to another owner.
    /// </returns>
    ValueTask<Result<ProducerSlotClosure>> CloseSlotAsync(
        Guid runId,
        string producerId,
        string ownerId,
        long declaredLastSequence,
        int declaredCount,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads every slot of one run (P4, served by the <c>producer_slot_run</c> index). The read-only
    /// query 05's reconciler uses; never exposed on <c>/v1</c>.
    /// </summary>
    /// <param name="runId">The run whose slots are read.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    ValueTask<IReadOnlyList<ProducerSlotView>> GetRunProducerSlotsAsync(
        Guid runId,
        CancellationToken cancellationToken);
}

/// <summary>
/// The bounds the slot table and the registry share, so a value the database CHECK would refuse never
/// reaches a statement (00 §5.5 C6). Each constant names the source it mirrors: the migration's CHECK
/// (<c>migrations/0029_ingestion_producer_slot.sql</c>), the manifest schema
/// (<c>contracts/manifest/manifest.v1.json</c>) or the envelope schema
/// (<c>contracts/schemas/event-envelope.v1.json</c>).
/// </summary>
public static class ProducerSlotLimits
{
    /// <summary>The manifest's <c>shard_plan.shards.maxItems</c>: at most 256 shards, so at most 256 slots per run.</summary>
    public const int MaxShards = 256;

    /// <summary>
    /// The longest <c>producer_id</c> the envelope schema allows
    /// (<c>contracts/schemas/event-envelope.v1.json</c> <c>producer_id.maxLength</c>), which the
    /// migration's <c>producer_slot_producer_id_check</c> mirrors.
    /// </summary>
    public const int MaxProducerIdLength = 128;

    /// <summary>
    /// The charset of <c>producer_id</c>: the manifest <c>shard_id</c> grammar
    /// (<c>^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$</c>) widened to the envelope's 128-character limit,
    /// exactly as the migration's <c>producer_slot_producer_id_check</c> writes it.
    /// </summary>
    public const string ProducerIdPattern = "^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$";

    /// <summary>The historical bound_token_jti column allows at most 256 characters.</summary>
    public const int MaxOwnerIdLength = 256;
}

/// <summary>One registered slot, as <see cref="IProducerRegistry.RegisterSlotsAsync"/> returns it.</summary>
/// <param name="Id">The slot row's UUIDv7 id.</param>
/// <param name="ShardIndex">The 1-based manifest shard index.</param>
/// <param name="ProducerId">The deterministic producer id, <c>shard-&lt;index&gt;</c> (T54).</param>
public sealed record ProducerSlotRef(Guid Id, int ShardIndex, string ProducerId);

/// <summary>The outcome of <see cref="IProducerRegistry.BindOrCheckAsync"/>.</summary>
/// <param name="Id">The slot row's id.</param>
/// <param name="ShardIndex">The 1-based manifest shard index.</param>
/// <param name="ProducerId">The deterministic producer id (T54).</param>
/// <param name="Bound">True when this call wrote the binding; false when the slot was already bound to the same identity.</param>
public sealed record ProducerSlotBinding(Guid Id, int ShardIndex, string ProducerId, bool Bound);

/// <summary>The outcome of <see cref="IProducerRegistry.CloseSlotAsync"/> (T68, AC9).</summary>
/// <param name="Id">The slot row's id.</param>
/// <param name="ShardIndex">The 1-based manifest shard index.</param>
/// <param name="ProducerId">The deterministic producer id (T54).</param>
/// <param name="ClosedAt">When the slot was closed; the value already stored on an idempotent repeat.</param>
/// <param name="DeclaredLastSequence">The stored declared high-water mark (T68); the first close's value on a repeat.</param>
/// <param name="DeclaredCount">The stored declared count (T68); the first close's value on a repeat.</param>
/// <param name="Closed">True when this call wrote the close; false when the slot was already closed (the idempotent no-op).</param>
public sealed record ProducerSlotClosure(
    Guid Id,
    int ShardIndex,
    string ProducerId,
    DateTimeOffset ClosedAt,
    long DeclaredLastSequence,
    int DeclaredCount,
    bool Closed);

/// <summary>One slot of a run, as <see cref="IProducerRegistry.GetRunProducerSlotsAsync"/> returns it.</summary>
/// <param name="Id">The slot row's id.</param>
/// <param name="ShardIndex">The 1-based manifest shard index.</param>
/// <param name="ProducerId">The deterministic producer id (T54).</param>
/// <param name="Bound">True when a first accepted write has bound the slot (T9).</param>
/// <param name="ClosedAt">When <c>CloseSlotAsync</c> closed the slot (T68); null while open.</param>
/// <param name="LastSequence">The highest sequence the slot has stored; 0 before the first write.</param>
/// <param name="DeclaredLastSequence">The declared high-water mark <c>CloseSlotAsync</c> stored (T68); null while open.</param>
/// <param name="DeclaredCount">The declared count <c>CloseSlotAsync</c> stored (T68); null while open.</param>
public sealed record ProducerSlotView(
    Guid Id,
    int ShardIndex,
    string ProducerId,
    bool Bound,
    DateTimeOffset? ClosedAt,
    long LastSequence,
    long? DeclaredLastSequence,
    int? DeclaredCount);
