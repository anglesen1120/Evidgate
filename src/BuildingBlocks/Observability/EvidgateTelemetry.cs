using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Evidgate.BuildingBlocks.Observability;

/// <summary>
/// One telemetry vocabulary for both entry points (01.1.2-T1.2): the
/// <see cref="ActivitySource"/> and <see cref="Meter"/> the hosts subscribe to,
/// and the <c>evidgate.module</c> tag every module-crossing span carries so a
/// trace shows which modules the 00 §5.5 slice crossed.
/// </summary>
public static class EvidgateTelemetry
{
    /// <summary>Name of the single application activity source (00 §5.7, plan 10).</summary>
    public const string ActivitySourceName = "Evidgate";

    /// <summary>Name of the single application meter.</summary>
    public const string MeterName = "Evidgate";

    /// <summary>Span/metric tag naming the module a unit of work belongs to (00 §5.1 names).</summary>
    public const string ModuleTag = "evidgate.module";

    /// <summary>Span tag naming the module operation, e.g. <c>CreateRun</c>.</summary>
    public const string OperationTag = "evidgate.operation";

    /// <summary>Span tag carrying the 00 §9.1 request id.</summary>
    public const string RequestIdTag = "evidgate.request_id";

    /// <summary>Counter of module operations, tagged by <see cref="ModuleTag"/>.</summary>
    public const string ModuleOperationCounterName = "evidgate.module.operations";

    /// <summary>Histogram of pipeline-handler durations in milliseconds (01.4.2-T4.1, the C2 telemetry stage).</summary>
    public const string PipelineDurationMetricName = "evidgate.pipeline.duration";

    /// <summary>Span tag naming the message type a pipeline span ran (e.g. <c>CreateRun</c>).</summary>
    public const string PipelineMessageTag = "evidgate.message";

    /// <summary>Span tag carrying the thrown exception's type on a failed pipeline run (OTel <c>error.type</c>).</summary>
    public const string PipelineErrorTag = "error.type";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> ModuleOperations = Meter.CreateCounter<long>(
        ModuleOperationCounterName,
        unit: "{operation}",
        description: "Module operations started, tagged by evidgate.module.");

    private static readonly Histogram<double> PipelineDurations = Meter.CreateHistogram<double>(
        PipelineDurationMetricName,
        unit: "ms",
        description: "C2 pipeline handler duration by message type.");

    /// <summary>
    /// Starts the span for one module operation, tagged with the module name
    /// (AC6). Returns null when nothing listens, so callers use
    /// <c>using var activity = …</c> unconditionally.
    /// </summary>
    public static Activity? StartModuleActivity(string module, string operation)
    {
        var activity = Source.StartActivity(operation, ActivityKind.Internal);
        activity?.SetTag(ModuleTag, module);
        activity?.SetTag(OperationTag, operation);

        ModuleOperations.Add(1, new KeyValuePair<string, object?>(ModuleTag, module));

        return activity;
    }

    /// <summary>
    /// Starts the span for one pipeline message (01.4.2-T4.1): tagged with the
    /// message type but NOT with <see cref="ModuleTag"/> — the pipeline is not a
    /// module, so the per-module tag vocabulary stays accurate. Returns null when
    /// nothing listens.
    /// </summary>
    public static Activity? StartPipelineActivity(string message)
    {
        var activity = Source.StartActivity(message, ActivityKind.Internal);
        activity?.SetTag(PipelineMessageTag, message);

        return activity;
    }

    /// <summary>
    /// Records the handler duration of one pipeline message, tagged with the
    /// message type and whether it failed, so 10's dashboards split the two.
    /// </summary>
    public static void RecordPipelineDuration(string message, TimeSpan duration, bool error)
    {
        PipelineDurations.Record(
            duration.TotalMilliseconds,
            new KeyValuePair<string, object?>(PipelineMessageTag, message),
            new KeyValuePair<string, object?>("error", error));
    }
}
