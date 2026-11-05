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
    /// Decides one write against the run's own row. A canceled run still accepts evidence and marks it
    /// (<see cref="RunAdmission.AfterCancel"/>), an incomplete run accepts it only inside its late window and marks it
    /// (<see cref="RunAdmission.Late"/>), and a complete run or a closed incomplete one is refused.
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
        var late = string.Equals(runState, "incomplete", StringComparison.Ordinal) && lateWindowOpen;
        var closed = (string.Equals(runState, "complete", StringComparison.Ordinal)
                || string.Equals(runState, "incomplete", StringComparison.Ordinal))
            && !late;

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
/// <param name="Late">True when the run finished incomplete and its late window is still open.</param>
public sealed record RunAdmission(bool Allowed, string? Refusal, bool AfterCancel, bool Late);
