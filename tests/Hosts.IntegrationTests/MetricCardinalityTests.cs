using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Evidgate.BuildingBlocks.Observability;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// 10.3.1-T1.4 / AC3 (S3, cardinality): every metric tag the platform emits is one of a closed set of
/// keys, and every tag value is a token from a closed vocabulary — never free text, never a tenant,
/// run, job or request id. The rule this pins is the one the plan's §8 states: no customer identifier
/// in any label, because a label is a series and a series is a bill.
/// </summary>
/// <remarks>
/// The measurement surface is a <see cref="MeterListener"/> over the platform meter — the same surface
/// the OTel pipeline subscribes to — and every public recording method of
/// <see cref="EvidgateTelemetry"/> is exercised, so a new instrument or a new tag fails here rather
/// than in the billing dashboard.
/// </remarks>
public sealed class MetricCardinalityTests
{
    /// <summary>
    /// The closed set of tag KEYS. Adding one is a reviewable decision about what may travel in a
    /// label; it is not a formality, and a key not listed here fails the test.
    /// </summary>
    private static readonly HashSet<string> AllowedTagKeys = new(StringComparer.Ordinal)
    {
        EvidgateTelemetry.ModuleTag,
        EvidgateTelemetry.PipelineMessageTag,
        "reason",
        "code",
        "protocol_major",
        "normalized_status",
        "error",
        "provider",
        "outcome",
        "trigger",
        "written",
    };

    /// <summary>Id-shaped key names that must never become a label (they would be one series per row).</summary>
    private static readonly string[] IdentifierKeyMarkers =
    [
        "tenant",
        "org",
        "run_id",
        "job_id",
        "request_id",
        "session_id",
        "trace_id",
        "email",
        "subject",
        "user",
        "ip",
    ];

    /// <summary>A tag value is a token: lowercase words, digits, separators — no spaces, no prose.</summary>
    private static readonly System.Text.RegularExpressions.Regex TokenValue =
        new("^[A-Za-z0-9_.:-]{1,64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    [Fact]
    public void Every_Emitted_Tag_Key_And_Value_Is_From_A_Closed_Vocabulary()
    {
        var measurements = new ConcurrentQueue<(string Instrument, KeyValuePair<string, object?>[] Tags)>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, publisher) =>
            {
                if (instrument.Meter.Name == EvidgateTelemetry.MeterName)
                {
                    publisher.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>(
            (instrument, _, tags, _) => measurements.Enqueue((instrument.Name, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>(
            (instrument, _, tags, _) => measurements.Enqueue((instrument.Name, tags.ToArray())));
        listener.Start();

        RecordEverything();

        var recorded = measurements.ToArray();
        Assert.True(recorded.Length >= 12, $"only {recorded.Length} measurements were captured — the probe is not alive");

        foreach (var (instrument, tags) in recorded)
        {
            foreach (var tag in tags)
            {
                Assert.True(
                    AllowedTagKeys.Contains(tag.Key),
                    $"{instrument} emitted a tag key outside the closed set: {tag.Key}");

                var key = tag.Key.ToLowerInvariant();
                Assert.DoesNotContain(
                    IdentifierKeyMarkers,
                    marker => key.Contains(marker, StringComparison.Ordinal));

                var value = tag.Value?.ToString() ?? string.Empty;
                Assert.Matches(TokenValue, value);
            }
        }

        // Every instrument's tag key showed up, so the allowlist is not passing by omission.
        var observedKeys = recorded
            .SelectMany(measurement => measurement.Tags)
            .Select(tag => tag.Key)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(EvidgateTelemetry.ModuleTag, observedKeys);
        Assert.Contains(EvidgateTelemetry.PipelineMessageTag, observedKeys);
        Assert.Contains("reason", observedKeys);
        Assert.Contains("code", observedKeys);
        Assert.Contains("normalized_status", observedKeys);
        Assert.Contains("provider", observedKeys);
        Assert.Contains("outcome", observedKeys);
        Assert.Contains("trigger", observedKeys);
        Assert.Contains("written", observedKeys);
    }

    /// <summary>Calls every public recording method once, with closed-vocabulary arguments.</summary>
    private static void RecordEverything()
    {
        using (EvidgateTelemetry.StartModuleActivity("ingestion", "CreateRun"))
        {
        }

        EvidgateTelemetry.RecordManifestRejected("schema");
        EvidgateTelemetry.RecordAdapterCapabilityRejected("capability_invalid", 3);
        EvidgateTelemetry.RecordAdapterCapabilityRejected("unsupported_protocol_major", null);
        EvidgateTelemetry.RecordIngestBatchRejected("event_count");
        EvidgateTelemetry.RecordCaseAttemptWritten("accepted", 2);
        EvidgateTelemetry.RecordCaseAttemptBlockedBySetup(1);
        EvidgateTelemetry.RecordFinalizeProducerSlotTaken();
        EvidgateTelemetry.RecordFinalizeDeclarationMismatch();
        EvidgateTelemetry.RecordLoginAttempt("google", "started");
        EvidgateTelemetry.RecordSessionAuthOutcome("active");
        EvidgateTelemetry.RecordSessionRotated("login");
        EvidgateTelemetry.RecordSessionRevoked(2);
        EvidgateTelemetry.RecordSessionSlideWrite(true);
        EvidgateTelemetry.RecordPipelineDuration("CreateRun", TimeSpan.FromMilliseconds(5), error: false);
    }
}
