using Evidgate.Modules.Orchestrator.Contracts;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// Whether a run still accepts a <em>reprocess</em>, as distinct from whether it still accepts new ingest evidence.
/// <para>
/// It shares the intake policy's verdict everywhere except the two finished states. A run that finished
/// <c>incomplete</c> stays open to a reprocess after its late window closed: that window bounds what an ingest route or
/// a parse task may still add to a run the clock already judged, while a reprocess is a deliberate re-reading of bytes
/// the run itself holds and the one path that can turn an incomplete verdict into an evidence-backed one. A
/// <c>complete</c> run accepts a reprocess too, and stays <c>complete</c>: the re-read rows reach the run's next
/// revision, never its state. A deleted or suspended project refuses as it does for every writer.
/// </para>
/// </summary>
internal static class ReprocessAdmission
{
    /// <summary>Decides one reprocess against the run's and its project's state.</summary>
    /// <param name="projectState">The project's state: <c>active</c>, <c>suspended</c> or <c>deleted</c>.</param>
    /// <param name="runState">The run's state, from the closed vocabulary the run state machine defines.</param>
    /// <param name="lateWindowOpen">True when <c>now()</c> is at or before the run's late-window end.</param>
    /// <returns>The decision, with the rows' <c>after_cancel</c>/<c>late</c> markers already resolved.</returns>
    public static RunAdmission Decide(string projectState, string runState, bool lateWindowOpen)
    {
        var intake = RunAdmissionPolicy.Decide(projectState, runState, lateWindowOpen);
        if (intake.Allowed || !string.Equals(intake.Refusal, RunAdmissionPolicy.RunClosed, StringComparison.Ordinal))
        {
            return intake;
        }

        // Closed because the clock ran out, not because the evidence is final: the run stays where it is and the attempt's
        // rows are marked late, so the freeze path records a new revision instead of moving the run.
        if (string.Equals(runState, "incomplete", StringComparison.Ordinal))
        {
            return new RunAdmission(Allowed: true, Refusal: null, AfterCancel: false, Late: true);
        }

        // A complete run is final, so its re-read rows are neither late nor post-cancel: they count through the run's
        // next revision alone, which is the one thing a reprocess may append to a finished run.
        return string.Equals(runState, "complete", StringComparison.Ordinal)
            ? new RunAdmission(Allowed: true, Refusal: null, AfterCancel: false, Late: false)
            : intake;
    }
}
