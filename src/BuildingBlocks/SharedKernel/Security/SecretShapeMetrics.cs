using System.Diagnostics.Metrics;

namespace Evidgate.BuildingBlocks.SharedKernel.Security;

/// <summary>The closed set of secret-shape patterns; the name is the only tag the timeout counter carries.</summary>
internal enum SecretPattern
{
    /// <summary>A JWT.</summary>
    Jwt,

    /// <summary>A URL with credentials.</summary>
    UrlCredentials,

    /// <summary>A bare hex run.</summary>
    HexRun,

    /// <summary>A random-looking run.</summary>
    RandomRun,

    /// <summary>A UUID.</summary>
    Uuid,
}

/// <summary>
/// The one counter for a secret-shape pattern that ran out of its match budget. It is recorded once per timed-out check,
/// with the pattern name only: the checked value never reaches a tag, a log line or an exception message.
/// </summary>
internal static class SecretShapeMetrics
{
    /// <summary>
    /// The application meter. The canonical name is <c>EvidgateTelemetry.MeterName</c>; this block does not reference
    /// Observability, so the value is repeated here and a test pins the two together.
    /// </summary>
    internal const string MeterName = "Evidgate";

    /// <summary>The counter's instrument name, as listed in the metrics catalogue.</summary>
    internal const string TimeoutCounterName = "evidgate_secret_shape_timeout_total";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Timeouts = Meter.CreateCounter<long>(
        TimeoutCounterName,
        unit: "{check}",
        description: "Secret-shape checks whose regex timed out and were answered as secret-shaped (fail closed), by pattern. Never labelled with the checked value.");

    /// <summary>Counts one timed-out check.</summary>
    /// <param name="pattern">The pattern that timed out.</param>
    internal static void RecordTimeout(SecretPattern pattern) =>
        Timeouts.Add(1, new KeyValuePair<string, object?>("pattern", PatternName(pattern)));

    private static string PatternName(SecretPattern pattern) => pattern switch
    {
        SecretPattern.Jwt => "jwt",
        SecretPattern.UrlCredentials => "url_credentials",
        SecretPattern.HexRun => "hex_run",
        SecretPattern.RandomRun => "random_run",
        _ => "uuid",
    };
}
