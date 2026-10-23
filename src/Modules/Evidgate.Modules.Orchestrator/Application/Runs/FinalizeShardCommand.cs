using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Http;
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
/// The caller's credential id (<c>jti</c>), folded into <see cref="BodyHash"/> and never serialized: the replay is
/// bound to the credential that made the first request (T9).
/// </param>
/// <param name="CiRunBinding">
/// The caller's CI run reference in the <see cref="CiRunReference.ToBindingReference"/> form (T88), or null when the
/// token carries none. Folded into <see cref="BodyHash"/> beside the credential, so the same key and body from
/// another CI run attempt conflicts instead of replaying (the <see cref="CreateRunCommand"/> precedent).
/// </param>
internal sealed record FinalizeShardCommand(
    Guid RunId,
    [property: JsonIgnore] ReadOnlyMemory<byte> Body,
    string IdempotencyKey,
    [property: JsonIgnore] string? CredentialId = null,
    [property: JsonIgnore] string? CiRunBinding = null) : ICommand<Result>, IIdempotentCommand
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
    /// The caller's identity is folded in beside it, exactly as <see cref="CreateRunCommand"/> folds its
    /// <c>CiRunBinding</c>: the same key and body replayed by a different credential, or by the same credential under
    /// another CI run attempt, is 409 <c>idempotency_conflict</c> instead of taking the stored <c>202</c> — a replay
    /// must never stand in for the T9 slot-identity check of another caller. Both halves are hashed before they enter
    /// the hashed document, so neither the credential id nor the CI run reference is ever stored or logged.
    /// </para>
    /// </summary>
    public string BodyHash => Combine(
        "raw-sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Body.Span)),
        CredentialId,
        CiRunBinding);

    /// <summary>
    /// The digest of one request that carries a caller identity: the RFC 8785 digest of a three-member document
    /// holding the body's own digest and the digests of the credential id and the CI run reference, so every half is
    /// covered by a single canonical hash and no raw identity value is ever part of a stored record.
    /// </summary>
    private static string Combine(string bodyHash, string? credentialId, string? ciRunBinding) =>
        CanonicalHash.Compute(
            $$"""{"body_hash":{{JsonSerializer.Serialize(bodyHash)}},"credential_sha256":{{JsonSerializer.Serialize(Digest(credentialId))}},"ci_run_ref_sha256":{{JsonSerializer.Serialize(Digest(ciRunBinding))}}}""");

    /// <summary>The SHA-256 of an identity value, so only a digest of it reaches the hashed document.</summary>
    private static string Digest(string? value) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty)));
}

