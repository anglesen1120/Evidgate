using System.Diagnostics.Metrics;

namespace Evidgate.BuildingBlocks.Observability;

/// <summary>
/// The quota refusal instrument, kept in its own file so the metric vocabulary stays readable: the events route
/// refuses a batch the project's billing period has no room for, and an operator alerts on the rate of that
/// refusal rather than on any single tenant's counter.
/// </summary>
public static partial class EvidgateTelemetry
{
    /// <summary>
    /// Counter of event batches refused because the project's billing-period quota is used up, tagged by
    /// <c>kind</c> from a closed set (<c>quota_exceeded</c>, <c>report_quota_exceeded</c>). Low cardinality only: never an org id, a project
    /// id, a counter value or any request value.
    /// </summary>
    public const string IngestQuotaDeniedMetricName = "evidgate_ingest_quota_denied_total";

    private static readonly Counter<long> IngestQuotaDenied = Meter.CreateCounter<long>(
        IngestQuotaDeniedMetricName,
        unit: "{batch}",
        description: "Event batches refused because the project's billing-period quota is used up, by kind. Never labelled with an org or project id, a counter value or any request value.");

    /// <summary>
    /// Counts one event batch refused because the project's billing-period quota is used up, tagged by
    /// <paramref name="kind"/> from the closed set only — never an org id, a project id or a counter value.
    /// </summary>
    /// <param name="kind">The refusal kind: <c>quota_exceeded</c> or <c>report_quota_exceeded</c>.</param>
    public static void RecordIngestQuotaDenied(string kind) =>
        IngestQuotaDenied.Add(1, new KeyValuePair<string, object?>("kind", kind));
}
