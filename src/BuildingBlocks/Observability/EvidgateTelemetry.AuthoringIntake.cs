using System.Diagnostics.Metrics;

namespace Evidgate.BuildingBlocks.Observability;

/// <summary>The requirement-intake instruments, kept apart so each telemetry file stays under the size limit.</summary>
public static partial class EvidgateTelemetry
{
    /// <summary>Counter of requirement intake state transitions, tagged by kind, state and failure code.</summary>
    public const string AuthoringIntakeMetricName = "evidgate_authoring_intake_total";

    /// <summary>Histogram of document extraction durations, tagged by kind.</summary>
    public const string AuthoringExtractionMetricName = "evidgate_authoring_extraction_seconds";

    private static readonly Counter<long> AuthoringIntakes = EvidgateMeter.Instance.CreateCounter<long>(
        AuthoringIntakeMetricName,
        unit: "{intake}",
        description: "Requirement intakes by kind, state and failure_code (closed vocabularies, none when absent). Never a file name, a text or an id, so the series count stays bounded.");

    private static readonly Histogram<double> AuthoringExtractions = EvidgateMeter.Instance.CreateHistogram<double>(
        AuthoringExtractionMetricName,
        unit: "s",
        description: "Wall time of one document extraction attempt, by kind (docx, pdf).",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 20, 30] });

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
}
