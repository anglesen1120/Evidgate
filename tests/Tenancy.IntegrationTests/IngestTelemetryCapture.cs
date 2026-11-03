// Two capture helpers shared by the ingest suites so every assertion reads the same instruments: the
// evidgate_ingest_batch_rejected_total counter's reason tags and the module spans of the Evidgate source.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Evidgate.BuildingBlocks.Observability;

namespace Tenancy.IntegrationTests;

/// <summary>The measurements and spans the ingest suites assert on, captured through the public telemetry surface.</summary>
internal static class IngestTelemetryCapture
{
    /// <summary>
    /// Listens for <c>evidgate_ingest_batch_rejected_total</c> and collects the <c>reason</c> tag of every
    /// measurement taken while the listener runs.
    /// </summary>
    internal static MeterListener RejectedBatches(out ConcurrentBag<string> reasons)
    {
        var bag = new ConcurrentBag<string>();
        reasons = bag;
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == EvidgateTelemetry.MeterName
                    && instrument.Name == EvidgateTelemetry.IngestBatchRejectedMetricName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "reason" && tag.Value is string reason)
                {
                    bag.Add(reason);
                }
            }
        });
        listener.Start();

        return listener;
    }

    /// <summary>
    /// Listens to every source, so the request's own server span (ASP.NET Core) and the application's
    /// module spans are captured alike.
    /// </summary>
    internal static ActivityListener AllSpans(out ConcurrentQueue<Activity> spans)
    {
        var queue = new ConcurrentQueue<Activity>();
        spans = queue;
        var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = queue.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        return listener;
    }

    /// <summary>Listens on the application's activity source and collects every stopped span.</summary>
    internal static ActivityListener ModuleSpans(out ConcurrentQueue<Activity> spans)
    {
        var queue = new ConcurrentQueue<Activity>();
        spans = queue;
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == EvidgateTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = queue.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        return listener;
    }
}
