using Evidgate.BuildingBlocks.SharedKernel.Serialization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Orchestrator.Application.Manifests;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Evidgate.Modules.Policy.Contracts;
using Mediator;
using Npgsql;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// Create a run from a verified manifest (<c>POST /v1/projects/{p}/runs</c>, 05.2.1 §5). The raw body travels unparsed
/// (like <see cref="Manifests.UploadManifestCommand"/>): the handler parses and validates it after the C2 pipeline
/// authorized the caller, so an anonymous or unauthorized request costs no parse.
/// <para>
/// Besides <c>manifest_id</c>, <c>components</c> and an optional <c>release_id</c>, the body may carry the legacy
/// <c>release</c> name — permanently unresolvable, because a run binds to a release by id — plus <c>purpose</c>
/// (<c>regular</c>, the default, or <c>replay</c>) and <c>replay_grant_id</c> (05.2.1-T5): a <c>replay</c> request is
/// refused 422 before any read, and a <c>replay_grant_id</c> is only meaningful for one.
/// </para>
/// <para>
/// The body may also carry <c>ref</c> (05.2.2-T1.3): <c>{kind, name, pr_number, head_sha, base_sha, trigger_event,
/// is_fork}</c>, the identity a run records when no verified GitHub OIDC claim covers it. The block is always
/// shape-checked before any write (AC4), and where a verified claim covers a member the body's value is ignored rather
/// than refused (AC3).
/// </para>
/// <para>
/// Idempotent under the client's <c>Idempotency-Key</c> (00 §9.1, 05.2.1-T2): the C2 idempotency stage claims the key
/// inside the run's tenant transaction, so the <c>idempotency_record</c>, the run, its planned executions, its producer
/// slots and its outbox row commit or roll back together. The request identity is the RFC 8785 digest of the body
/// (<see cref="BodyHash"/>), so member order and whitespace are invisible to dedup: a retry with the same key and the
/// same canonical body replays the original response, a different body is 409 <c>idempotency_conflict</c>.
/// </para>
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Body">The raw UTF-8 body, at most 64 KiB — the run-create transport cap the endpoint counts.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
/// <param name="CiRunBinding">
/// The caller's verified CI run reference in the <see cref="CiRunReference.ToBindingReference"/> form
/// (<c>{repository_id}/{run_id}/{run_attempt}</c>, T88), or null for a caller whose token carries none (a member, an
/// ingest-token exchange). 05.2.2-T1.4 folds it into <see cref="BodyHash"/>: the identity a run records now depends on
/// it, so the same key with the same body but a different CI run attempt must conflict instead of replaying. Never
/// serialized — the request document the store hashes is the body digest, exactly as before (00 §9.1).
/// </param>
internal sealed record CreateRunCommand(
    Guid ProjectId,
    [property: JsonIgnore] ReadOnlyMemory<byte> Body,
    string IdempotencyKey,
    [property: JsonIgnore] string? CiRunBinding = null)
    : ICommand<Result<RunCreated>>, IIdempotentCommand
{
    /// <summary>The 00 §7 operation the key is claimed for (05.2.1-T2.1): stable across a rename.</summary>
    string IIdempotentCommand.Operation => "run:create";

    /// <summary>
    /// The request identity the idempotency store hashes, computed from <see cref="Body"/> on first serialization
    /// (inside the C2 idempotency stage, never at the HTTP edge): the RFC 8785 digest of the body
    /// (<see cref="CanonicalHash"/>), so member order and whitespace differences replay rather than conflict. A body
    /// that cannot be canonicalized — invalid JSON, a duplicate member name, a lone surrogate — falls back to
    /// <c>raw-sha256:&lt;hex&gt;</c> of the raw bytes, so the request still reaches the handler, which answers its normal
    /// 400 <c>request_invalid</c> (a failure Result is never stored).
    /// <para>
    /// 05.2.2-T1.4: a caller with a verified CI run reference (<see cref="CiRunBinding"/>) folds it in, so the same
    /// key and body from a different CI run attempt is 409 <c>idempotency_conflict</c> rather than a replay, while a
    /// re-minted token for the same attempt still replays. The fold is additive: a request without that reference
    /// hashes exactly as before this story, so no stored record mass-conflicts on deploy (00 §9.1).
    /// </para>
    /// </summary>
    public string BodyHash => CiRunBinding is { Length: > 0 } binding
        ? Combine(ComputeBodyHash(Body), binding)
        : ComputeBodyHash(Body);

    /// <summary>
    /// The digest of a request that carries a verified CI run reference: the RFC 8785 digest of one two-member document
    /// holding the body's own digest and the reference, so both halves are covered by a single canonical hash (the
    /// reference is escaped as a JSON string, not concatenated).
    /// </summary>
    private static string Combine(string bodyHash, string binding) =>
        CanonicalHash.Compute(
            $$"""{"body_hash":{{JsonSerializer.Serialize(bodyHash)}},"ci_run_ref":{{JsonSerializer.Serialize(binding)}}}""");

    private static string ComputeBodyHash(ReadOnlyMemory<byte> body)
    {
        try
        {
            return CanonicalHash.Compute(body.Span);
        }
        catch (JsonCanonicalizationException)
        {
            return RawSha256(body);
        }
        catch (JsonException)
        {
            return RawSha256(body);
        }
    }

    private static string RawSha256(ReadOnlyMemory<byte> body) =>
        "raw-sha256:" + Convert.ToHexStringLower(SHA256.HashData(body.Span));
}
