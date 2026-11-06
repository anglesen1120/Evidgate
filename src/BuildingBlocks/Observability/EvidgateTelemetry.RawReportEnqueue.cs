using System.Diagnostics.Metrics;

namespace Evidgate.BuildingBlocks.Observability;

/// <summary>
/// The raw-report parse meter, kept beside the main telemetry file so neither grows past the file-size bound. The
/// counter carries the producer lookup's closed issue vocabulary and nothing from the manifest: a hint is untrusted
/// plan text, so it never becomes a metric dimension.
/// </summary>
public static partial class EvidgateTelemetry
{
    /// <summary>Counter of completed <c>raw_report</c> artifacts scheduled for parsing, tagged by the lookup's issue code.</summary>
    public const string RawReportEnqueuedMetricName = "evidgate_raw_report_enqueued_total";

    /// <summary>Tag carrying the producer lookup's outcome, one of <c>resolved</c> and the two issue codes.</summary>
    public const string RawReportProducerIssueTag = "issue";

    private static readonly Counter<long> RawReportEnqueued = Meter.CreateCounter<long>(
        RawReportEnqueuedMetricName,
        unit: "{artifact}",
        description: "Completed raw_report artifacts scheduled for parsing, tagged by the producer lookup's issue code. Never labelled with a hint value, a run id or an artifact id.");

    /// <summary>
    /// Counts one scheduled raw-report parse, tagged by the lookup's outcome: <c>resolved</c> when the run's manifest
    /// declared exactly one producer kind, else the issue code that stood in its place.
    /// </summary>
    /// <param name="producerIssue">
    /// The issue code the scheduling decision carried, or <see langword="null"/> when the kind resolved. The module's
    /// vocabulary is mirrored here as literals because this assembly is the module's dependency, not the reverse; a
    /// value outside the closed set collapses to <c>other</c> so the label stays bounded.
    /// </param>
    public static void RecordRawReportEnqueued(string? producerIssue)
    {
        var issue = producerIssue switch
        {
            null => "resolved",
            "producer_unknown" => "producer_unknown",
            "producer_ambiguous" => "producer_ambiguous",
            _ => "other",
        };

        RawReportEnqueued.Add(1, new KeyValuePair<string, object?>(RawReportProducerIssueTag, issue));
    }
}
