namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The literals the raw-report parse and the reprocess share. Both write the same synthetic slot and report the same
/// caps, so one copy keeps the two paths from drifting apart.
/// </summary>
internal static class RawReportWorkerConstants
{
    /// <summary>
    /// The binding the synthetic slot carries: no credential writes this producer, so the slot is bound to a value no
    /// token can hold and a producer with the same id is refused rather than silently inheriting the report's shard.
    /// </summary>
    internal const string SyntheticBinding = "synthetic:junit-report";

    /// <summary>The highest shard index a run's manifest may declare; a run at the cap cannot take another synthetic shard.</summary>
    internal const int MaxShardIndex = 256;

    /// <summary>The cap name a report over its byte budget carries in telemetry.</summary>
    internal const string ReportBytesCap = "report_bytes";

    /// <summary>The cap name a report over its entry budget carries in telemetry.</summary>
    internal const string ReportCasesCap = "report_cases";

    /// <summary>The media type the child is told the report carries.</summary>
    internal const string XmlMediaType = "application/xml";
}
