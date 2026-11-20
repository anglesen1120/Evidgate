using System.Text;
using System.Text.RegularExpressions;

namespace Evidgate.BuildingBlocks.SharedKernel.Security;

/// <summary>
/// The secret-shape rules shared by the catalog (03.2.1: D5, AC4, AC8) and the orchestrator (05.1.1 AC4, 05.1.2 AC3): a variant's parameters and a suite's environment requirements must
/// never carry a secret, so a key or a value that <em>looks</em> like one is refused at write time, before any hash is
/// stored. The rules are deliberately heuristic and conservative; a legitimate value that trips them is renamed by its
/// author, a real secret that slips through is the residual risk the plan records. Keys are split into words (on
/// non-alphanumerics and camelCase), so <c>api_token</c> and <c>apiToken</c> match while <c>monkey</c> and <c>keyboard</c> do not.
/// </summary>
public static partial class SecretShape
{
    private static readonly HashSet<string> SecretWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "tokens", "password", "passwd", "pwd", "secret", "secrets", "key", "keys", "credential", "credentials",
        "bearer", "apikey", "authorization", "passphrase", "pass", "jwt", "dsn", "signature", "otp", "pat", "cert",
    };

    private static readonly string[] TokenPrefixes =
    [
        "ghp_", "gho_", "ghu_", "ghs_", "ghr_", "github_pat_", "glpat-", "xoxb-", "xoxp-", "xoxa-", "xoxs-", "sk-", "sk_live_", "sk_test_",
        "pk_live_", "rk_live_", "AKIA", "ASIA", "AIza", "egt_", "evg_", "npm_", "pypi-", "dop_v1_", "shpat_",
    ];

    private static readonly string[] EmbeddedMarkers =
    [
        "password=", "passwd=", "pwd=", "secret=", "token=", "apikey=", "api_key=", "api-key=", "access_token", "client_secret",
        "x-amz-signature", "x-amz-credential", "sig=", "token ", "bearer ",
    ];

    /// <summary>
    /// True when a name that is supposed to be the <em>name</em> of a secret (an environment variable such as <c>API_TOKEN</c>)
    /// looks like a secret <em>value</em> instead: an access-key prefix, or a long run without a separator.
    /// </summary>
    /// <param name="name">The candidate name.</param>
    public static bool SecretNameLooksLikeValue(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (name.Length > 40)
        {
            return true;
        }

        foreach (var prefix in TokenPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal) && name.Length >= prefix.Length + 8)
            {
                return true;
            }
        }

        return name.Length >= 16 && !name.Contains('_', StringComparison.Ordinal);
    }

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
            || flat.Contains("privatekey", StringComparison.Ordinal)
            || flat.Contains("secretkey", StringComparison.Ordinal)
            || flat.Contains("accesskey", StringComparison.Ordinal)
            || flat.Contains("sshkey", StringComparison.Ordinal)
            || flat.Contains("authkey", StringComparison.Ordinal)
            || flat.Contains("licensekey", StringComparison.Ordinal)
            || flat.Contains("passwd", StringComparison.Ordinal)
            || flat.Contains("password", StringComparison.Ordinal)
            || flat.Contains("secret", StringComparison.Ordinal)
            || flat.Contains("token", StringComparison.Ordinal)
            || flat.Contains("credential", StringComparison.Ordinal);
    }

    /// <summary>True when the value has the shape of a token, a JWT, a private key, a bearer credential or a long random string.</summary>
    /// <param name="value">A string value.</param>
    public static bool ValueLooksSecret(string value) => ValueLooksSecret(value, GeneratedMatch);

    /// <summary>
    /// The rule behind <see cref="ValueLooksSecret(string)"/> with the pattern matcher passed in, so a test can make a
    /// pattern time out without a pathological input. A timeout fails closed: a value the shape check could not finish
    /// is treated as secret-shaped (callers already refuse those), never as clean and never as an exception. The patterns
    /// are linear, so a timeout means a stalled host rather than a hostile input; the 100 ms budget stays because
    /// NonBacktracking would drop source generation without removing the need for this handling.
    /// </summary>
    /// <param name="value">A string value.</param>
    /// <param name="match">Runs one named pattern against the trimmed value; may throw <see cref="RegexMatchTimeoutException"/>.</param>
    internal static bool ValueLooksSecret(string value, Func<SecretPattern, string, bool> match)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(match);

        var trimmed = value.Trim();
        if (trimmed.Length < 8)
        {
            return false;
        }

        var running = SecretPattern.Jwt;
        try
        {
            return Looks(trimmed, match, ref running);
        }
        catch (RegexMatchTimeoutException)
        {
            SecretShapeMetrics.RecordTimeout(running);
            return true;
        }
    }

    private static bool Test(Func<SecretPattern, string, bool> match, ref SecretPattern running, SecretPattern pattern, string text)
    {
        running = pattern;
        return match(pattern, text);
    }

    private static bool Looks(string trimmed, Func<SecretPattern, string, bool> match, ref SecretPattern running)
    {
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

        if (Test(match, ref running, SecretPattern.Jwt, trimmed) || Test(match, ref running, SecretPattern.UrlCredentials, trimmed))
        {
            return true;
        }

        // Embedded forms: `password=hunter2`, `token ghp_…`, a credential in a query string.
        foreach (var marker in EmbeddedMarkers)
        {
            if (trimmed.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // A bare long hex run is a key however its entropy turns out (many vendor keys are 32 or 40 hex digits); a UUID is an id.
        if (trimmed.Length >= 32 && Test(match, ref running, SecretPattern.HexRun, trimmed))
        {
            return true;
        }

        // A long run of random-looking characters: not an id (uuid), not a path or URL (they contain . : / or spaces).
        return trimmed.Length >= 24
            && trimmed.AsSpan().ContainsAnyInRange('0', '9')
            && trimmed.AsSpan().ContainsAnyInRange('A', 'z')
            && Test(match, ref running, SecretPattern.RandomRun, trimmed)
            && !Test(match, ref running, SecretPattern.Uuid, trimmed)
            && Entropy(trimmed) >= 3.7;
    }

    private static bool GeneratedMatch(SecretPattern pattern, string text) => pattern switch
    {
        SecretPattern.Jwt => Jwt().IsMatch(text),
        SecretPattern.UrlCredentials => UrlCredentials().IsMatch(text),
        SecretPattern.HexRun => HexRun().IsMatch(text),
        SecretPattern.RandomRun => RandomRun().IsMatch(text),
        SecretPattern.Uuid => Uuid().IsMatch(text),
        _ => throw new ArgumentOutOfRangeException(nameof(pattern)),
    };

    /// <summary>
    /// True when <paramref name="value"/> is the wire shape of a reference, <c>secret:</c> and 1–64 characters of
    /// <c>[A-Za-z0-9_.-]</c>. A reference is not a licence to carry a token: its name still has to pass
    /// <see cref="SecretNameLooksLikeValue"/>. Anchored at both ends, so a trailing newline is refused.
    /// </summary>
    /// <param name="value">The candidate reference.</param>
    public static bool IsReference(ReadOnlySpan<char> value) =>
        value.StartsWith("secret:", StringComparison.Ordinal)
        && AsciiShape.IsMatch(value[7..], 1, 64, AsciiShape.AlnumDotDash, AsciiShape.AlnumDotDash);

    /// <summary>Shannon entropy in bits per character.</summary>
    /// <param name="text">The text.</param>
    public static double Entropy(string text)
    {
        Span<int> counts = stackalloc int[128];
        var other = new Dictionary<char, int>();
        foreach (var c in text)
        {
            if (c < 128)
            {
                counts[c]++;
            }
            else
            {
                other[c] = other.GetValueOrDefault(c) + 1;
            }
        }

        var bits = 0.0;
        foreach (var count in counts)
        {
            if (count > 0)
            {
                var p = (double)count / text.Length;
                bits -= p * Math.Log2(p);
            }
        }

        foreach (var count in other.Values)
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

    [GeneratedRegex(@"^[0-9a-fA-F]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex HexRun();

    [GeneratedRegex(@"^eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}(\.[A-Za-z0-9_-]*)?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"^[a-z][a-z0-9+.-]*://[^/\s:@]+:[^/\s@]+@", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"^[A-Za-z0-9+/=_-]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex RandomRun();

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Uuid();
}