/// <summary>
/// Handler of <see cref="FinalizeShardCommand"/> (05.3.2-T1 §5, AC1–AC3/AC6/AC7), inside the C2 pipeline's one
/// tenant transaction: nothing it writes commits unless the whole request succeeds, so a refusal leaves the slot
/// open, the run's state unchanged and no outbox row.
/// <para>
/// <b>Order of decisions.</b> The caller's credential is checked first (401, exactly like the events route), then the
/// body is validated against the contract (422 <c>request_invalid</c>, before any statement). The run is read
/// unlocked and then locked: <c>FOR NO KEY UPDATE</c> while it is <c>queued</c> or <c>running</c>, because a finalize
/// may transition such a run (see <see cref="OrchestratorSql.LockRunForFinalize"/>: the registry locks run before
/// slot), and <c>FOR SHARE</c> in every other state, which never transitions again
/// (<see cref="OrchestratorSql.ShareLockRunForFinalize"/>). Both answer existence (404), the credential's project
/// scope (403 <c>project_scope_mismatch</c>) and the project's lifecycle state (409 <c>project_deleting</c>, 403
/// <c>org_suspended</c>) — the same codes the events handler answers. The shard's identity is settled next, through
/// <see cref="IProducerRegistry.BindOrCheckAsync"/>: only the identity bound to that slot may finalize it (T9), which
/// is why a foreign caller is 403 <c>producer_slot_taken</c> and an unregistered producer 422 <c>unknown_producer</c>
/// <i>before</i> the shard number or the manifest hash is compared — a foreign caller learns nothing about either.
/// </para>
/// <para>
/// <b>Idempotency (AC6/D3).</b> <see cref="IProducerRegistry.CloseSlotAsync"/> is idempotent on
/// <c>producer_slot.closed_at</c> and answers the <b>stored</b> declaration on a repeat. A repeat writes no state,
/// enqueues no second reconcile row and applies no transition, so one finalize produces exactly one outbox row; a
/// repeat whose declared values differ from the stored pair is still <c>202</c> (the first declaration wins, the
/// stored pair stays authoritative for 05.3.2-T2 — [DECISION] Q1) and is counted and logged once.
/// </para>
/// <para>
/// <b>Accept-and-mark (T12).</b> A finalize on a <c>collecting</c>, terminal or <c>canceled</c> run still closes its
/// slot and answers <c>202</c>; nothing here completes a run, so a canceled run's finalize marks the shard and stops.
/// A finalize that arrives before any event of a still-<c>queued</c> run steps the state
/// <c>queued → running → collecting</c> (AC7): <c>queued → collecting</c> is an undeclared forward-skip in the 00 §8.1
/// machine, and the transition command refuses forward-skips rather than widening the table ([DECISION] Q4).
/// </para>
/// <para>
/// <b>Audit.</b> The applied transitions write their own canonical <c>run.state_changed</c> rows. This handler's own
/// entry (<see cref="FinalizeAuditAction"/>) is described <b>after</b> every transition returns, because the
/// transition shares the unit's single pending description slot; a duplicate finalize changes nothing, so it
/// suppresses its own entry (<see cref="IAuditTrail.Suppress"/> semantics: "an idempotent replay changed nothing").
/// </para>
/// </summary>
[AuditedAction(FinalizeShardCommandHandler.FinalizeAuditAction)]
[Permission(Permissions.Event.Write)]
internal sealed partial class FinalizeShardCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRunStateTransition runStates,
    IProducerRegistry producers,
    IOutboxWriter outbox,
    IAuditTrail audit,
    ILogger<FinalizeShardCommandHandler> logger) : ICommandHandler<FinalizeShardCommand, Result>
{
    /// <summary>The <c>audit_log.action</c> of an accepted finalize (00 §7).</summary>
    internal const string FinalizeAuditAction = "run.finalize";

    /// <summary>The 00 §9.2 reason of a member the body cannot hold (the same token every Orchestrator body rule uses).</summary>
    internal const string InvalidValue = "invalid_value";

    /// <summary>The closed member set of the finalize body (00 §9.3); any other member is 422 <c>additional_properties</c>.</summary>
    private static readonly string[] Members = ["producer_id", "shard", "last_sequence", "count", "manifest_hash"];

    /// <inheritdoc />
    public async ValueTask<Result> Handle(FinalizeShardCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Orchestrator, nameof(FinalizeShardCommand));

        // Fail closed: the slot's binding IS the caller's token identity (T9), so a principal with no credential id
        // (a member session) is nobody this route can serve. The same check and code the events route answers.
        var principal = principals.Current;
        if (principal is null || string.IsNullOrEmpty(principal.CredentialId))
        {
            return Result.Failure(UnauthenticatedProducer);
        }

        // (0) body: the member set and the five shapes. Nothing is read or written before this passes.
        var parsed = ParseBody(command.Body);
        if (!parsed.IsSuccess)
        {
            return Result.Failure(parsed.Error!);
        }

        var request = parsed.Value;
        var orgId = scopes.Require().OrgId;
        var ciRunRef = principal.CiRun?.ToBindingReference();

        // (1) The run. An unlocked, RLS-scoped read first, the events handler's own pattern (IngestEventBatch):
        // it answers existence (404) and the credential's project scope (403) and the project's lifecycle state
        // (409/403) without taking a row lock, so a refused finalize costs event ingest nothing.
        var observed = await db.QuerySingleOrDefaultAsync<FinalizeRunRow>(
            OrchestratorSql.ReadRunForFinalize,
            new { org_id = orgId, run_id = command.RunId },
            cancellationToken).ConfigureAwait(false);

        if (observed is null)
        {
            return Result.Failure(NoSuchRun);
        }

        if (ProjectRefusal(observed, principal) is { } refused)
        {
            return Result.Failure(refused);
        }

        // The lock is taken only for what this handler may do with it (lock order: run before slot, and no shared
        // lock is ever upgraded). A run that is queued or running can still transition, so it is taken
        // FOR NO KEY UPDATE; collecting, complete, incomplete and canceled never return to queued or running, so
        // they are taken FOR SHARE, which does not conflict with an event batch's own FOR SHARE or with another
        // shard's finalize (OrchestratorSql.LockRunForFinalize / ShareLockRunForFinalize). RLS makes another
        // tenant's run invisible either way.
        var run = await db.QuerySingleOrDefaultAsync<FinalizeRunRow>(
            MayTransition(observed.RunState)
                ? OrchestratorSql.LockRunForFinalize
                : OrchestratorSql.ShareLockRunForFinalize,
            new { org_id = orgId, run_id = command.RunId },
            cancellationToken).ConfigureAwait(false);

        if (run is null)
        {
            return Result.Failure(NoSuchRun);
        }

        // The locked row is authoritative: the state only moves forward, so it is still one this handler may
        // serve, and the two gates are re-decided against what is locked rather than what was observed.
        if (ProjectRefusal(run, principal) is { } lockedRefusal)
        {
            return Result.Failure(lockedRefusal);
        }

        // (2) the shard's identity (T9/T88): the slot binds to this token on a first write, and only the identity it
        // is bound to may go on. A failure is the registry's own 403/422 and leaves nothing behind (the transaction
        // rolls back), so a foreign caller learns neither the shard index nor the manifest hash.
        var bound = await producers
            .BindOrCheckAsync(command.RunId, request.ProducerId, principal.CredentialId, ciRunRef, cancellationToken)
            .ConfigureAwait(false);

        if (!bound.IsSuccess)
        {
            return CountedFailure(bound.Error!);
        }

        // (3) the declared shard must be the slot's own 1-based manifest shard (05.3.2-T1 [DECISION] Q2).
        if (request.Shard != bound.Value.ShardIndex)
        {
            return Result.Failure(Error.Invalid(
                "shard is not the producer slot's shard index",
                [new ErrorDetail("/shard", InvalidValue)]));
        }

        // (4) the manifest hash must be the run's own (AC3). Checked after the identity, so a foreign caller never
        // learns the expected hash — and the expected value is never echoed back, only the fact that they differ.
        if (!string.Equals(request.ManifestHash, run.ManifestHash, StringComparison.Ordinal))
        {
            return Result.Failure(Error.Unprocessable(
                ApiErrorCode.ManifestInvalid,
                "manifest_hash is not the hash of the run's manifest",
                [new ErrorDetail("/manifest_hash", InvalidValue)]));
        }

        // (5) close the slot once, storing the declaration (T68). A repeat answers the STORED pair with Closed false.
        var closed = await producers
            .CloseSlotAsync(
                command.RunId,
                request.ProducerId,
                principal.CredentialId,
                ciRunRef,
                request.LastSequence,
                request.Count,
                cancellationToken)
            .ConfigureAwait(false);

        if (!closed.IsSuccess)
        {
            return CountedFailure(closed.Error!);
        }

        var closure = closed.Value;
        if (!closure.Closed)
        {
            // AC6/D3: the slot was already closed, so this request changes nothing — no transition, no outbox row, no
            // second evidence revision, and no audit entry ("an idempotent replay changed nothing").
            if (closure.DeclaredLastSequence != request.LastSequence || closure.DeclaredCount != request.Count)
            {
                // The stored declaration stays authoritative for 05.3.2-T2 and only the fact of the disagreement is
                // recorded: a counter and one line naming the run, the shard and the two numbers this caller sent.
                EvidgateTelemetry.RecordFinalizeDeclarationMismatch();
                LogDeclarationMismatch(logger, command.RunId, request.ProducerId, request.LastSequence, request.Count);
            }

            audit.Suppress();

            return Result.Success(StatusCodes.Status202Accepted);
        }

        // (6a) Only the call that wrote the close moves the run: a finalize before the events of a queued run steps
        // queued -> running first (Q4), and a run that is collecting, terminal or canceled keeps its state (T12).
        if (string.Equals(run.RunState, RunStates.Queued, StringComparison.Ordinal))
        {
            var started = await runStates
                .TransitionAsync(new TransitionRunStateCommand(command.RunId, RunStates.Running), cancellationToken)
                .ConfigureAwait(false);

            if (!started.IsSuccess)
            {
                return Result.Failure(started.Error!);
            }
        }

        if (string.Equals(run.RunState, RunStates.Queued, StringComparison.Ordinal)
            || string.Equals(run.RunState, RunStates.Running, StringComparison.Ordinal))
        {
            var collecting = await runStates
                .TransitionAsync(new TransitionRunStateCommand(command.RunId, RunStates.Collecting), cancellationToken)
                .ConfigureAwait(false);

            if (!collecting.IsSuccess)
            {
                return Result.Failure(collecting.Error!);
            }
        }

        // (6b) One reconcile trigger per newly closed slot (Q3), in this same tenant transaction: the row is invisible
        // to the dispatcher until this commits, and disappears with it if the scope rolls back.
        await outbox.EnqueueAsync(
            RunReconcileContract.OutboxTaskType,
            RunReconcileContract.PayloadJson(
                command.RunId, closure.ProducerId, closure.DeclaredLastSequence, closure.DeclaredCount),
            RunReconcileContract.AggregateType,
            command.RunId,
            RunReconcileContract.IdempotencyKey(command.RunId, closure.ProducerId),
            cancellationToken).ConfigureAwait(false);

        // (7) Our own audit description goes AFTER the transitions: they share the unit's single pending slot and
        // their Record consumes it, so a Describe made earlier would be swallowed.
        audit.Describe(new AuditObject("run", command.RunId.ToString("D")));

        activity?.SetTag("evidgate.run_id", command.RunId.ToString("D"));
        activity?.SetTag("evidgate.producer_id", request.ProducerId);

        return Result.Success(StatusCodes.Status202Accepted);
    }

    /// <summary>401: no resolved credential, so nobody can be the slot's bound identity (the events route's answer).</summary>
    private static Error UnauthenticatedProducer => new(
        ApiErrorCode.Unauthenticated,
        "no authenticated producer could be resolved",
        StatusCodes.Status401Unauthorized);

    /// <summary>404 for an unknown, another tenant's or another project's run: RLS answers all three the same way.</summary>
    private static Error NoSuchRun => Error.NotFound("no run with that id is visible to this tenant");

    /// <summary>
    /// True when the run may still transition: only <c>queued</c> and <c>running</c> can reach <c>collecting</c>, and
    /// no state returns to either of them (00 §8.1), so every other state needs no exclusive lock.
    /// </summary>
    private static bool MayTransition(string runState) =>
        string.Equals(runState, RunStates.Queued, StringComparison.Ordinal)
        || string.Equals(runState, RunStates.Running, StringComparison.Ordinal);

    /// <summary>
    /// The credential and project gates a finalize answers next: the token's project is the boundary of a
    /// <c>ci_producer</c> identity (FR-02-7, 00 §9.1), and the project's own lifecycle state mirrors the events
    /// handler's decision (suspended refuses writes, deleted is the conflict). Null when the run may go on.
    /// </summary>
    private static Error? ProjectRefusal(FinalizeRunRow run, IRequestPrincipal principal)
    {
        if (principal.ProjectScope is { } tokenProject && tokenProject != run.ProjectId)
        {
            return Error.Denied(
                ApiErrorCode.ProjectScopeMismatch,
                "the run belongs to a project outside the credential's scope");
        }

        if (string.Equals(run.ProjectState, "deleted", StringComparison.Ordinal))
        {
            return Error.Conflict(ApiErrorCode.ProjectDeleting, "the project is being deleted");
        }

        if (string.Equals(run.ProjectState, "suspended", StringComparison.Ordinal))
        {
            return Error.Denied(ApiErrorCode.OrgSuspended, "the project is suspended");
        }

        return null;
    }

    /// <summary>
    /// The registry's failure, counted when it is the T9 abuse signal (a slot bound to another identity) and returned
    /// unchanged otherwise. The close repeats the same identity check, so both calls feed the one counter.
    /// </summary>
    private static Result CountedFailure(Error error)
    {
        if (string.Equals(error.Code, ApiErrorCode.ProducerSlotTaken, StringComparison.Ordinal))
        {
            EvidgateTelemetry.RecordFinalizeProducerSlotTaken();
        }

        return Result.Failure(error);
    }

    // A repeated member name would make a declared value ambiguous, so it is refused with the rest of the invalid shapes.
    private static readonly JsonDocumentOptions DuplicateFreeOptions = new() { AllowDuplicateProperties = false };

    /// <summary>
    /// The body's member set and the five shapes, in the order the contract lists them (00 §9.3, 05.3.2 §5). A
    /// member outside <see cref="Members"/> is 422 <c>additional_properties</c>, a missing or mistyped one 422
    /// <c>request_invalid</c> naming the pointer — exactly the shape the run-create body parse gives.
    /// </summary>
    private static Result<FinalizeShardRequest> ParseBody(ReadOnlyMemory<byte> body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, DuplicateFreeOptions);
        }
        catch (JsonException)
        {
            return BodyInvalid("the request body is not JSON or repeats a member name", [new ErrorDetail(string.Empty, ApiErrorCode.RequestInvalid)]);
        }
        catch (InvalidOperationException)
        {
            // The duplicate check reads member names to compare them, and transcoding a lone-surrogate escape throws
            // instead of returning a JsonException. The body is unreadable either way, so it is the same 422.
            return BodyInvalid("the request body carries text that cannot be stored", [new ErrorDetail(string.Empty, InvalidValue)]);
        }

        using var parsed = document;
        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return BodyInvalid("the request body must be a JSON object", [new ErrorDetail(string.Empty, ApiErrorCode.RequestInvalid)]);
        }

        // Before a single member is named: reading member.Name on a lone-surrogate name throws (JsonBody's own
        // remark), so the body is refused 422 here rather than escaping as a 500 (the ActivatePolicy precedent).
        if (JsonBody.HasUnstorableText(root))
        {
            // A lone surrogate or a NUL: neither the canonical form nor jsonb can carry it.
            return BodyInvalid("the request body carries text that cannot be stored", [new ErrorDetail(string.Empty, InvalidValue)]);
        }

        foreach (var member in root.EnumerateObject())
        {
            if (!Members.Contains(member.Name, StringComparer.Ordinal))
            {
                return BodyInvalid("the request body carries an unknown field", [new ErrorDetail($"/{member.Name}", "additional_properties")]);
            }
        }

        if (!root.TryGetProperty("producer_id", out var producerElement))
        {
            return Required("producer_id");
        }

        if (producerElement.ValueKind != JsonValueKind.String || !ProducerIdShape.IsMatch(producerElement.GetString()!))
        {
            return BodyInvalid(
                $"producer_id must match {ProducerSlotLimits.ProducerIdPattern}",
                [new ErrorDetail("/producer_id", InvalidValue)]);
        }

        if (!root.TryGetProperty("shard", out var shardElement))
        {
            return Required("shard");
        }

        // The contract's bound is enforced here, not left to the slot comparison (a shard above 256 names a slot no
        // run can plan): 422 /shard either way, but with the shape the contract declares.
        if (shardElement.ValueKind != JsonValueKind.Number
            || !shardElement.TryGetInt32(out var shard)
            || shard < 1
            || shard > MaxShard)
        {
            return BodyInvalid($"shard must be an integer between 1 and {MaxShard}", [new ErrorDetail("/shard", InvalidValue)]);
        }

        if (!root.TryGetProperty("last_sequence", out var sequenceElement))
        {
            return Required("last_sequence");
        }

        if (sequenceElement.ValueKind != JsonValueKind.Number
            || !sequenceElement.TryGetInt64(out var lastSequence)
            || lastSequence < 0)
        {
            return BodyInvalid(
                "last_sequence must be an integer of at least 0",
                [new ErrorDetail("/last_sequence", InvalidValue)]);
        }

        if (!root.TryGetProperty("count", out var countElement))
        {
            return Required("count");
        }

        // int, not long: producer_slot.declared_count is an integer column (migration 0035), so a value that would
        // overflow it is refused here rather than surfacing as an unhandled 22003 from the cast.
        if (countElement.ValueKind != JsonValueKind.Number
            || !countElement.TryGetInt32(out var count)
            || count < 0)
        {
            return BodyInvalid(
                "count must be an integer of at least 0",
                [new ErrorDetail("/count", InvalidValue)]);
        }

        if (!root.TryGetProperty("manifest_hash", out var hashElement))
        {
            return Required("manifest_hash");
        }

        if (hashElement.ValueKind != JsonValueKind.String || !ManifestHashShape().IsMatch(hashElement.GetString()!))
        {
            return BodyInvalid(
                "manifest_hash must match ^sha256:[0-9a-f]{64}$",
                [new ErrorDetail("/manifest_hash", InvalidValue)]);
        }

        return Result.Success(new FinalizeShardRequest(
            producerElement.GetString()!,
            shard,
            lastSequence,
            count,
            hashElement.GetString()!));
    }

    /// <summary>The one member rule every Orchestrator body parse shares: a missing member is 422 naming its pointer.</summary>
    private static Error Invalid(string name, string reason, string message) =>
        Error.Invalid(message, [new ErrorDetail($"/{name}", reason)]);

    /// <summary>422 <c>request_invalid</c> as a failed result, with the parse's own details.</summary>
    private static Result<FinalizeShardRequest> BodyInvalid(string message, IReadOnlyList<ErrorDetail> details) =>
        Result.Failure<FinalizeShardRequest>(Error.Invalid(message, details));

    /// <summary>422 <c>request_invalid</c> for an absent required member, naming its pointer (the run-create shape).</summary>
    private static Result<FinalizeShardRequest> Required(string name) =>
        Result.Failure<FinalizeShardRequest>(Invalid(name, "required", $"{name} is required"));

    /// <summary>An event line of the report: the run, the shard and the two numbers this caller sent. Never a token or a CI reference.</summary>
    [LoggerMessage(
        EventId = 2420,
        Level = LogLevel.Warning,
        Message = "a repeat finalize declared last_sequence {LastSequence} and count {Count} for run {RunId}, producer {ProducerId}; the stored declaration stays authoritative")]
    private static partial void LogDeclarationMismatch(
        ILogger logger,
        Guid runId,
        string producerId,
        long lastSequence,
        int count);

    /// <summary>The contract's <c>shard</c> maximum (05.3.2 §5, 256 planned executions per run at most).</summary>
    private const int MaxShard = 256;

    /// <summary>
    /// The 00 §7 producer-id shape the slot table's CHECK mirrors (04.1.2's <c>ProducerSlotLimits</c>): the shared
    /// constant with its trailing <c>$</c> replaced by <c>\z</c>, because .NET's <c>$</c> also matches before a
    /// trailing newline while PostgreSQL's <c>~</c> does not (<c>ProducerSlotArguments</c>' own construction). Built at
    /// runtime for that reason, so the shared constant stays the one source of the charset.
    /// </summary>
    private static readonly Regex ProducerIdShape = new(
        ProducerSlotLimits.ProducerIdPattern[..^1] + @"\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>The contract's <c>manifest_hash</c> pattern (00 §9.3, 05.3.2 §5): <c>sha256:</c> + 64 lower-case hex.</summary>
    [GeneratedRegex("\\Asha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ManifestHashShape();

    /// <summary>One validated finalize body (00 §9.3).</summary>
    /// <param name="ProducerId">The manifest shard's deterministic producer id (T54).</param>
    /// <param name="Shard">The 1-based manifest shard index; it must be the bound slot's own (Q2).</param>
    /// <param name="LastSequence">The highest producer sequence the shard declares it sent.</param>
    /// <param name="Count">How many events the shard declares it sent.</param>
    /// <param name="ManifestHash">The run's manifest hash, as the shard knows it (AC3).</param>
    private sealed record FinalizeShardRequest(
        string ProducerId,
        int Shard,
        long LastSequence,
        int Count,
        string ManifestHash);

    /// <summary>
    /// The run row <see cref="OrchestratorSql.ReadRunForFinalize"/> and its two lock variants return (Dapper maps the
    /// snake_case columns).
    /// </summary>
    private sealed class FinalizeRunRow
    {
        public Guid ProjectId { get; set; }

        public string ProjectState { get; set; } = string.Empty;

        public string RunState { get; set; } = string.Empty;

        public string ManifestHash { get; set; } = string.Empty;
    }
}
