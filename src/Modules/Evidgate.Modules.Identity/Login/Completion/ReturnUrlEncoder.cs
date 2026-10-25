using System.Text;

namespace Evidgate.Modules.Identity.Login.Completion;

/// <summary>
/// Turns the allowlisted <c>returnUrl</c> into a <c>Location</c> header value (02.3.1 §5, hand-over (vi) of
/// #160): <see cref="ReturnUrlPolicy"/> accepts non-ASCII path characters, and a header may not carry them,
/// so every character outside the RFC 3986 path/query set is percent-encoded as UTF-8.
/// </summary>
/// <remarks>
/// <para>
/// <b>No double encoding.</b> <c>%</c> is kept literally, because the value the allowlist approved is exactly
/// what a same-origin browser resolves: encoding it again would turn <c>/runs/a%20b</c> into
/// <c>/runs/a%2520b</c> and land the user on a 404. The allowlist has already proven every decoding form of
/// the value is an app path (<see cref="ReturnUrlPolicy.MaxDecodePasses"/>), so keeping the client's escapes is
/// safe, and nothing here can introduce <c>//</c>, a backslash, a control character or whitespace that was not
/// there.
/// </para>
/// <para>
/// <b>Fail closed.</b> The encoded value is run through <see cref="ReturnUrlPolicy.IsAppPath"/> again (an
/// encoding can lengthen the value past the allowlist's bound), and a refusal is the app root — never a throw,
/// because this is the last step before the browser is redirected and a failure here would be a 500 after a
/// login that actually succeeded.
/// </para>
/// </remarks>
internal static class ReturnUrlEncoder
{
    /// <summary>
    /// Characters kept literally: RFC 3986 unreserved, sub-delims, the <c>:@</c> pair a path segment may carry,
    /// the path/query/fragment delimiters <c>/?#</c>, and <c>%</c> for an escape the client already wrote.
    /// </summary>
    private const string Literal = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~!$&'()*+,;=:@/?#%";

    /// <summary>The ASCII escape prefix, and the hex digits of the <c>%XX</c> triplet.</summary>
    private const char EscapePrefix = '%';

    private const string HexDigits = "0123456789ABCDEF";

    /// <summary>
    /// The <c>Location</c> value for <paramref name="returnUrl"/>: the allowlisted path with every unsafe
    /// character percent-encoded, or <see cref="ReturnUrlPolicy.Root"/> when the result is no longer an app path.
    /// </summary>
    /// <param name="returnUrl">The value <see cref="ReturnUrlPolicy.Resolve"/> approved.</param>
    public static string ToLocation(string returnUrl)
    {
        ArgumentNullException.ThrowIfNull(returnUrl);

        var encoded = PercentEncode(returnUrl);

        return ReturnUrlPolicy.IsAppPath(encoded) ? encoded : ReturnUrlPolicy.Root;
    }

    /// <summary>
    /// <paramref name="value"/> with every byte that may not appear in a header, and every byte of a non-ASCII
    /// character, written as <c>%XX</c> (upper-case hex), ASCII characters outside <see cref="Literal"/> too.
    /// </summary>
    private static string PercentEncode(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var octet in Encoding.UTF8.GetBytes(value))
        {
            var character = (char)octet;

            if (octet < 0x80 && Literal.Contains(character, StringComparison.Ordinal))
            {
                builder.Append(character);

                continue;
            }

            builder.Append(EscapePrefix).Append(HexDigits[octet >> 4]).Append(HexDigits[octet & 0x0F]);
        }

        return builder.ToString();
    }
}
