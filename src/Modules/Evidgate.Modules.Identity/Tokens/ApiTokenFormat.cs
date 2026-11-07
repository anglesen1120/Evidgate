using System.Text.RegularExpressions;

namespace Evidgate.Modules.Identity.Tokens;

/// <summary>
/// The public form of a project API token: <c>evg_&lt;12-char prefix&gt;_&lt;base64url secret&gt;</c>.
/// The prefix is the resolver's lookup key and the scheme marks a leaked value for secret scanners;
/// only the HMAC of the secret half is stored, so a database dump holds nothing usable.
/// </summary>
internal static partial class ApiTokenFormat
{
    /// <summary>The credential scheme.</summary>
    internal const string Scheme = "evg";

    /// <summary>Characters in the public lookup prefix.</summary>
    internal const int PrefixLength = SecretTokenFormat.PrefixLength;

    /// <summary>The longest string the parser accepts, so an oversized value is refused before any lookup.</summary>
    internal const int MaxLength = 80;

    /// <summary>A freshly generated token.</summary>
    /// <param name="Prefix">The public lookup prefix.</param>
    /// <param name="Secret">The secret half (what is HMACed).</param>
    /// <param name="Full">The string shown to the admin once.</param>
    internal sealed record Generated(string Prefix, string Secret, string Full);

    /// <summary>Generates a token from the system CSPRNG.</summary>
    public static Generated Generate()
    {
        var prefix = SecretTokenFormat.NewPrefix();
        var secret = SecretTokenFormat.NewSecret();

        return new Generated(prefix, secret, $"{Scheme}_{prefix}_{secret}");
    }

    /// <summary>Splits a presented token; false when it is not shaped like one (no lookup is made then).</summary>
    /// <param name="token">The presented string.</param>
    /// <param name="prefix">The public prefix.</param>
    /// <param name="secret">The secret half.</param>
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

    [GeneratedRegex(
        "\\Aevg_([A-Za-z0-9]{12})_([A-Za-z0-9_-]{43})\\z",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex Shape();
}
