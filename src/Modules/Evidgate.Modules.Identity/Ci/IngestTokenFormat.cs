using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Evidgate.Modules.Identity.Ci;

/// <summary>
/// The public form of an ingest token (02.4.2 §5): <c>egt_&lt;12-char prefix&gt;_&lt;base64url secret&gt;</c>.
/// The prefix is the resolver's lookup key; the secret is 256 random bits and is never stored.
/// </summary>
internal static partial class IngestTokenFormat
{
    internal const int PrefixLength = 12;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>A freshly generated token.</summary>
    /// <param name="Prefix">The public lookup prefix.</param>
    /// <param name="Secret">The secret half (what is HMACed).</param>
    /// <param name="Full">The string shown to the admin once.</param>
    internal sealed record Generated(string Prefix, string Secret, string Full);

    /// <summary>Generates a token from the system CSPRNG.</summary>
    public static Generated Generate()
    {
        var prefix = string.Create(PrefixLength, 0, static (span, _) =>
        {
            Span<byte> random = stackalloc byte[PrefixLength];
            RandomNumberGenerator.Fill(random);
            for (var i = 0; i < span.Length; i++)
            {
                // 62 symbols from a byte: reject the biased tail (>= 248) by re-drawing.
                var value = random[i];
                while (value >= 248)
                {
                    value = RandomNumberGenerator.GetBytes(1)[0];
                }

                span[i] = Alphabet[value % Alphabet.Length];
            }
        });

        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));

        return new Generated(prefix, secret, $"egt_{prefix}_{secret}");
    }

    /// <summary>Splits a presented token; false when it is not shaped like one (no lookup is made then).</summary>
    /// <param name="token">The presented string.</param>
    /// <param name="prefix">The public prefix.</param>
    /// <param name="secret">The secret half.</param>
    public static bool TryParse(string? token, out string prefix, out string secret)
    {
        prefix = string.Empty;
        secret = string.Empty;

        if (token is null || token.Length > 80)
        {
            return false;
        }

        var match = Shape().Match(token);
        if (!match.Success)
        {
            return false;
        }

        prefix = match.Groups[1].Value;
        secret = match.Groups[2].Value;

        return true;
    }

    /// <summary>A 128-bit-plus random challenge for a binding (T70): 256 bits, base64url.</summary>
    public static string NewChallenge() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex("^egt_([A-Za-z0-9]{12})_([A-Za-z0-9_-]{43})$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Shape();
}
