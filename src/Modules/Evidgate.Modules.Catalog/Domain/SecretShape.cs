using System.Text;
using System.Text.RegularExpressions;

namespace Evidgate.Modules.Catalog.Domain;

/// <summary>
/// The secret-shape rules of 03.2.1 (D5, AC4, AC8): a variant's parameters and a suite's environment requirements must
/// never carry a secret, so a key or a value that <em>looks</em> like one is refused at write time, before any hash is
/// stored. The rules are deliberately heuristic and conservative; a legitimate value that trips them is renamed by its
/// author, a real secret that slips through is the residual risk the plan records. Keys are split into words (on
/// non-alphanumerics and camelCase), so <c>api_token</c> and <c>apiToken</c> match while <c>monkey</c> and <c>keyboard</c> do not.
/// </summary>
internal static partial class SecretShape
{
    private static readonly HashSet<string> SecretWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "tokens", "password", "passwd", "pwd", "secret", "secrets", "key", "keys", "credential", "credentials",
        "bearer", "apikey", "authorization", "passphrase",
    };

    private static readonly string[] TokenPrefixes =
    [
        "ghp_", "gho_", "ghu_", "ghs_", "ghr_", "github_pat_", "glpat-", "xoxb-", "xoxp-", "xoxa-", "xoxs-", "sk-", "sk_live_", "sk_test_",
        "pk_live_", "rk_live_", "AKIA", "ASIA", "AIza", "egt_", "evg_", "npm_", "pypi-", "dop_v1_", "shpat_",
    ];

    /// <summary>True when the key names a secret (<c>api_token</c>, <c>apiKey</c>, <c>Password</c>, <c>client-secret</c>).</summary>
    /// <param name="key">A parameter or member name.</param>
    public static bool KeyLooksSecret(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        foreach (var word in Words(key))
        {
            if (SecretWords.Contains(word))
            {
                return true;
            }
        }

        // Compound spellings without a separator: apikey, accesstoken, clientsecret.
        var flat = key.ToLowerInvariant();

        return flat.Contains("apikey", StringComparison.Ordinal)
            || flat.Contains("password", StringComparison.Ordinal)
            || flat.Contains("secret", StringComparison.Ordinal)
            || flat.Contains("token", StringComparison.Ordinal)
            || flat.Contains("credential", StringComparison.Ordinal);
    }

    /// <summary>True when the value has the shape of a token, a JWT, a private key, a bearer credential or a long random string.</summary>
    /// <param name="value">A string value.</param>
    public static bool ValueLooksSecret(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var trimmed = value.Trim();
        if (trimmed.Length < 8)
        {
            return false;
        }

        if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("-----BEGIN ", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var prefix in TokenPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal) && trimmed.Length >= prefix.Length + 8)
            {
                return true;
            }
        }

        if (Jwt().IsMatch(trimmed) || UrlCredentials().IsMatch(trimmed))
        {
            return true;
        }

        // A long run of random-looking characters: not an id (uuid), not a path or URL (they contain . : / or spaces).
        return trimmed.Length >= 24
            && RandomRun().IsMatch(trimmed)
            && !Uuid().IsMatch(trimmed)
            && trimmed.Any(char.IsDigit)
            && trimmed.Any(char.IsLetter)
            && Entropy(trimmed) >= 3.7;
    }

    /// <summary>Shannon entropy in bits per character.</summary>
    /// <param name="text">The text.</param>
    internal static double Entropy(string text)
    {
        var counts = new Dictionary<char, int>();
        foreach (var c in text)
        {
            counts[c] = counts.GetValueOrDefault(c) + 1;
        }

        var bits = 0.0;
        foreach (var count in counts.Values)
        {
            var p = (double)count / text.Length;
            bits -= p * Math.Log2(p);
        }

        return bits;
    }

    private static IEnumerable<string> Words(string key)
    {
        var word = new StringBuilder();
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (!char.IsLetterOrDigit(c))
            {
                if (word.Length > 0)
                {
                    yield return word.ToString();
                    word.Clear();
                }

                continue;
            }

            if (word.Length > 0 && char.IsUpper(c) && char.IsLower(key[i - 1]))
            {
                yield return word.ToString();
                word.Clear();
            }

            word.Append(c);
        }

        if (word.Length > 0)
        {
            yield return word.ToString();
        }
    }

    [GeneratedRegex(@"^eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}(\.[A-Za-z0-9_-]*)?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"^[a-z][a-z0-9+.-]*://[^/\s:@]+:[^/\s@]+@", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"^[A-Za-z0-9+/=_-]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex RandomRun();

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Uuid();
}
