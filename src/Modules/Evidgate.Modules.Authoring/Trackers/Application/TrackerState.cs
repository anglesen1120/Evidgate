using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Evidgate.Modules.Authoring.Trackers.Application;

/// <summary>
/// The consent <c>state</c>: <c>&lt;org&gt;.&lt;32 random bytes, base64url&gt;</c>. The org prefix is what lets the callback,
/// which the browser opens with a session and no org header, resolve its tenant; the caller's membership of that org is
/// proven by the tenant resolution that follows, never by this parse. Only the SHA-256 of the whole value is stored.
/// </summary>
internal static class TrackerState
{
    /// <summary>The longest <c>state</c> the callback accepts; a longer one is refused before any lookup.</summary>
    internal const int MaxStateLength = 256;

    /// <summary>The longest authorization code the callback accepts.</summary>
    internal const int MaxCodeLength = 512;

    /// <summary>The longest provider <c>error</c> value the callback reads.</summary>
    internal const int MaxErrorLength = 128;

    private const int OrgLength = 36;

    /// <summary>A fresh state for <paramref name="orgId"/>.</summary>
    internal static string New(Guid orgId) =>
        string.Create(CultureInfo.InvariantCulture, $"{orgId:D}.{Random()}");

    /// <summary>A fresh PKCE verifier (43 characters, RFC 7636).</summary>
    internal static string NewVerifier() => Random();

    /// <summary>The org a state names, or null when the value is not of the shape <see cref="New"/> writes.</summary>
    /// <param name="state">The untrusted <c>state</c> query value.</param>
    internal static Guid? Org(string? state) =>
        state is { Length: > OrgLength + 1 and <= MaxStateLength }
        && state[OrgLength] == '.'
        && Guid.TryParseExact(state.AsSpan(0, OrgLength), "D", out var org)
            ? org
            : null;

    /// <summary>The SHA-256 of the state, the only form of it that is stored.</summary>
    internal static byte[] Hash(string state) => SHA256.HashData(Encoding.UTF8.GetBytes(state));

    private static string Random() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
}
