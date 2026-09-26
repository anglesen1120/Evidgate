namespace Evidgate.BuildingBlocks.Persistence.Idempotency;

/// <summary>
/// The arbiter of <c>Idempotency-Key</c> on mutating endpoints (00 §9.1, TL T11):
/// <c>idempotency_record</c> is the only place a client key is remembered, and no
/// business table carries a permanent unique constraint on one. The C2 idempotency
/// behaviour (00 §5.5 C2) drives it inside the command's tenant scope, after
/// authorization and before the handler:
/// <see cref="TryBeginAsync"/> → handler → <see cref="CompleteAsync"/>, with
/// <see cref="ReplayAsync"/> answering a retry from the stored response.
/// <para>
/// All three members demand an active tenant scope and run on its connection and
/// transaction, so a record is only ever written for the tenant the command runs
/// as, and it commits or rolls back with the command itself.
/// </para>
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Claims the key for this command, or reports that it already exists.
    /// </summary>
    /// <param name="operation">The operation the key belongs to, e.g. <c>runs.create</c>.</param>
    /// <param name="key">The client-supplied key, at most 128 characters (00 §7).</param>
    /// <param name="requestJson">
    /// The request document. Its RFC 8785 canonical digest
    /// (<c>CanonicalHash</c>, 01.3.2-T4) is the stored <c>request_hash</c>, so
    /// member order and whitespace do not change the identity of a request.
    /// </param>
    /// <param name="cancellationToken">Cancels the statements.</param>
    /// <returns>The decision; <see cref="IdempotencyOutcome.Proceed"/> carries the new record id.</returns>
    /// <exception cref="Evidgate.BuildingBlocks.Canonicalization.JsonCanonicalizationException">
    /// <paramref name="requestJson"/> is not a canonicalizable JSON document. The
    /// edge already rejected that request (00 §5.5, JSON Schema before binding), so
    /// reaching this is a bug, not a client error.
    /// </exception>
    Task<IdempotencyBeginResult> TryBeginAsync(
        string operation,
        string key,
        string requestJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the response of the command that owns <paramref name="recordId"/>.
    /// Together with the begin insert it is one transaction, so a record never
    /// becomes visible to another request before its response exists.
    /// </summary>
    /// <param name="recordId">The id <see cref="TryBeginAsync"/> returned for <see cref="IdempotencyOutcome.Proceed"/>.</param>
    /// <param name="status">The HTTP status the command returned.</param>
    /// <param name="responseJson">The JSON body the command returned.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    Task CompleteAsync(
        Guid recordId,
        int status,
        string responseJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the stored response of a completed record (the plan's <c>Replay</c>),
    /// for a caller that was just told <see cref="IdempotencyOutcome.Replay"/>.
    /// </summary>
    /// <param name="operation">The operation the key belongs to.</param>
    /// <param name="key">The client-supplied key.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns>The stored status and body, or null when there is no completed record.</returns>
    Task<IdempotencyReplay?> ReplayAsync(
        string operation,
        string key,
        CancellationToken cancellationToken = default);
}
