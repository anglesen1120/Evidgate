using System.Diagnostics.Metrics;

namespace Evidgate.BuildingBlocks.Observability;

/// <summary>
/// The release-binding meter, kept beside the main telemetry file so neither grows past the file-size bound. The counter
/// carries no labels: a component key or a digest is build data, not a metric dimension.
/// </summary>
public static partial class EvidgateTelemetry
{
    /// <summary>Counter of release components a bind could not match, added per applied bind. No labels: never a key or a digest.</summary>
    public const string ReleaseBindMismatchMetricName = "evidgate_release_bind_mismatch_total";

    private static readonly Counter<long> ReleaseBindMismatch = Meter.CreateCounter<long>(
        ReleaseBindMismatchMetricName,
        unit: "{component}",
        description: "Release components a bind could not match. No labels: never a component key, a digest or an id.");
    /// <summary>
    /// Counts the release components a bind could not match (<c>digest_mismatch</c> or <c>component_missing</c>), added
    /// once per applied bind. No labels: the component keys and digests themselves are never recorded.
    /// </summary>
    /// <param name="count">The mismatched components; zero or less is a no-op.</param>
    public static void RecordReleaseBindMismatch(long count)
    {
        if (count <= 0)
        {
            return;
        }

        ReleaseBindMismatch.Add(count);
    }
}
