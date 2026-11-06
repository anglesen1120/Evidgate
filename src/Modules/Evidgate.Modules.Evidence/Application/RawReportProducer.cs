using System.Globalization;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The synthetic producer identity a raw report's events are written under. The artifact id makes it unique per
/// report artifact, so one artifact's events never collide with another's sequence, and the comparison read can select
/// the events parsed from that artifact (two parser releases of one artifact share them).
/// </summary>
internal static class RawReportProducer
{
    /// <summary>The prefix that marks a producer as this module's report parse rather than a producer's own shard.</summary>
    private const string Prefix = "junit-";

    /// <summary>The producer id of the events one report artifact's parse writes.</summary>
    /// <param name="artifactId">The report artifact.</param>
    internal static string IdFor(Guid artifactId) =>
        Prefix + artifactId.ToString("D", CultureInfo.InvariantCulture);
}
