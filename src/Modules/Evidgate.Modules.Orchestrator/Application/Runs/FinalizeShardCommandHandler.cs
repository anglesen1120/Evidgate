using Evidgate.BuildingBlocks.SharedKernel.Serialization;
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
        audit.Describe(new AuditObject("run", command.RunId.ToString("D"), ProjectId: run.ProjectId));

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
