using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Ingestion.Contracts;

namespace Evidgate.Modules.Ingestion.Application;

/// <summary>
/// Errors of the producer registry (04.1.2-T4.2), the shape the 04.7.1-T2 capability store returns
/// (00 §5.5 C8): a value, not an exception, so the caller maps it straight to the 00 §9.2 envelope.
/// </summary>
internal static class ProducerSlotErrors
{
    /// <summary>422: the manifest declares more shards than the 256-shard bound, or fewer than one.</summary>
    internal static Error ShardCountOutOfRange => Error.Unprocessable(
        ApiErrorCode.RequestInvalid,
        $"shardCount must be between 1 and {ProducerSlotLimits.MaxShards}");

    /// <summary>409: the run already carries a different number of slots, so a second registration cannot be a no-op.</summary>
    internal static Error SlotsAlreadyRegistered => Error.Conflict(
        ApiErrorCode.ProducerSlotsConflict,
        "the run already has a different number of producer slots");

    /// <summary>422: no slot of the run carries this producer id (T9).</summary>
    internal static Error UnknownProducer => Error.Unprocessable(
        ApiErrorCode.UnknownProducer,
        "no producer slot of this run carries that producer id");

    /// <summary>
    /// 422: an argument is outside the column bounds the migration's CHECKs enforce, so it is refused
    /// before any SQL instead of surfacing as an unhandled 23514.
    /// </summary>
    internal static Error ArgumentOutOfRange => Error.Unprocessable(
        ApiErrorCode.RequestInvalid,
        $"producerId must match {ProducerSlotLimits.ProducerIdPattern} and ownerId 1..{ProducerSlotLimits.MaxOwnerIdLength}");

    /// <summary>403: the slot is bound to another authenticated owner (T9).</summary>
    internal static Error SlotTaken => Error.Denied(
        ApiErrorCode.ProducerSlotTaken,
        "the producer slot is bound to another token identity");

    /// <summary>422: a declared value <c>CloseSlotAsync</c> would store is outside its column bound (T68).</summary>
    internal static Error DeclaredOutOfRange => Error.Unprocessable(
        ApiErrorCode.RequestInvalid,
        "declaredLastSequence and declaredCount must be non-negative");
}
