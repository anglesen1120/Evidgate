using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Evidgate.BuildingBlocks.Observability;

/// <summary>
/// The app-side head-sampling policy of 10.3.1-T1.3: <see cref="Sampler"/> composition
/// for traces and the processor that carries the error-retention rule.
/// </summary>
/// <remarks>
/// <para>
/// The head decision is <c>ParentBased(TraceIdRatioBased(sampleRatio))</c> as the task
/// asks: a root trace is kept with the configured probability <em>by trace id</em>, so
/// every service in the request path — and every retry of the same trace — reaches the
/// same decision, and a child never contradicts its parent.
/// </para>
/// <para>
/// "Errors are always kept" changes what a <em>dropped</em> decision records, not what it
/// decides: <see cref="RecordWhenNotSampledSampler"/> turns the sampler's
/// <see cref="SamplingDecision.Drop"/> into <see cref="SamplingDecision.RecordOnly"/> for
/// the root span, and the not-sampled parent branches of <see cref="ParentBasedSampler"/>
/// do the same for their children. Such a span is fully recorded locally — tags, events,
/// and the error status a handler sets later — and only its propagation is unsampled.
/// </para>
/// <para>
/// At span end, <see cref="ErrorRetainingProcessor"/> sets the W3C recorded flag on an
/// error span. That is the OTel .NET tail-sampling recipe, and it is required rather than
/// cosmetic: both of the SDK's export processors skip a span whose
/// <see cref="Activity.Recorded"/> is false, so an unsampled error span reaches the
/// exporter only once this processor has marked it. The processor is registered before the
/// exporter; a non-error span of a dropped trace is skipped and never leaves the process.
/// </para>
/// <para>
/// Cost and limits, both deliberate: instrumentation work is <em>not</em> skipped for a
/// dropped trace (that is the price of keeping errors), so a low ratio saves backend volume
/// and network, not local CPU; and because retention is per span, an error in a dropped
/// trace is exported on its own — its non-error parent and sibling spans are already gone.
/// The exported span also carries the recorded flag the processor set, while its
/// propagation to other services said "not sampled": a consumer that re-applies the parent
/// decision (as our own sampler does) still drops the rest of that trace. A collector
/// tail-sampler (10.3.1-T2) is what reconstructs whole error traces; this policy is the
/// app-side floor that works with no collector configuration at all.
/// </para>
/// </remarks>
public static class TelemetrySampling
{
    /// <summary>
    /// Builds the sampler for one host: ratio head sampling, parent-respecting, recording
    /// (but not exporting) a trace the ratio drops so an error span inside it still reaches
    /// the exporter.
    /// </summary>
    /// <param name="sampleRatio">The configured ratio; <c>[0, 1]</c>, validated by <see cref="TelemetryOptions"/> before the host starts.</param>
    /// <exception cref="ArgumentOutOfRangeException">The ratio is not a finite value in <c>[0, 1]</c>.</exception>
    public static Sampler CreateSampler(double sampleRatio)
    {
        if (double.IsNaN(sampleRatio) || double.IsInfinity(sampleRatio) || sampleRatio < 0d || sampleRatio > 1d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRatio),
                sampleRatio,
                "EVIDGATE__TELEMETRY__SAMPLERATIO must be a decimal fraction between 0 and 1.");
        }

        return new ParentBasedSampler(
            new RecordWhenNotSampledSampler(new TraceIdRatioBasedSampler(sampleRatio)),
            new AlwaysOnSampler(),
            AlwaysRecordSampler.Instance,
            new AlwaysOnSampler(),
            AlwaysRecordSampler.Instance);
    }

    /// <summary>Records the root span of a trace the ratio drops, instead of not creating it at all.</summary>
    private sealed class RecordWhenNotSampledSampler(Sampler head) : Sampler
    {
        public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
        {
            var decision = head.ShouldSample(samplingParameters);

            return decision.Decision == SamplingDecision.Drop
                ? new SamplingResult(SamplingDecision.RecordOnly)
                : decision;
        }
    }

    /// <summary>Records every child of a parent that is not sampled, so its error spans still exist.</summary>
    private sealed class AlwaysRecordSampler : Sampler
    {
        internal static readonly AlwaysRecordSampler Instance = new();

        public override SamplingResult ShouldSample(in SamplingParameters samplingParameters) =>
            new(SamplingDecision.RecordOnly);
    }
}

/// <summary>
/// Keeps the error spans of a head-sampled-out trace (10.3.1-T1.3, "errors always kept"):
/// on span end it sets the W3C recorded flag when the status is
/// <see cref="ActivityStatusCode.Error"/>, which is the only signal the SDK's export
/// processors (batch and simple alike) look at before they hand a span to an exporter.
/// </summary>
/// <remarks>
/// Registered before the export processor (see <see cref="ObservabilityExtensions"/>). A span
/// the ratio kept already carries the flag and is untouched; a non-error span of a dropped
/// trace keeps the flag off and is dropped by the export processor for free, which is why
/// this processor has no exporter side to maintain.
/// </remarks>
public sealed class ErrorRetainingProcessor : BaseProcessor<Activity>
{
    /// <inheritdoc />
    public override void OnEnd(Activity activity)
    {
        if (activity is not null && activity.Status == ActivityStatusCode.Error)
        {
            activity.ActivityTraceFlags |= ActivityTraceFlags.Recorded;
        }
    }
}
