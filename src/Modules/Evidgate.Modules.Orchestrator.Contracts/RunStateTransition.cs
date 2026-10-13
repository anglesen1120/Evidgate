using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// The in-process run state transition (05.3.1-T1.1/T1.2, 00 §8.1, rulings T82/T3): the single
/// guarded entry point every caller uses to move a run's state, so no caller re-implements the
/// adjacency table (00 §5.5 C6).
/// <para>
/// It has no endpoint and no permission of its own: each caller already holds its own permission
/// (04's event handler for <c>queued → running</c>, 05.3.2's reconciler for <c>running → collecting</c>
/// and <c>collecting → complete|incomplete</c>, 05.4.1's cancel handler for <c>* → canceled</c>) and
/// reaches this module only through this project (00 §5.5 C5). It runs <b>inside the caller's tenant
/// transaction and unit of work</b>: it never opens a connection, a transaction or an audit unit,
/// never commits and never rolls back, so a rollback of the caller leaves no state change, outbox row
/// or audit row.
/// </para>
/// <para>
/// The <c>created → queued</c> edge has no caller: 05.2.1-T4 inserts the run as <c>queued</c> in the
/// create transaction, so <c>created</c> is never persisted.
/// </para>
/// </summary>
public interface IRunStateTransition
{
    /// <summary>
    /// Classifies the requested <c>from → to</c> edge against the 00 §8.1 adjacency table, under the
    /// run row's <c>FOR UPDATE</c> lock inside the caller's transaction, and applies it when the edge
    /// is a declared forward move. Three outcomes (T82):
    /// <list type="bullet">
    /// <item>the run already holds <c>to</c>, has passed it on the forward path, or <c>from</c> is
    /// terminal → <see cref="TransitionOutcome.NoOp"/>, returning the current state with no write, no
    /// outbox row, no audit row and no version bump, so idempotent retries and concurrent first
    /// batches stay safe;</item>
    /// <item>a declared forward edge → <see cref="TransitionOutcome.Applied"/>: one
    /// <c>update public.run set state = @state</c> (the database's <c>set_updated_at_and_version</c>
    /// trigger bumps <c>version</c>), one <c>run.state_changed</c> outbox row and one
    /// <c>audit_log</c> row (action <see cref="RunStateTransitionContract.AuditAction"/>, subject
    /// <c>run:{id}</c>), all in the caller's transaction;</item>
    /// <item>an undeclared forward-skip between non-terminal states (e.g. <c>running → complete</c>) →
    /// a failure with <see cref="RunStateTransitionContract.ForwardSkipCode"/> (409), never applied.
    /// </item>
    /// </list>
    /// </summary>
    /// <param name="command">The run, the target state, the credited actor and the optional expected version.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <returns>
    /// The applied or no-op outcome, or a failure the caller maps to the 00 §9.2 envelope:
    /// <c>not_found</c> (404) when no run with that id is visible to the caller's tenant;
    /// <see cref="RunStateTransitionContract.ForwardSkipCode"/> (409) for an undeclared forward-skip;
    /// <c>request_invalid</c> (422) when <c>to_state</c> is not a 00 §8.1 state; <c>precondition_failed</c>
    /// (412, AC6) when <see cref="TransitionRunStateCommand.ExpectedVersion"/> does not match the row's
    /// current <c>version</c> — the failure's <c>details</c> mark the stale <c>If-Match</c> and carry the
    /// current <c>version</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is null.</exception>
    /// <exception cref="InvalidOperationException">No tenant scope is open (the tenant-scope guard throws before any statement).</exception>
    ValueTask<Result<TransitionResult>> TransitionAsync(
        TransitionRunStateCommand command,
        CancellationToken cancellationToken);
}

/// <summary>One requested run state transition.</summary>
/// <param name="RunId">The run to move; it must be visible to the caller's tenant scope.</param>
/// <param name="ToState">The target state, one of the 00 §8.1 vocabulary (0031's <c>run_state_check</c>).</param>
/// <param name="Actor">Who the applied transition is credited to in <c>audit_log</c> (00 §7).</param>
/// <param name="ExpectedVersion">
/// The <c>run.version</c> the caller read, or null to skip the precondition. A mismatch is 412
/// <c>precondition_failed</c> with the current version in <c>details</c> (00 §5.5 C4, AC6).
/// </param>
public sealed record TransitionRunStateCommand(
    Guid RunId,
    string ToState,
    TransitionActor Actor = TransitionActor.System,
    int? ExpectedVersion = null);

/// <summary>Who an applied transition is credited to in <c>audit_log</c> (05.3.1 AC5).</summary>
public enum TransitionActor
{
    /// <summary>An automatic edge (first event, first finalize, reconciler verdict): actor <c>system</c>.</summary>
    System,

    /// <summary>A caller-driven edge (a cancel): the caller's own audit identity, or <c>system</c> when no principal is published.</summary>
    Caller,
}

/// <summary>What a <see cref="IRunStateTransition.TransitionAsync"/> call did (05.3.1 T82).</summary>
public enum TransitionOutcome
{
    /// <summary>The run moved: state, outbox row and audit row were written in the caller's transaction.</summary>
    Applied,

    /// <summary>The request asked for the current or an already-passed state, or the run is terminal: nothing was written.</summary>
    NoOp,
}

/// <summary>The result of an applied or no-op transition.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="State">The run's state after the call: the new state when applied, the current one on a no-op.</param>
/// <param name="Version">The run's <c>version</c> after the call (bumped by the database on an apply, unchanged on a no-op).</param>
/// <param name="CurrentEvidenceRevisionId">The run's current evidence revision; null until 05.3.2 sets it.</param>
public sealed record TransitionResult(
    TransitionOutcome Outcome,
    string State,
    int Version,
    Guid? CurrentEvidenceRevisionId);

/// <summary>The stable names and codes of a run state transition (00 §7/§9.2).</summary>
public static class RunStateTransitionContract
{
    /// <summary>The outbox task type of an applied transition (00 §5.3/§9.5), consumed by 09, 12 and the webhooks.</summary>
    public const string OutboxTaskType = "orchestrator.run.state_changed";

    /// <summary>The <c>audit_log.action</c> of an applied transition (00 §7).</summary>
    public const string AuditAction = "run.state_changed";

    /// <summary>
    /// 409 <c>invalid_transition</c>: the requested edge is an undeclared forward-skip between
    /// non-terminal states (05.3.1 AC3). The 00 §9.2 code is shared with 02.1.2's membership
    /// transitions; the failure is never applied.
    /// </summary>
    public const string ForwardSkipCode = ApiErrorCode.InvalidTransition;
}
