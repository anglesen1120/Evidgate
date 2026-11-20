using System.Globalization;
using System.Text;

namespace Evidgate.Modules.Authoring.Application.Queries;

/// <summary>
/// The opaque <c>cursor</c> of route 22: the <c>(created_at, id)</c> pair the keyset seeks past, encoded
/// <c>v1&lt;newline&gt;&lt;unix microseconds&gt;&lt;newline&gt;&lt;uuid&gt;</c> and Base64URL-encoded so a client cannot
/// read it as a timestamp and cannot be tempted to build one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Microsecond-exact by construction.</b> <c>authoring_version.created_at</c> is <c>timestamptz</c> (microseconds) and
/// a .NET tick is 100 ns, so the wire unit is the Unix <b>microsecond</b>: the codec divides by ten when it encodes (a
/// Postgres value is always a whole number of microseconds) and multiplies by ten when it decodes, and the parameter is
/// sent as a <see cref="DateTimeOffset"/>, which Npgsql binds as <c>timestamptz</c>. No row is
/// therefore skipped or repeated at a page boundary, even when several rows share one <c>created_at</c> and the pages
/// have size one.
/// </para>
/// <para>
/// <b>Strictly validated.</b> Only the canonical output of <see cref="Encode"/> decodes: the alphabet is Base64URL with
/// no padding, the payload is exactly three members, the microsecond half is canonical decimal digits inside the
/// <see cref="DateTimeOffset"/> range, the id is a non-empty lower-case "D" uuid, and the decode re-encodes the value and
/// compares it ordinally, so an appended byte, a re-padded string, an alternative Base64 spelling or a NUL (which is
/// outside the alphabet) is refused instead of guessed. A structurally valid cursor for another instant is not
/// distinguishable from one this route issued — it needs no secret, because the statement always carries
/// <c>org_id</c> and <c>project_id</c>, so a cursor can only ever address rows of the project the URL names.
/// </para>
/// </remarks>
internal static class AuthoringVersionCursor
{
    /// <summary>The cursor version tag, so a future format can be recognised instead of misread.</summary>
    private const string Prefix = "v1";

    /// <summary>The separator between the three members; a uuid "D" form and decimal digits cannot contain it.</summary>
    private const char Separator = '\n';

    /// <summary>The longest cursor this codec can produce (a long microseconds half and a uuid), with slack.</summary>
    private const int MaximumLength = 128;

    /// <summary>The ticks of the Unix epoch, the origin of the microsecond half.</summary>
    private static readonly long UnixEpochTicks = DateTime.UnixEpoch.Ticks;

    /// <summary>
    /// The timestamp of the first page's sentinel pair: later than any stored <c>clock_timestamp()</c>, so
    /// <c>(created_at, id) &lt; (sentinel, max uuid)</c> is true for every row while staying one searchable row
    /// comparison. It is a plain value, deliberately one second short of <see cref="DateTime.MaxValue"/> so Npgsql's
    /// <c>timestamptz</c> infinity mapping is never involved.
    /// </summary>
    internal static readonly DateTimeOffset FirstPageTimestamp =
        new(9999, 12, 31, 23, 59, 59, TimeSpan.Zero);

    /// <summary>The id of the first page's sentinel pair: the largest <c>uuid</c> in the column's own ordering.</summary>
    internal static readonly Guid FirstPageId = new("ffffffff-ffff-ffff-ffff-ffffffffffff");

    /// <summary>Encodes one row's keyset pair as the next page's cursor.</summary>
    /// <param name="createdAt">That row's <c>created_at</c>.</param>
    /// <param name="id">That row's id, the tiebreaker of the total order.</param>
    internal static string Encode(DateTimeOffset createdAt, Guid id) =>
        Encode(Microseconds(createdAt), id);

    /// <summary>
    /// Decodes a cursor, or answers false for anything this codec did not produce — see the type remarks for the list of
    /// refusals. A false answer is the route's 422 <c>/cursor</c> <c>malformed</c>, never a guessed page position.
    /// </summary>
    /// <param name="cursor">The raw query value.</param>
    /// <param name="createdAt">The decoded instant on success.</param>
    /// <param name="id">The decoded id on success.</param>
    internal static bool TryDecode(string? cursor, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = FirstPageTimestamp;
        id = FirstPageId;

        if (string.IsNullOrEmpty(cursor) || cursor.Length > MaximumLength)
        {
            return false;
        }

        foreach (var character in cursor)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                // Base64URL only: this is also what refuses a NUL, a '+', a '/' and the '=' padding.
                return false;
            }
        }

        var text = cursor.Replace('-', '+').Replace('_', '/');
        var padded = text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '=');

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return false;
        }

        var decoded = Encoding.UTF8.GetString(bytes);
        var members = decoded.Split(Separator);

        if (members.Length != 3
            || !string.Equals(members[0], Prefix, StringComparison.Ordinal)
            || !TryReadMicroseconds(members[1], out var microseconds)
            || !TryReadId(members[2], out id))
        {
            return false;
        }

        createdAt = new DateTimeOffset(UnixEpochTicks + (microseconds * 10), TimeSpan.Zero);

        // The canonical form is the only accepted one: re-encoding cannot change a legitimate cursor, and it refuses a
        // padded, re-spelled or appended variant that parsed by accident.
        return string.Equals(Encode(microseconds, id), cursor, StringComparison.Ordinal);
    }

    /// <summary>Encodes the two members.</summary>
    /// <param name="microseconds">Non-negative Unix microseconds, inside the <see cref="DateTimeOffset"/> range.</param>
    /// <param name="id">The row's id.</param>
    private static string Encode(long microseconds, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
                string.Create(CultureInfo.InvariantCulture, $"{Prefix}{Separator}{microseconds}{Separator}{id:D}")))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>The Unix microseconds of an instant, truncated to the database's precision (which is exact for a stored
    /// value).</summary>
    /// <param name="createdAt">The instant.</param>
    private static long Microseconds(DateTimeOffset createdAt) => (createdAt.UtcTicks - UnixEpochTicks) / 10;

    /// <summary>Reads the microseconds half: canonical decimal digits, non-negative, and inside the tick range.</summary>
    /// <param name="text">The member's text.</param>
    /// <param name="microseconds">The decoded value on success.</param>
    private static bool TryReadMicroseconds(string text, out long microseconds)
    {
        microseconds = 0;

        if (text.Length is 0 or > 19 || (text.Length > 1 && text[0] == '0'))
        {
            return false;
        }

        foreach (var character in text)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out microseconds) || microseconds < 0)
        {
            return false;
        }

        // The value must be representable: the tick range ends far below long.MaxValue, so a cursor from another unit
        // (nanoseconds, milliseconds) is refused here instead of overflowing the DateTime arithmetic.
        return microseconds <= (DateTime.MaxValue.Ticks - UnixEpochTicks) / 10;
    }

    /// <summary>Reads the id half: a non-empty uuid in its canonical lower-case "D" spelling.</summary>
    /// <param name="text">The member's text.</param>
    /// <param name="id">The decoded id on success.</param>
    private static bool TryReadId(string text, out Guid id)
    {
        id = Guid.Empty;

        if (!Guid.TryParse(text, out var parsed)
            || parsed == Guid.Empty
            || !string.Equals(parsed.ToString("D"), text, StringComparison.Ordinal))
        {
            return false;
        }

        id = parsed;

        return true;
    }
}
