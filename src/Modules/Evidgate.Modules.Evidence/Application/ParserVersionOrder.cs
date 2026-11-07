using System.Numerics;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The order a reprocess asks a parser release to match: a version is a run of dotted decimal segments compared
/// segment by segment, and a shorter version is the longer one padded with zeros (<c>1.0</c> equals <c>1.0.0</c>).
/// Only a strictly greater release is a reprocess — the same release wrote the stored row, and an older one would
/// replace a newer report with what that release knew at the time.
/// <para>
/// A version with a non-numeric segment (<c>1.0.0-rc.1</c>, <c>1.0.0+build</c>, an empty segment) is unorderable:
/// pre-release and build metadata have their own precedence rules, and guessing one here would let a release the
/// catalogue cannot rank replace a report. Such a version never counts as newer, so the caller refuses it with the
/// same code an unknown version gets. Segments are <see cref="BigInteger"/> because a valid version may be longer
/// than any fixed integer and an overflow would silently reorder two releases.
/// </para>
/// </summary>
internal static class ParserVersionOrder
{
    private static readonly char SegmentSeparator = '.';

    /// <summary>
    /// True when <paramref name="requested"/> is a strictly greater release than <paramref name="source"/> under the
    /// dotted numeric order, and false when either side is unorderable or the two are equal.
    /// </summary>
    /// <param name="requested">The version the caller asked to reprocess with.</param>
    /// <param name="source">The version of the report being reprocessed.</param>
    internal static bool IsNewer(string? requested, string? source)
    {
        if (!TryParse(requested, out var requestedSegments) || !TryParse(source, out var sourceSegments))
        {
            return false;
        }

        var length = Math.Max(requestedSegments.Length, sourceSegments.Length);

        for (var index = 0; index < length; index++)
        {
            var left = index < requestedSegments.Length ? requestedSegments[index] : BigInteger.Zero;
            var right = index < sourceSegments.Length ? sourceSegments[index] : BigInteger.Zero;

            if (left != right)
            {
                return left > right;
            }
        }

        return false;
    }

    /// <summary>
    /// Splits a version into its numeric segments. A null, empty, blank or segment-less version, an empty segment
    /// (<c>1..2</c>) and any non-digit character make the whole version unorderable, so nothing is compared.
    /// </summary>
    private static bool TryParse(string? version, out BigInteger[] segments)
    {
        segments = [];

        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var parts = version.Split(SegmentSeparator);
        var parsed = new BigInteger[parts.Length];

        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];

            if (part.Length == 0 || !part.All(char.IsAsciiDigit))
            {
                return false;
            }

            parsed[index] = BigInteger.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
        }

        segments = parsed;
        return true;
    }
}
