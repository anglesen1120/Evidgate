using System.ComponentModel.DataAnnotations;

namespace Evidgate.BuildingBlocks.Observability;

/// <summary>
/// Telemetry options, bound from <c>EVIDGATE__TELEMETRY__*</c> (01.1.2-T1.2, 10.3.1-T1.3).
/// The OTLP exporter is registered only when an endpoint is configured, so local
/// runs and tests never try to reach a collector (00 §11: no hard dependency on
/// the observability backend).
/// </summary>
public sealed class TelemetryOptions : IValidatableObject
{
    /// <summary>Configuration section bound to <c>EVIDGATE__TELEMETRY__*</c>.</summary>
    public const string SectionName = "EVIDGATE:Telemetry";

    /// <summary>
    /// The head-sampling ratio in effect when <c>EVIDGATE__TELEMETRY__SAMPLERATIO</c> is
    /// unset (10.3.1-T1.3): keep every trace. Loss of telemetry is never silent — a
    /// deployment that wants less volume has to say so — and it matches the browser
    /// runtime's default (<c>evidgate-telemetry-sample-ratio</c> unset ⇒ 1).
    /// </summary>
    public const double DefaultSampleRatio = 1d;

    /// <summary>OTLP/gRPC endpoint, e.g. <c>http://localhost:4317</c>; unset disables the exporter.</summary>
    [Url(ErrorMessage = "EVIDGATE__TELEMETRY__OTLPENDPOINT must be an absolute URL, e.g. http://localhost:4317")]
    public string? OtlpEndpoint { get; init; }

    /// <summary>
    /// Head-sampling ratio applied to root traces (10.3.1-T1.3): <c>1</c> keeps every
    /// trace, <c>0</c> keeps only error spans. Validated to the closed interval
    /// <c>[0, 1]</c> — a value outside it fails host start with the key named, the same
    /// clean path as an invalid endpoint — so a typo can neither sample everything nor
    /// turn telemetry off silently. Error spans are exported whatever this ratio says
    /// (<see cref="TelemetrySampling"/>), and metrics are aggregates, never head-sampled.
    /// </summary>
    public double SampleRatio { get; init; } = DefaultSampleRatio;

    /// <summary>
    /// The <c>service.version</c> resource attribute (10.3.1-T1.3). Unset means the entry
    /// assembly's informational version — in this repository the SDK default, because no build
    /// sets <c>Version</c> or <c>SourceRevisionId</c>, so it does not tell two commits apart.
    /// A container deploy sets this key to the image tag or digest, which is what the backend
    /// needs to join an error to a build.
    /// </summary>
    public string? ServiceVersion { get; init; }

    /// <summary>
    /// Whether the host subscribes to the libraries' own meters (the .NET runtime, Npgsql and, in the API,
    /// ASP.NET Core hosting) that the operations catalogue reports. On by default. Every live meter provider
    /// receives every measurement those libraries make, so a process that builds many hosts (the integration
    /// suites) switches it off with <c>EVIDGATE__TELEMETRY__LIBRARYMETRICS=false</c> instead of paying for
    /// metrics nobody scrapes.
    /// </summary>
    public bool LibraryMetrics { get; init; } = true;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrWhiteSpace(OtlpEndpoint)
            && (!Uri.TryCreate(OtlpEndpoint, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            yield return new ValidationResult(
                "EVIDGATE__TELEMETRY__OTLPENDPOINT must be an absolute http(s) URL, e.g. http://localhost:4317",
                [nameof(OtlpEndpoint)]);
        }

        if (double.IsNaN(SampleRatio) || double.IsInfinity(SampleRatio) || SampleRatio < 0d || SampleRatio > 1d)
        {
            yield return new ValidationResult(
                "EVIDGATE__TELEMETRY__SAMPLERATIO must be a decimal fraction between 0 and 1 "
                    + "(0 keeps only error spans, 1 keeps every trace)",
                [nameof(SampleRatio)]);
        }

        if (ServiceVersion is not null && string.IsNullOrWhiteSpace(ServiceVersion))
        {
            yield return new ValidationResult(
                "EVIDGATE__TELEMETRY__SERVICEVERSION must not be blank when it is set; remove the key to use the assembly version",
                [nameof(ServiceVersion)]);
        }
    }
}
