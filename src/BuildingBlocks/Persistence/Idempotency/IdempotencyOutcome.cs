namespace Evidgate.BuildingBlocks.Persistence.Idempotency;

/// <summary>
/// The decision <see cref="IIdempotencyStore.TryBeginAsync"/> hands back to the C2
/// idempotency behaviour (00 §5.5 C2, 01.5.1-T6.1).
/// </summary>
public enum IdempotencyOutcome
{
    /// <summary>
    /// No record existed for the key: this command owns it and must run, then
    /// store its response with <see cref="IIdempotencyStore.CompleteAsync"/>.
    /// </summary>
    Proceed,

    /// <summary>
    /// The same key with the same canonical request hash already completed: the
    /// original response is replayed (00 §9.1) instead of executing again.
    /// </summary>
    Replay,

    /// <summary>
    /// The same key was used for a different request — 409
    /// <c>idempotency_conflict</c> (00 §9.1/§9.2).
    /// </summary>
    Conflict,
}
