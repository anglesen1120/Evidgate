using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// Finalize a producer shard (<c>POST /v1/runs/{r}/finalize</c>, 05.3.2 §5): the shard declares it has sent
/// everything, the platform marks its slot closed and starts reconciling instead of waiting for the collection
/// deadline.
/// <para>
/// The raw body travels unparsed, like <see cref="CreateRunCommand"/>: the handler parses and validates it after the
/// C2 pipeline authorized the caller, so an anonymous or unauthorized request costs no parse. The response is
/// bodiless <c>202</c>: reconciliation is asynchronous, and the plan §5 pins no body for it.
/// </para>
/// <para>
/// An <see cref="IIdempotentCommand"/> as 00 §9.1 / <c>contracts/README.md</c> require of every mutation that is not
/// on the exemption list: the <c>Idempotency-Key</c> the contract declares is <b>required</b> and is claimed by the C2
/// idempotency stage in the same tenant transaction, so a missing, blank or over-128-character key is 400
/// <c>request_invalid</c> there, before the body is parsed. A retry of the same request replays the stored <c>202</c>,
/// and a reused key with a different run, body or caller identity is 409 <c>idempotency_conflict</c>
/// (<see cref="BodyHash"/> covers the body and the caller's identity, both <see cref="JsonIgnoreAttribute"/>-ed out of
/// the serialized command). The key is <b>not</b> the arbiter of a duplicate finalize, though:
/// <c>producer_slot.closed_at</c> is (D3), set once by <see cref="IProducerRegistry.CloseSlotAsync"/>, so a repeat
/// with a <b>fresh</b> key is the same no-op <c>202</c>.
/// </para>
/// </summary>
/// <param name="RunId">The run of the route's <c>{r}</c> segment.</param>
/// <param name="Body">
/// The raw UTF-8 request body (<c>producer_id</c>, <c>shard</c>, <c>last_sequence</c>, <c>count</c>,
/// <c>manifest_hash</c>), at most the route's transport cap.
/// </param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1, at most 128 characters).</param>
/// <param name="CredentialId">
/// The caller's stable credential id, folded into <see cref="BodyHash"/> and never serialized: the replay is
/// bound to the credential that made the first request (T9), and survives a token renewal.
/// </param>
internal sealed record FinalizeShardCommand(
    Guid RunId,
    [property: JsonIgnore] ReadOnlyMemory<byte> Body,
    string IdempotencyKey,
    [property: JsonIgnore] string? CredentialId = null) : ICommand<Result>, IIdempotentCommand
{
    /// <summary>The 00 §7 operation the key is claimed for: stable across a rename.</summary>
    string IIdempotentCommand.Operation => "run:finalize";

    /// <summary>
    /// The request identity the idempotency store hashes, next to the serialized <see cref="RunId"/>: the raw
    /// SHA-256 of the body bytes. The body is excluded from the serialized command (it is a
    /// <see cref="ReadOnlyMemory{T}"/>), so without this member two different declarations under one key would
    /// replay each other instead of conflicting. Raw bytes on purpose — a byte-identical retry from the same client
    /// is the case that must replay, and no canonical form is needed for that.
    /// <para>
    /// The caller's identity is folded in beside it: the same key and body replayed by a different credential
    /// is 409 <c>idempotency_conflict</c> instead of taking the stored <c>202</c> — a replay must never stand in
    /// for the T9 slot-identity check of another caller. The id is hashed before it enters the hashed
    /// document, so it is never stored or logged.
    /// </para>
    /// </summary>
    public string BodyHash => Combine(
        "raw-sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Body.Span)),
        CredentialId);

    /// <summary>
    /// The digest of one request that carries a caller identity: the RFC 8785 digest of a two-member document
    /// holding the body's own digest and the digest of the credential id, so no raw identity value is ever part
    /// of a stored record.
    /// </summary>
    private static string Combine(string bodyHash, string? credentialId) =>
        CanonicalHash.Compute(
            $$"""{"body_hash":{{JsonSerializer.Serialize(bodyHash)}},"credential_sha256":{{JsonSerializer.Serialize(Digest(credentialId))}}}""");

    /// <summary>The SHA-256 of an identity value, so only a digest of it reaches the hashed document.</summary>
    private static string Digest(string? value) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty)));
}
