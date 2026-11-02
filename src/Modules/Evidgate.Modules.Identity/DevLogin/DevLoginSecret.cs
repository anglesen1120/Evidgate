using System.Security.Cryptography;
using System.Text;

namespace Evidgate.Modules.Identity.DevLogin;

/// <summary>
/// The constant-time comparison behind <c>POST /v1/dev/session</c>: both the configured secret and the presented
/// one are reduced to a SHA-256 digest and compared with <see cref="CryptographicOperations.FixedTimeEquals"/>.
/// </summary>
/// <remarks>
/// Hashing first is what makes the comparison independent of the presented length: the digests are always 32
/// bytes, so neither the timing nor the branch depends on how many characters a caller sent. The secret itself
/// is never logged, echoed in a body or stored — only its digest exists for the duration of the call.
/// </remarks>
internal static class DevLoginSecret
{
    /// <summary>Whether <paramref name="presented"/> is the configured <paramref name="configured"/> secret.</summary>
    /// <param name="configured">The secret the deployment provisioned; null or blank never matches.</param>
    /// <param name="presented">The value the request body carried; null or blank never matches.</param>
    internal static bool Matches(string? configured, string? presented)
    {
        if (string.IsNullOrWhiteSpace(configured) || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[32];
        Span<byte> actual = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(configured), expected);
        SHA256.HashData(Encoding.UTF8.GetBytes(presented), actual);

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
