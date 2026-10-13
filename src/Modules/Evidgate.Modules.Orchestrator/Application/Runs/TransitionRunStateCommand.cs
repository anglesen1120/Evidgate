using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>
/// <see cref="IRunStateTransition"/> over the caller's tenant transaction (05.3.1-T1.1/T1.2/T1.3, 00 §8.1,
/// rulings T82/T3/C4). It never opens a connection or a transaction, never commits and never rolls back:
/// the run row is read <c>FOR NO KEY UPDATE</c> on the ambient scope (so terminality and the passed-state
/// check run under the same row lock as the write, AC4), the edge is classified against
/// <see cref="RunStateMachine"/>, and an applied edge writes the state, the <c>run.state_changed</c> outbox
/// row and the <c>audit_log</c> row in that one transaction (AC5). The edge is classified <b>before</b> the
/// version precondition is checked (AC2/AC3 before AC6): a request for the current or an already-passed
/// state, or any request out of a terminal state, is the no-op that returns the current state whatever
/// <see cref="TransitionRunStateCommand.ExpectedVersion"/> the caller passed, so only an edge that will be
/// applied can fail 412.
/// </summary>
internal sealed class TransitionRunStateCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IOutboxWriter outbox,
    IAuditTrail audit)
    : IRunStateTransition
{
    /// <summary>
    /// The run row, locked: RLS scopes it to the caller's tenant, so an unknown and a foreign id are the
    /// same null. <c>state</c> and <c>version</c> are read under this lock, which is what makes the
    /// passed-state and terminality checks race-free (AC4) and lets the version precondition (AC6) and the
    /// database's own version bump agree.
    /// <para>
    /// <c>FOR NO KEY UPDATE</c>, not <c>FOR UPDATE</c>: it is the lock the guarded write itself takes (an
    /// <c>UPDATE</c> of a non-key column, the same choice as <c>TenancyReadSql.ProjectCreateGate</c>), it still
    /// conflicts with another transition (<c>NO KEY UPDATE</c> and <c>FOR SHARE</c> included), so two
    /// transitions serialise exactly as AC4 requires — but it does not conflict with the <c>FOR KEY SHARE</c>
    /// every FK check on <c>run</c> takes, so a concurrent child-row insert (<c>run_execution</c>,
    /// <c>run_event</c>, …) is never blocked by a transition that has not written yet.
    /// </para>
    /// <para>
    /// Lock-order rule for callers: do not call the transition while already holding a weaker lock on the same
    /// run row — <c>FOR SHARE</c> (e.g. <c>RunEventScopeReader</c>) or <c>FOR KEY SHARE</c>. Two such callers
    /// both upgrading to <c>NO KEY UPDATE</c> deadlock (40P01), because each waits on the other's shared lock.
    /// A caller that may transition and also reads the run under <c>FOR SHARE</c> (04.1.1-T4's event handler)
    /// must take the run row <c>FOR NO KEY UPDATE</c> up front, or run the transition before any shared read.
    /// </para>
    /// </summary>
    private const string LockRunForTransition = """
        select state, version, current_evidence_revision_id
          from public.run
         where id = @run_id
         for no key update
        """;

    /// <summary>The single guarded write: the one column 0036 grants the app role on <c>run</c>.</summary>
    private const string UpdateRunState = """
        update public.run set state = @state where id = @run_id
        """;

    /// <inheritdoc />
    public async ValueTask<Result<TransitionResult>> TransitionAsync(
        TransitionRunStateCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Orchestrator, nameof(TransitionRunStateCommand));

        // An absent or empty target is a malformed request, never a null-argument fault: 422 before the vocabulary
        // lookup and before any statement.
        if (string.IsNullOrEmpty(command.ToState) || !RunStateMachine.IsKnownState(command.ToState))
        {
            return Result.Failure<TransitionResult>(Error.Invalid(
                "to_state is not a run state",
                [new ErrorDetail("/to_state", "invalid_value")]));
        }

        // The tenant-scope guard runs before any statement (00 §5.5 C3): with no open scope this throws and nothing
        // is sent. Its value is deliberately unused — Require exists for that throw, the scope itself is ambient.
        _ = scopes.Require();

        var row = await db.QuerySingleOrDefaultAsync<RunRow>(
            LockRunForTransition,
            new { run_id = command.RunId },
            cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure<TransitionResult>(RunNotFound);
        }

        // AC2/AC3 before AC6: the decision is taken first, so a stale ExpectedVersion can only ever fail an edge
        // that would have been applied — a no-op or a forward-skip is decided without consulting it.
        switch (RunStateMachine.Classify(row.State, command.ToState))
        {
            case RunTransitionDecision.NoOp:
                // AC2/AC3: the run holds or has passed the state, or is terminal — the current state is the
                // answer, and nothing is written (no outbox row, no audit row, no version bump), whatever
                // ExpectedVersion the caller passed.
                return Result.Success(new TransitionResult(
                    TransitionOutcome.NoOp, row.State, row.Version, row.CurrentEvidenceRevisionId));

            case RunTransitionDecision.ForwardSkip:
                // AC3: an undeclared edge to a state the run has not passed is never applied.
                return Result.Failure<TransitionResult>(ForwardSkip(row.State, command.ToState));

            default:
                break;
        }

        // AC6 (00 §5.5 C4): the row moved on since the caller read it. Only the applying branch pays this check.
        if (command.ExpectedVersion is { } expected && expected != row.Version)
        {
            return Result.Failure<TransitionResult>(StaleVersion(row.Version));
        }

        await db.ExecuteAsync(
            UpdateRunState,
            new { state = command.ToState, run_id = command.RunId },
            cancellationToken).ConfigureAwait(false);

        // The database's set_updated_at_and_version trigger bumped version to old + 1 (0004/0031), so the
        // returned version is the row's version after this write.
        var version = row.Version + 1;

        await outbox.EnqueueAsync(
            RunStateTransitionContract.OutboxTaskType,
            JsonSerializer.Serialize(new
            {
                run_id = command.RunId,
                from = row.State,
                to = command.ToState,
                evidence_revision_id = row.CurrentEvidenceRevisionId,
            }),
            "run",
            command.RunId,
            $"orchestrator:run:{command.RunId:D}:state:{command.ToState}",
            cancellationToken).ConfigureAwait(false);

        // AC5: the audit entry (actor system for automatic edges, the caller for cancel) is appended to the
        // caller's unit of work, so the tenant stage flushes it in this same transaction. It is the canonical
        // audit of the edge (action run.state_changed, object run:{id}). Describe/Record share one pending slot
        // (AuditTrail.cs) and Record consumes it, so a caller that audits its own action must call
        // audit.Describe(...) for its own entry only AFTER this method returns: a Describe made before the call
        // would be overwritten by this one and then swallowed by this Record.
        audit.Describe(new AuditObject("run", command.RunId.ToString("D")));
        audit.Record(RunStateTransitionContract.AuditAction, ActorFor(command.Actor));

        activity?.SetTag("evidgate.run_id", command.RunId.ToString("D"));
        activity?.SetTag("evidgate.run.state", command.ToState);

        return Result.Success(new TransitionResult(
            TransitionOutcome.Applied, command.ToState, version, row.CurrentEvidenceRevisionId));
    }

    /// <summary>The audit actor of an applied transition: <c>system</c> for an automatic edge, the resolved caller for a cancel.</summary>
    private AuditActor ActorFor(TransitionActor actor) =>
        actor == TransitionActor.Caller
            ? AuditActorResolver.From(principals.Current)
            : new AuditActor(AuditActorTypes.System, "system");

    /// <summary>404: no run with that id is visible to the caller's tenant (unknown and foreign are the same code).</summary>
    private static Error RunNotFound => Error.NotFound("no run with that id is visible to this tenant");

    /// <summary>
    /// 412 <c>precondition_failed</c> (00 §5.5 C4, AC6): the row moved on since the caller read it; the
    /// <c>details</c> mark the stale <c>If-Match</c> and carry the current <c>version</c>, so the caller can
    /// retry against it (the <c>PolicyRules.Stale</c>/<c>UpdateMembership.Stale</c> shape).
    /// </summary>
    private static Error StaleVersion(int currentVersion) => new(
        ApiErrorCode.PreconditionFailed,
        "the run changed since it was read",
        StatusCodes.Status412PreconditionFailed,
        Retryable: false,
        Details:
        [
            new ErrorDetail("If-Match", "stale"),
            new ErrorDetail("version", currentVersion.ToString(CultureInfo.InvariantCulture)),
        ]);

    /// <summary>409: the requested edge is an undeclared forward-skip between non-terminal states (AC3).</summary>
    private static Error ForwardSkip(string from, string to) => Error.Conflict(
        RunStateTransitionContract.ForwardSkipCode,
        $"a run cannot move from '{from}' to '{to}'");

    /// <summary>The run row the transition reads under its lock (Dapper maps the snake_case columns).</summary>
    private sealed class RunRow
    {
        public string State { get; set; } = string.Empty;

        public int Version { get; set; }

        public Guid? CurrentEvidenceRevisionId { get; set; }
    }
}
