using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Idempotency;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Mediator;

namespace Evidgate.BuildingBlocks.Pipeline.Behaviours;

/// <summary>
/// C2 stage 5 (00 §5.5 C2, idempotency): for a command carrying
/// <see cref="IIdempotentCommand"/>, claims the <c>Idempotency-Key</c> inside the
/// already-open tenant scope via <see cref="IIdempotencyStore"/> — the record is
/// tenant-scoped and commits or rolls back with the command (01.5.1-T6). A
/// completed same-payload key replays its stored response verbatim; a different
/// payload is 409 <c>idempotency_conflict</c> (00 §9.1/§9.2). A failure Result is
/// never completed: the begin row rolls back with the transaction, so the key may
/// legitimately be retried after the failure is fixed.
/// <para>
/// The request hash is over the RFC 8785 canonicalization of the serialized
/// command (<c>CanonicalHash</c> inside the store, 01.3.2-T4), so member order and
/// whitespace are invisible to dedup. The serialized command uses the declared
/// <typeparamref name="TMessage"/> shape — deterministic per command type.
/// </para>
/// </summary>
public sealed class IdempotencyBehaviour<TMessage, TResponse>(IIdempotencyStore store)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    /// <summary>00 §7 / the <c>idempotency_record</c> key column: a longer key is a client error, not a 22001.</summary>
    private const int MaxKeyLength = 128;

    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        if (message is not IIdempotentCommand idempotent)
        {
            return await next(message, cancellationToken).ConfigureAwait(false);
        }

        // Same contract guard the tenant/authorization stages carry: a DTO-answering
        // command cannot express the 409/400 Result this behaviour produces, so it
        // passes through rather than surfacing TenantScopeMissingException or an
        // InvalidOperationException from ResultFactory (QA-P defect D2).
        if (!ResultFactory.IsResultContract(typeof(TResponse)))
        {
            return await next(message, cancellationToken).ConfigureAwait(false);
        }

        var key = idempotent.IdempotencyKey;

        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyLength)
        {
            return ResultFactory.Failure<TResponse>(Error.BadRequest(
                $"Idempotency-Key must be 1..{MaxKeyLength} characters",
                [new ErrorDetail("Idempotency-Key", string.IsNullOrWhiteSpace(key) ? "required" : "too_long")]));
        }

        var operation = idempotent.Operation;
        var requestJson = SerializeCommand(message);

        var begin = await store.TryBeginAsync(operation, key, requestJson, cancellationToken).ConfigureAwait(false);

        switch (begin.Outcome)
        {
            case IdempotencyOutcome.Proceed:
                {
                    var response = await next(message, cancellationToken).ConfigureAwait(false);

                    if (!ResultFactory.TryGetFailure(response, out _))
                    {
                        var (status, body) = ResultFactory.Describe(response);
                        await store.CompleteAsync(begin.RecordId!.Value, status, body, cancellationToken).ConfigureAwait(false);
                    }

                    return response;
                }

            case IdempotencyOutcome.Replay:
                {
                    var replay = await store.ReplayAsync(operation, key, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException(
                            $"idempotency key '{key}' was reported as replayed by the store, yet no completed record exists");

                    return ResultFactory.Revive<TResponse>(replay.ResponseStatus, replay.ResponseBody);
                }

            default:
                return ResultFactory.Failure<TResponse>(Error.Conflict(
                    ApiErrorCode.IdempotencyConflict,
                    "the Idempotency-Key was already used for a different request"));
        }
    }

    /// <summary>
    /// The command as the store hashes it: the declared <typeparamref name="TMessage"/>
    /// shape, so the document is deterministic per command type (RFC 8785
    /// canonicalization inside the store then makes member order and whitespace
    /// irrelevant to dedup). Kept out of <c>Handle</c> because a serializer call is
    /// a blocking API (VSTHRD103) with an async alternative that would only add a stream.
    /// </summary>
    private static string SerializeCommand(TMessage message) => JsonSerializer.Serialize(message);
}
