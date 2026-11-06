using Evidgate.Modules.Usage.Contracts;

namespace Evidgate.Modules.Usage.Domain;

/// <summary>
/// Which of the seven ledger metrics has a producer that writes rows today, and the reason the rest cannot be
/// reported as usage. This is the one place the answer lives: the summary, the plan and the quota screen all read
/// it, so a metric becomes available here when its emitter lands and nowhere else.
/// </summary>
/// <remarks>
/// Only <see cref="UsageMetric.Results"/> has an emitter in this build (the ingestion batch's meter task). The other
/// six have no code that writes <c>usage_ledger_entry</c> or <c>quota_counter</c> yet, so reporting them as
/// <c>0</c> would claim a measurement nobody made. Their producers are named in the reason so the next emitter
/// knows what to remove.
/// </remarks>
internal static class UsageMetricAvailability
{
    /// <summary>True when a producer in this build writes the metric's ledger rows.</summary>
    /// <param name="metric">A declared metric.</param>
    internal static bool IsMetered(UsageMetric metric) => UnavailableReason(metric) is null;

    /// <summary>
    /// Why the metric cannot be reported, or null when it can. The text is user-facing: it is returned verbatim as
    /// <c>unavailable_reason</c>.
    /// </summary>
    /// <param name="metric">A declared metric; an undeclared value is a bug this switch refuses loudly.</param>
    internal static string? UnavailableReason(UsageMetric metric) => metric switch
    {
        UsageMetric.Results => null,
        UsageMetric.ArtifactGbMonth =>
            "storage metering is not running yet: the GB-month roll-up of 06.5.2 is the producer of this metric",
        UsageMetric.EgressGb =>
            "the object store does not report egress bytes yet, so no egress usage can be measured",
        UsageMetric.AiTokens =>
            "the healing gateway that would meter model tokens is a V2 integration; no V1 producer writes this metric",
        UsageMetric.SupportHours =>
            "the support workflow that records support time does not emit this metric yet",
        UsageMetric.RunnerMinutes =>
            "no metering point writes runner minutes yet",
        UsageMetric.DeviceMinutes =>
            "no metering point writes device-farm minutes yet",
        _ => throw new ArgumentOutOfRangeException(
            nameof(metric),
            metric,
            "the usage metric is not one of the seven the ledger stores"),
    };
}
