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

    /// <summary>Counter of manifests refused at upload, tagged by reason (05.1.1 §11).</summary>
    public const string ManifestRejectedMetricName = "evidgate_manifest_rejected_total";

    /// <summary>Counter of adapter capabilities refused at first send, tagged by code and protocol-major bucket (04.7.1-T2).</summary>
    public const string AdapterCapabilityRejectedMetricName = "evidgate_adapter_capability_rejected_total";

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

    private static readonly Counter<long> ManifestRejections = Meter.CreateCounter<long>(
        ManifestRejectedMetricName,
        unit: "{manifest}",
        description: "Manifests refused at upload, by reason (05.1.1). Never labelled with a manifest value, case name or hash.");

    private static readonly Counter<long> AdapterCapabilityRejections = Meter.CreateCounter<long>(
        AdapterCapabilityRejectedMetricName,
        unit: "{capability}",
        description: "Adapter capabilities refused at first send, by code and protocol-major bucket (04.7.1). Never labelled with a document value, adapter name or hash.");

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

    /// <summary>Counts one manifest refused for <paramref name="reason"/> (a closed vocabulary: <c>schema</c>, <c>unknown_case_revision</c>, …).</summary>
    /// <param name="reason">The reason token; never a value from the manifest.</param>
    public static void RecordManifestRejected(string reason) =>
        ManifestRejections.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>
    /// Counts one adapter capability refused at first send (04.7.1-T2), tagged by the
    /// rejection <paramref name="code"/> and a bounded protocol-major bucket.
    /// </summary>
    /// <param name="code">The <c>ApiErrorCode</c> token: <c>capability_invalid</c>, <c>unsupported_protocol_major</c> or <c>capability_conflict</c>; never a document value.</param>
    /// <param name="protocolMajor">The adapter's declared major, bucketed to <c>1</c>–<c>8</c> or <c>other</c>; <c>null</c> when the document carried none.</param>
    public static void RecordAdapterCapabilityRejected(string code, int? protocolMajor) =>
        AdapterCapabilityRejections.Add(
            1,
            new KeyValuePair<string, object?>("code", code),
            new KeyValuePair<string, object?>("protocol_major", ProtocolMajorBucket(protocolMajor)));

    /// <summary>Bounds the protocol-major label to <c>1</c>–<c>8</c> (the majors a v1 server can see) or <c>other</c>, so the tag stays low-cardinality.</summary>
    private static string ProtocolMajorBucket(int? protocolMajor) => protocolMajor switch
    {
        1 => "1",
        2 => "2",
        3 => "3",
        4 => "4",
        5 => "5",
        6 => "6",
        7 => "7",
        8 => "8",
        _ => "other",
    };

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
