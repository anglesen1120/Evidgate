using Evidgate.BuildingBlocks.Observability;

namespace Evidgate.Modules.Authoring.Application.Intake.Jobs;

/// <summary>The one place intake transitions and extraction timings are counted.</summary>
internal static class IntakeJobMetrics
{
    /// <summary>Counts an intake reaching <paramref name="state"/>.</summary>
    /// <param name="kind">The intake kind.</param>
    /// <param name="state">received, extracting, ready or rejected.</param>
    /// <param name="failureCode">The closed failure code of a rejection, or null.</param>
    internal static void Record(string kind, string state, string? failureCode = null) =>
        EvidgateTelemetry.RecordAuthoringIntake(kind, state, failureCode);

    /// <summary>Records one extraction attempt's duration.</summary>
    /// <param name="kind">docx or pdf.</param>
    /// <param name="elapsed">The measured time.</param>
    internal static void RecordDuration(string kind, TimeSpan elapsed) =>
        EvidgateTelemetry.RecordAuthoringExtraction(kind, elapsed.TotalSeconds);

    /// <summary>Counts a slot renewal or release that failed.</summary>
    /// <param name="operation">renew or release.</param>
    /// <param name="privilege">True when the database refused for a missing grant, false for any other cause.</param>
    internal static void RecordSlotFailure(string operation, bool privilege) =>
        EvidgateTelemetry.RecordAuthoringIntakeSlotFailure(operation, privilege ? "privilege" : "other");
}
