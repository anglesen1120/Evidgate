using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Evidgate.BuildingBlocks.Observability;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Records every measurement of the AI settings and authoring instruments taken while it is alive, as
/// <c>name|value|k=v;k=v</c> text so a test compares the exact list. The meter is process-global and the instruments
/// carry only closed tags, so isolation comes from the callers: they join <see cref="ExclusiveTenancySuite"/>, which
/// runs with no other test in the assembly, and start the capture after their arrangement.
/// </summary>
internal sealed class AuthoringMetricCapture : IDisposable
{
    private static readonly HashSet<string> Names =
    [
        EvidgateTelemetry.AiSettingsWriteMetricName,
        EvidgateTelemetry.AuthoringChangeQueuedMetricName,
        EvidgateTelemetry.AuthoringChangeApplyMetricName,
        EvidgateTelemetry.AuthoringChangeApplyBatchSizeMetricName,
    ];

    private readonly ConcurrentQueue<string> _taken = new();
    private readonly MeterListener _listener;

    internal AuthoringMetricCapture()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == EvidgateTelemetry.MeterName && Names.Contains(instrument.Name))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            _taken.Enqueue($"{instrument.Name}|{value}|{string.Join(';', tags.ToArray().Select(tag => $"{tag.Key}={tag.Value}"))}"));
        _listener.Start();
    }

    /// <summary>The measurements so far, in the order they were taken.</summary>
    internal string[] Taken => [.. _taken];

    public void Dispose() => _listener.Dispose();
}
