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

    private static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> ModuleOperations = Meter.CreateCounter<long>(
        ModuleOperationCounterName,
        unit: "{operation}",
        description: "Module operations started, tagged by evidgate.module.");

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
}
