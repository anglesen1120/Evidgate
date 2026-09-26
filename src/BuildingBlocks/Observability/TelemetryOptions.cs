using System.ComponentModel.DataAnnotations;

namespace Evidgate.BuildingBlocks.Observability;

/// <summary>
/// Telemetry options, bound from <c>EVIDGATE__TELEMETRY__*</c> (01.1.2-T1.2).
/// The OTLP exporter is registered only when an endpoint is configured, so local
/// runs and tests never try to reach a collector (00 §11: no hard dependency on
/// the observability backend).
/// </summary>
public sealed class TelemetryOptions : IValidatableObject
{
    /// <summary>Configuration section bound to <c>EVIDGATE__TELEMETRY__*</c>.</summary>
    public const string SectionName = "EVIDGATE:Telemetry";

    /// <summary>OTLP/gRPC endpoint, e.g. <c>http://localhost:4317</c>; unset disables the exporter.</summary>
    [Url(ErrorMessage = "EVIDGATE__TELEMETRY__OTLPENDPOINT must be an absolute URL, e.g. http://localhost:4317")]
    public string? OtlpEndpoint { get; init; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(OtlpEndpoint))
        {
            yield break;
        }

        if (!Uri.TryCreate(OtlpEndpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            yield return new ValidationResult(
                "EVIDGATE__TELEMETRY__OTLPENDPOINT must be an absolute http(s) URL, e.g. http://localhost:4317",
                [nameof(OtlpEndpoint)]);
        }
    }
}
