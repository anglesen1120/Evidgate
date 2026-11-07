using System.Security.Cryptography;

namespace Evidgate.Modules.Identity.Tokens;

/// <summary>
/// The text every project credential shares: a public 12-character prefix and a 256-bit
/// base64url secret. One implementation, so the CI token and the automation token cannot diverge
/// in entropy, alphabet or encoding — a second copy is how two formats quietly drift apart.
/// </summary>
internal static class SecretTokenFormat
{
    /// <summary>Characters in the public lookup prefix.</summary>
    internal const int PrefixLength = 12;

    /// <summary>Entropy of the secret half, in bytes.</summary>
    internal const int SecretBytes = 32;

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>A fresh prefix drawn from the CSPRNG, unfilled and with no allocation of its own.</summary>
    internal static string NewPrefix() =>
        string.Create(PrefixLength, 0, static (span, _) =>
        {
            Span<byte> random = stackalloc byte[PrefixLength];
            RandomNumberGenerator.Fill(random);

            for (var index = 0; index < span.Length; index++)
            {
                var value = random[index];

                // 62 symbols from a byte: the biased tail (>= 248) is re-drawn instead of folded in.
                while (value >= 248)
                {
                    value = RandomNumberGenerator.GetBytes(1)[0];
                }

                span[index] = Alphabet[value % Alphabet.Length];
            }
        });

    /// <summary>A fresh secret half: 256 random bits, base64url without padding.</summary>
    internal static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>The unpadded base64url spelling of <paramref name="bytes"/>.</summary>
    internal static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
