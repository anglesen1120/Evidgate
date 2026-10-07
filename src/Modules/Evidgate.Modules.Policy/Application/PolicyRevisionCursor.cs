using System.Globalization;

namespace Evidgate.Modules.Policy.Application;

/// <summary>
/// The opaque cursor of <c>GET /v1/policies/{id}/revisions</c> (00 §9.1): the keyset pair
/// <c>(revision_no, id)</c> of the page's last row, encoded as <c>&lt;revision_no in hex&gt;-&lt;uuid&gt;</c>. It is
/// opaque to clients — no meaning is promised beyond "hand it back verbatim" — and it decodes to typed values so the
/// SQL never compares a client string against a number. The <c>KeysetCursor</c> shape, over the policy list's own
/// ordering key.
/// </summary>
internal static class PolicyRevisionCursor
{
    /// <summary>Encodes one row's keyset position.</summary>
    internal static string Encode(int revisionNo, Guid id) =>
        string.Create(CultureInfo.InvariantCulture, $"{revisionNo:x8}-{id:D}");

    /// <summary>
    /// Decodes a cursor. False for anything this codec did not produce — the caller answers 422 <c>request_invalid</c>
    /// rather than guessing a page position.
    /// </summary>
    internal static bool TryDecode(string? cursor, out int revisionNo, out Guid id)
    {
        revisionNo = default;
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

        if (!int.TryParse(
                cursor.AsSpan(0, separator),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out revisionNo)
            || revisionNo < 1
            || !Guid.TryParse(cursor.AsSpan(separator + 1), out id))
        {
            return false;
        }

        return true;
    }
}
