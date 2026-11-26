using System.Diagnostics.Metrics;

namespace Evidgate.BuildingBlocks.Observability;

/// <summary>The requirement-intake instruments, kept apart so each telemetry file stays under the size limit.</summary>
public static partial class EvidgateTelemetry
{
    /// <summary>Counter of requirement intake state transitions, tagged by kind, state and failure code.</summary>
    public const string AuthoringIntakeMetricName = "evidgate_authoring_intake_total";

    /// <summary>Histogram of document extraction durations, tagged by kind.</summary>
    public const string AuthoringExtractionMetricName = "evidgate_authoring_extraction_seconds";

    /// <summary>Counter of extraction slot renewals and releases that failed, tagged by operation and reason.</summary>
    public const string AuthoringIntakeSlotFailureMetricName = "evidgate_authoring_intake_slot_failures_total";

    private static readonly Counter<long> AuthoringIntakes = EvidgateMeter.Instance.CreateCounter<long>(
        AuthoringIntakeMetricName,
        unit: "{intake}",
        description: "Requirement intakes by kind, state and failure_code (closed vocabularies, none when absent). Never a file name, a text or an id, so the series count stays bounded.");

    private static readonly Histogram<double> AuthoringExtractions = EvidgateMeter.Instance.CreateHistogram<double>(
        AuthoringExtractionMetricName,
        unit: "s",
        description: "Wall time of one document extraction attempt, by kind (docx, pdf).",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 20, 30] });

    private static readonly Counter<long> AuthoringIntakeSlotFailures = EvidgateMeter.Instance.CreateCounter<long>(
        AuthoringIntakeSlotFailureMetricName,
        unit: "{failure}",
        description: "Extraction slot renewals and releases that failed, by operation (renew, release) and reason (privilege, other). A rising privilege series is a grant gap, not load.");

    /// <summary>Counts one intake reaching <paramref name="state"/>.</summary>
    /// <param name="kind">The intake kind (text, docx, pdf, image).</param>
    /// <param name="state">The state reached: received, extracting, ready or rejected.</param>
    /// <param name="failureCode">The closed failure code of a rejection, or null.</param>
    public static void RecordAuthoringIntake(string kind, string state, string? failureCode) =>
        AuthoringIntakes.Add(
            1,
            new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("state", state),
            new KeyValuePair<string, object?>("failure_code", failureCode ?? "none"));

    /// <summary>Records the wall time of one extraction attempt.</summary>
    /// <param name="kind">The intake kind (docx, pdf).</param>
    /// <param name="seconds">The elapsed seconds.</param>
    public static void RecordAuthoringExtraction(string kind, double seconds) =>
        AuthoringExtractions.Record(seconds, new KeyValuePair<string, object?>("kind", kind));

    /// <summary>Counts one extraction slot step that failed and was left to lease expiry.</summary>
    /// <param name="operation">The step: renew or release.</param>
    /// <param name="reason">The cause: privilege (a missing database grant) or other.</param>
    public static void RecordAuthoringIntakeSlotFailure(string operation, string reason) =>
        AuthoringIntakeSlotFailures.Add(
            1,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("reason", reason));
}
