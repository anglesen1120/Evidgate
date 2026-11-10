using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Evidgate.BuildingBlocks.Observability;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Captures the healing gateway's refusal counter: every measurement taken while the listener lives, by its
/// <c>reason</c> tag, through the public telemetry surface.
/// </summary>
internal static class HealingTelemetryCapture
{
    /// <summary>
    /// Listens for <c>evidgate_healing_llm_refused_total</c> and collects the <c>reason</c> tag of every measurement
    /// taken until the returned listener is disposed.
    /// </summary>
    /// <param name="reasons">The reason tags captured so far, filled as the tests run.</param>
    internal static MeterListener RefusedCalls(out ConcurrentBag<string> reasons)
    {
        var bag = new ConcurrentBag<string>();
        reasons = bag;
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == EvidgateTelemetry.MeterName
                    && instrument.Name == EvidgateTelemetry.HealingLlmRefusedMetricName)
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
}
