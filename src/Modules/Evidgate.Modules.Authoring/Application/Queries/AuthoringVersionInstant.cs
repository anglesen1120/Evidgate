using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Domain;

namespace Evidgate.Modules.Authoring.Application.Queries;

/// <summary>
/// The RFC 3339 instant reader of route 22's <c>from</c>/<c>to</c>: an absent value reads as null, anything that is not a
/// date-time with an explicit offset is a 422 naming the query parameter (never an echoed value), and an accepted value
/// is normalised to UTC.
/// </summary>
/// <remarks>
/// <b>Always UTC, never the caller's offset.</b> <c>authoring_version.created_at</c> is <c>timestamptz</c>, which stores
/// an instant and no zone, and Npgsql refuses to write a <see cref="DateTimeOffset"/> whose offset is not zero to that
/// type: a bound of <c>2026-10-01T10:00:00+07:00</c> would be a write error instead of a page. The reader therefore keeps
/// the caller's instant and drops its spelling (<see cref="DateTimeOffset.ToUniversalTime"/>), so <c>+07:00</c>,
/// <c>-05:30</c> and <c>Z</c> are the same window.
/// </remarks>
internal static class AuthoringVersionInstant
{
    /// <summary>The longest instant this reader accepts (RFC 3339 allows 10 fractional digits; 40 covers them).</summary>
    private const int MaximumLength = 40;

    /// <summary>Reads one bound; absent → null with no error.</summary>
    /// <param name="raw">The raw query value, or null.</param>
    /// <param name="path">The query parameter's pointer, e.g. <c>/from</c>.</param>
    /// <param name="instant">The instant on success.</param>
    internal static Error? Read(string? raw, string path, out DateTimeOffset? instant)
    {
        instant = null;

        if (raw is null)
        {
            return null;
        }

        if (!TryParse(raw, out var parsed))
        {
            return Error.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"{path[1..]} must be an RFC 3339 instant with an explicit offset, e.g. 2026-10-01T10:00:00Z"),
                [new ErrorDetail(path, "value_invalid")]);
        }

        // UTC, not the caller's spelling: Npgsql binds a DateTimeOffset with a non-zero offset to `timestamptz` as a
        // write error, so the offset is dropped here and only the instant survives.
        instant = parsed.ToUniversalTime();

        return null;
    }

    /// <summary>
    /// Parses an RFC 3339 date-time: a <c>T</c> separator, a time with hours, minutes and seconds, and a <c>Z</c> or a
    /// <c>±hh:mm</c> offset. A bare date, a local time without an offset and any other spelling are refused, because an
    /// instant without an offset would silently be read in the server's zone.
    /// </summary>
    /// <param name="raw">The raw value.</param>
    /// <param name="instant">The parsed instant on success.</param>
    private static bool TryParse(string raw, out DateTimeOffset instant)
    {
        instant = default;

        if (raw.Length is 0 or > MaximumLength || AuthoringText.ContainsNul(raw))
        {
            return false;
        }

        var separator = raw.IndexOf('T', StringComparison.Ordinal);

        if (separator < 0)
        {
            separator = raw.IndexOf('t', StringComparison.Ordinal);
        }

        if (separator <= 0)
        {
            return false;
        }

        var time = raw[separator..];
        var seconds = time.Count(character => character == ':');

        if (seconds < 2 || !HasOffset(raw))
        {
            return false;
        }

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out instant);
    }

    /// <summary>True when the value ends with <c>Z</c>/<c>z</c> or a <c>±hh:mm</c> offset.</summary>
    /// <param name="raw">The raw value.</param>
    private static bool HasOffset(string raw)
    {
        if (raw[^1] is 'Z' or 'z')
        {
            return true;
        }

        return raw.Length > 6
            && raw[^3] == ':'
            && raw[^6] is '+' or '-'
            && char.IsAsciiDigit(raw[^5])
            && char.IsAsciiDigit(raw[^4])
            && char.IsAsciiDigit(raw[^2])
            && char.IsAsciiDigit(raw[^1]);
    }
}
