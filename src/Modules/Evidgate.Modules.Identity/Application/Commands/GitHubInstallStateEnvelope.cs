using System.Security.Cryptography;
using System.Text;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>
/// The <c>&lt;org&gt;.&lt;nonce&gt;</c> envelope the start route writes into the GitHub <c>state</c> and the browser
/// hands back to the callback: the one place its shape, the org it names and the digest a state row stores are
/// defined, shared by the consume command and the completion that re-derives the digest after a successful consume.
/// </summary>
internal static class GitHubInstallStateEnvelope
{
    /// <summary>The longest state value the callback accepts.</summary>
    internal const int MaxStateLength = 256;

    /// <summary>The longest nonce the state may carry; the start route writes 64 hex characters.</summary>
    internal const int MaxNonceLength = 128;

    /// <summary>The length of the SHA-256 digest the state row stores (32 bytes).</summary>
    internal const int DigestLength = 32;

    /// <summary>Reads an envelope; false for anything the start route could not have written.</summary>
    /// <param name="state">The untrusted <c>state</c> query value.</param>
    /// <param name="orgId">The org the state names, when the shape is valid.</param>
    /// <param name="nonce">The nonce only, when the shape is valid.</param>
    /// <returns>Whether <paramref name="state"/> is a well-formed envelope.</returns>
    internal static bool TryParse(string? state, out Guid orgId, out string nonce)
    {
        orgId = Guid.Empty;
        nonce = string.Empty;

        if (state is not { Length: > 0 and <= MaxStateLength })
        {
            return false;
        }

        var separator = state.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        var prefix = state.AsSpan(0, separator);
        if (prefix.Length != 36 || !Guid.TryParseExact(prefix, "D", out orgId))
        {
            return false;
        }

        var rest = state.AsSpan(separator + 1);
        if (rest.IsEmpty || rest.Length > MaxNonceLength)
        {
            return false;
        }

        foreach (var character in rest)
        {
            if (character <= ' ' || character > '~')
            {
                return false;
            }
        }

        nonce = rest.ToString();
        return true;
    }

    /// <summary>The digest a state row stores: SHA-256 over the nonce alone, never over the envelope.</summary>
    /// <param name="nonce">The nonce of a parsed envelope.</param>
    /// <returns>The 32-byte digest.</returns>
    internal static byte[] Digest(string nonce) => SHA256.HashData(Encoding.ASCII.GetBytes(nonce));
}
