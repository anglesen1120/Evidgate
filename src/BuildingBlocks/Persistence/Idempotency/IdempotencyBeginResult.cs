namespace Evidgate.BuildingBlocks.Persistence.Idempotency;

/// <summary>
/// The result of beginning a command under an <c>Idempotency-Key</c>.
/// </summary>
/// <param name="Outcome">What the caller must do next.</param>
/// <param name="RecordId">
/// The id of the record this command owns — set only for
/// <see cref="IdempotencyOutcome.Proceed"/>, because that id is what
/// <see cref="IIdempotencyStore.CompleteAsync"/> stamps with the response. On
/// <see cref="IdempotencyOutcome.Replay"/> and
/// <see cref="IdempotencyOutcome.Conflict"/> the record belongs to the request
/// that got there first, so the caller has no business writing to it (null).
/// </param>
public sealed record IdempotencyBeginResult(IdempotencyOutcome Outcome, Guid? RecordId);
