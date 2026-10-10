using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Ingestion.Contracts;

/// <summary>
/// The run's producer registry (00 §9.4, TL T9): one <c>producer_slot</c> row per manifest shard,
/// written by 05's run-create command inside its own tenant transaction, and bound to the caller's
/// token identity by the first accepted write of any kind (an event batch or 06.1.2's
/// <c>upload-url</c>, T85). Every cross-module call goes through this project only (00 §5.5 C5).
/// <para>
/// The contract is the 04.1.2-T4 half of the story: it registers the slots and answers the binding
/// question. The enforcement that turns a <c>producer_slot_taken</c>/<c>unknown_producer</c> answer
/// into a rejected batch is 04.1.2-T5's; <c>CloseSlotAsync</c> (T68) and the
/// <c>declared_last_sequence</c>/<c>declared_count</c> columns are T5's as well.
/// </para>
/// </summary>
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
    /// <returns>The registered slots, or a failure when the count is out of range or the run already has slots.</returns>
    ValueTask<Result<IReadOnlyList<ProducerSlotRef>>> RegisterSlotsAsync(
        Guid runId,
        int shardCount,
        CancellationToken cancellationToken);

    /// <summary>
    /// Binds the slot of <paramref name="producerId"/> to the caller's token identity on the first
    /// accepted write, or checks that the caller is the identity already bound (T9). The bind is one
    /// conditional <c>UPDATE … WHERE bound_token_jti IS NULL</c>, so two racing first writers cannot
    /// both win.
    /// </summary>
    /// <param name="runId">The run the slot belongs to.</param>
    /// <param name="producerId">The manifest shard's deterministic producer id (T54).</param>
    /// <param name="tokenJti">The caller's token id (an id, never the token itself; S3).</param>
    /// <param name="ciRunRef">
    /// The GitHub <c>(repository_id, run_id, run_attempt)</c> reference the caller's claims carry, or
    /// null for a non-CI caller. Its comparison with <c>run.ci_run_ref</c> (T88) is 04.1.2-T5's: the
    /// <c>run</c> table does not exist yet (05.2.1-T1 creates it), so T4 only binds and compares the
    /// value against the slot's own binding.
    /// <para>
    /// The comparison is exact, so a retry that adds a CI reference to a slot first bound without one
    /// (or drops it) is a <em>different identity</em> and answers <c>producer_slot_taken</c>. That is
    /// the safe default: a different CI-run identity is a different identity, and a caller that
    /// changes its CI reference mid-run must not silently inherit the slot.
    /// </para>
    /// </param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>
    /// The slot's binding, or a failure: <c>request_invalid</c> (422) when an argument is outside the
    /// column bounds (an empty or over-long <c>tokenJti</c>/<c>ciRunRef</c>, or a <c>producerId</c>
    /// outside the 1..128 charset), <c>unknown_producer</c> (422) when the run has no such slot,
    /// <c>producer_slot_taken</c> (403) when the slot is bound to another jti or CI reference.
    /// </returns>
    ValueTask<Result<ProducerSlotBinding>> BindOrCheckAsync(
        Guid runId,
        string producerId,
        string tokenJti,
        string? ciRunRef,
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

    /// <summary>
    /// The longest <c>bound_token_jti</c> the migration's <c>producer_slot_bound_token_jti_check</c>
    /// allows (1..256, mirroring 0017's <c>jti</c>).
    /// </summary>
    public const int MaxTokenJtiLength = 256;

    /// <summary>
    /// The longest <c>bound_ci_run_ref</c> the migration's <c>producer_slot_bound_ci_run_ref_check</c>
    /// allows (1..256): the GitHub <c>(repository_id, run_id, run_attempt)</c> triple (T88).
    /// </summary>
    public const int MaxCiRunRefLength = 256;
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

/// <summary>One slot of a run, as <see cref="IProducerRegistry.GetRunProducerSlotsAsync"/> returns it.</summary>
/// <param name="Id">The slot row's id.</param>
/// <param name="ShardIndex">The 1-based manifest shard index.</param>
/// <param name="ProducerId">The deterministic producer id (T54).</param>
/// <param name="Bound">True when a first accepted write has bound the slot (T9).</param>
/// <param name="ClosedAt">When <c>CloseSlotAsync</c> closed the slot (T68); null while open.</param>
/// <param name="LastSequence">The highest sequence the slot has stored; 0 before the first write.</param>
public sealed record ProducerSlotView(
    Guid Id,
    int ShardIndex,
    string ProducerId,
    bool Bound,
    DateTimeOffset? ClosedAt,
    long LastSequence);
