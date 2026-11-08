using System.Numerics;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The one order a parser release is placed by, shared by the registry that resolves a producer kind to a release and
/// the reprocess route that compares one against a stored report: a version is a run of dotted decimal segments
/// compared segment by segment, and a shorter version is the longer one padded with zeros (<c>1.0</c> equals
/// <c>1.0.0</c>), because text order would put <c>1.10</c> behind <c>1.9</c>. Only a strictly greater release is a
/// reprocess — the same release wrote the stored row, and an older one would replace a newer report with what that
/// release knew at the time.
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
    internal static bool IsNewer(string? requested, string? source) => Compare(requested, source) is > 0;

    /// <summary>
    /// The dotted numeric order itself: negative when <paramref name="left"/> is the older release, zero when both
    /// name the same release (<c>1.10</c> equals <c>1.10.0</c>), positive when it is the newer — and
    /// <see langword="null"/> when either side is unorderable, because a caller must decide what an unplaceable
    /// release means instead of receiving a guessed order.
    /// </summary>
    /// <param name="left">One version.</param>
    /// <param name="right">The version to order it against.</param>
    internal static int? Compare(string? left, string? right)
    {
        if (!TryParse(left, out var leftSegments) || !TryParse(right, out var rightSegments))
        {
            return null;
        }

        return CompareSegments(leftSegments, rightSegments);
    }

    /// <summary>
    /// The same order over versions already split by <see cref="TryParse"/>, so a caller ranking many releases parses
    /// each label once instead of once per comparison.
    /// </summary>
    /// <param name="left">The segments of one version.</param>
    /// <param name="right">The segments of the version to order it against.</param>
    internal static int CompareSegments(IReadOnlyList<BigInteger> left, IReadOnlyList<BigInteger> right)
    {
        var length = Math.Max(left.Count, right.Count);

        for (var index = 0; index < length; index++)
        {
            var leftSegment = index < left.Count ? left[index] : BigInteger.Zero;
            var rightSegment = index < right.Count ? right[index] : BigInteger.Zero;

            if (leftSegment != rightSegment)
            {
                return leftSegment > rightSegment ? 1 : -1;
            }
        }

        return 0;
    }

    /// <summary>
    /// True when the version splits into numeric segments, so the dotted order can place it at all. A caller that
    /// ranks releases asks this before comparing: an unorderable label is never given a position among them.
    /// </summary>
    /// <param name="version">The version to inspect.</param>
    internal static bool IsOrderable(string? version) => TryParse(version, out _);

    /// <summary>
    /// Splits a version into its numeric segments. A null, empty, blank or segment-less version, an empty segment
    /// (<c>1..2</c>) and any non-digit character make the whole version unorderable, so nothing is compared.
    /// </summary>
    internal static bool TryParse(string? version, out BigInteger[] segments)
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
