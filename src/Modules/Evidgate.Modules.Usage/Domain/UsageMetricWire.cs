using Evidgate.Modules.Usage.Contracts;

namespace Evidgate.Modules.Usage.Domain;

/// <summary>
/// The <c>metric</c> wire mapping (12.3.1 §4): the column stores the seven strings the migration's <c>check</c>
/// constraint admits, while the module and its callers speak <see cref="UsageMetric"/>. The names live once in
/// <see cref="UsageLedgerMetricNames"/>, so this mapping only decides which member means which name.
/// </summary>
internal static class UsageMetricWire
{
    /// <summary>The column value of <paramref name="metric"/>.</summary>
    /// <param name="metric">A declared metric; an undeclared value is a bug this switch refuses loudly.</param>
    internal static string ToWireValue(UsageMetric metric) => metric switch
    {
        UsageMetric.Results => UsageLedgerMetricNames.Results,
        UsageMetric.ArtifactGbMonth => UsageLedgerMetricNames.ArtifactGbMonth,
        UsageMetric.EgressGb => UsageLedgerMetricNames.EgressGb,
        UsageMetric.AiTokens => UsageLedgerMetricNames.AiTokens,
        UsageMetric.SupportHours => UsageLedgerMetricNames.SupportHours,
        UsageMetric.RunnerMinutes => UsageLedgerMetricNames.RunnerMinutes,
        UsageMetric.DeviceMinutes => UsageLedgerMetricNames.DeviceMinutes,
        _ => throw new ArgumentOutOfRangeException(
            nameof(metric),
            metric,
            "the usage metric is not one of the seven the ledger stores"),
    };
}
