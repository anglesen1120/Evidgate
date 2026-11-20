namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// The one decision of whether a run still admits evidence, shared by every writer (the event-batch route and the
/// report parse task). It is a pure function of the run's state, its project's state and its late window, so the two
/// writers cannot drift on which evidence is refused and which is marked late or after-cancel.
/// </summary>
public static class RunAdmissionPolicy
{
    /// <summary>The refusal of a project that is being deleted (06.4.4's conflict).</summary>
    public const string ProjectDeleted = "project_deleted";

    /// <summary>The refusal of a suspended project (06.1.2's upload-url decision).</summary>
    public const string ProjectSuspended = "project_suspended";

    /// <summary>The refusal of a run that no longer accepts evidence: complete, or incomplete past its late window.</summary>
    public const string RunClosed = "run_closed";

    /// <summary>
    /// The one late-window predicate every admission read composes into its statement, over the run alias <c>r</c>:
    /// an <c>incomplete</c> run until its collection deadline plus the window, a <c>complete</c> run until its own
    /// completion instant plus the window, and no other state ever. Null-safe: a missing instant answers false.
    /// </summary>
    public const string LateWindowOpenSql = """
        coalesce(case r.state
            when 'incomplete' then now() <= r.collection_deadline_at + make_interval(mins => r.late_window_minutes)
            when 'complete' then r.completed_at is not null
                and now() <= r.completed_at + make_interval(mins => r.late_window_minutes)
            else false
        end, false)
        """;

    /// <summary>
    /// Decides one write against the run's own row. A canceled run still accepts evidence and marks it
    /// (<see cref="RunAdmission.AfterCancel"/>); an incomplete or a complete run accepts it only inside its late
    /// window and marks it (<see cref="RunAdmission.Late"/>); a finished run outside that window is refused.
    /// </summary>
    /// <param name="projectState">The project's state: <c>active</c>, <c>suspended</c> or <c>deleted</c>.</param>
    /// <param name="runState">
    /// The run's state: <c>created</c>, <c>queued</c>, <c>running</c>, <c>collecting</c>, <c>complete</c>,
    /// <c>incomplete</c> or <c>canceled</c>.
    /// </param>
    /// <param name="lateWindowOpen">True when <c>now()</c> is at or before the run's late-window end.</param>
    /// <returns>The decision: allowed or the one refusal reason, plus the two evidence markers.</returns>
    public static RunAdmission Decide(string projectState, string runState, bool lateWindowOpen)
    {
        var afterCancel = string.Equals(runState, "canceled", StringComparison.Ordinal);
        var incomplete = string.Equals(runState, "incomplete", StringComparison.Ordinal);
        var complete = string.Equals(runState, "complete", StringComparison.Ordinal);
        var late = (incomplete || complete) && lateWindowOpen;
        var closed = (incomplete || complete) && !late;

        var refusal = string.Equals(projectState, "deleted", StringComparison.Ordinal)
            ? ProjectDeleted
            : string.Equals(projectState, "suspended", StringComparison.Ordinal)
                ? ProjectSuspended
                : closed
                    ? RunClosed
                    : null;

        return new RunAdmission(refusal is null, refusal, afterCancel, late);
    }
}

/// <summary>
/// One admit/refuse decision for a run's evidence. A refusal is terminal for the writer: nothing is stored, and the
/// writer answers its own named result instead of retrying.
/// </summary>
/// <param name="Allowed">True when the evidence may be written.</param>
/// <param name="Refusal">The refusal reason when <paramref name="Allowed"/> is false, otherwise null.</param>
/// <param name="AfterCancel">True when the run is canceled, so the evidence is post-cancel.</param>
/// <param name="Late">True when the run finished incomplete or complete and its late window is still open.</param>
public sealed record RunAdmission(bool Allowed, string? Refusal, bool AfterCancel, bool Late);
