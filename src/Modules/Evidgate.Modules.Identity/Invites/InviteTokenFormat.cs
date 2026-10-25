using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Evidgate.Modules.Identity.Invites;

/// <summary>
/// The public form of an invite link token (02.1.2-T2 §5, T75):
/// <c>evgi_&lt;12-char prefix&gt;_&lt;base64url secret&gt;</c>. The prefix is the pre-tenant
/// lookup key of <c>evidgate_resolve.invite_by_secret</c>; the secret half is 256 random bits
/// and is never stored — only <c>HMAC-SHA256(pepper_vN, secret)</c> is
/// (<see cref="InviteSecrets"/>). Same construction as <c>Ci.IngestTokenFormat</c>.
/// </summary>
/// <remarks>
/// The token travels once, in the notification email the outbox row asks for, and once more in
/// the HTTP answer of the create/resend route (<see cref="InviteSecretCarrier"/>); nothing here
/// logs it, and the type has no parse-from-storage direction on purpose.
/// </remarks>
internal static partial class InviteTokenFormat
{
    /// <summary>Length of the public lookup prefix, in characters.</summary>
    internal const int PrefixLength = 12;

    /// <summary>The scheme word every invite token starts with.</summary>
    internal const string Scheme = "evgi";

    /// <summary>Longest token this contract accepts, in characters (00 §5.7 S3: bounded input).</summary>
    internal const int MaxLength = 80;

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>A freshly generated invite token.</summary>
    /// <param name="Prefix">The public lookup prefix (12 characters of <c>[A-Za-z0-9]</c>).</param>
    /// <param name="Secret">The secret half (base64url of 32 random bytes), what gets HMACed.</param>
    /// <param name="Full">The string shown to the admin once and put in the invite link.</param>
    internal sealed record Generated(string Prefix, string Secret, string Full);

    /// <summary>Generates a token from the system CSPRNG, rejection-sampling the prefix.</summary>
    public static Generated Generate()
    {
        var prefix = string.Create(PrefixLength, 0, static (span, _) =>
        {
            Span<byte> random = stackalloc byte[PrefixLength];
            RandomNumberGenerator.Fill(random);
            for (var index = 0; index < span.Length; index++)
            {
                // 62 symbols from a byte: reject the biased tail (>= 248) by re-drawing.
                var value = random[index];
                while (value >= 248)
                {
                    value = RandomNumberGenerator.GetBytes(1)[0];
                }

                span[index] = Alphabet[value % Alphabet.Length];
            }
        });

        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));

        return new Generated(prefix, secret, $"{Scheme}_{prefix}_{secret}");
    }

    /// <summary>Splits a presented token; false when it is not shaped like one (no lookup is made then).</summary>
    /// <param name="token">The presented string, typically the link fragment of a login.</param>
    /// <param name="prefix">The public prefix, when the shape matches.</param>
    /// <param name="secret">The secret half, when the shape matches.</param>
    public static bool TryParse(string? token, out string prefix, out string secret)
    {
        prefix = string.Empty;
        secret = string.Empty;

        if (token is null || token.Length > MaxLength)
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

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex(
        "^evgi_([A-Za-z0-9]{12})_([A-Za-z0-9_-]{43})\\z",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex Shape();
}
