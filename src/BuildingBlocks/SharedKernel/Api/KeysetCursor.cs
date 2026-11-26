using System.Globalization;

namespace Evidgate.BuildingBlocks.SharedKernel.Api;

/// <summary>
/// The opaque cursor of the keyset-paged lists (00 §9.1: <c>GET /v1/roles</c>,
/// <c>/v1/orgs/{o}/memberships</c>, <c>/v1/orgs/{o}/projects</c>): the keyset pair
/// <c>(created_at, id)</c> of the page's last row, encoded as
/// <c>&lt;utc ticks in hex&gt;-&lt;uuid&gt;</c>. It is opaque to clients — no
/// meaning is promised beyond "hand it back verbatim" — and it decodes to typed
/// values so the SQL never compares a client string against a timestamp.
/// <para>
/// The <c>d.</c> marker distinguishes issued newest-first cursors from ascending cursors. It is required by
/// <see cref="TryDecodeDescending"/> and is not a hexadecimal tick value accepted by <see cref="TryDecode"/>.
/// </para>
/// </summary>
public static class KeysetCursor
{
    private const string DescendingMarker = "d.";

    /// <summary>Encodes one row's keyset position.</summary>
    public static string Encode(DateTimeOffset createdAt, Guid id) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{createdAt.UtcTicks:x16}-{id:D}");

    /// <summary>Encodes one row's keyset position for a list read in descending order.</summary>
    public static string EncodeDescending(DateTimeOffset createdAt, Guid id) =>
        DescendingMarker + Encode(createdAt, id);

    /// <summary>
    /// Requires the descending marker, then parses the remaining position with <see cref="TryDecode"/>.
    /// </summary>
    public static bool TryDecodeDescending(string? cursor, out DateTimeOffset createdAt, out Guid id)
    {
        if (cursor is not null && cursor.StartsWith(DescendingMarker, StringComparison.Ordinal))
        {
            return TryDecode(cursor[DescendingMarker.Length..], out createdAt, out id);
        }

        createdAt = default;
        id = default;
        return false;
    }

    /// <summary>
    /// Parses hexadecimal UTC ticks in the timestamp range and a GUID, separated by the first hyphen.
    /// The parsed values need not have the exact spelling emitted by <see cref="Encode"/>.
    /// </summary>
    public static bool TryDecode(string? cursor, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default;
        id = default;

        if (string.IsNullOrEmpty(cursor))
        {
            return false;
        }

        var separator = cursor.IndexOf('-', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        if (!long.TryParse(
                cursor.AsSpan(0, separator),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var ticks)
            || ticks < 0
            || ticks > DateTimeOffset.MaxValue.UtcTicks
            || !Guid.TryParse(cursor.AsSpan(separator + 1), out id))
        {
            return false;
        }

        createdAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }
}
