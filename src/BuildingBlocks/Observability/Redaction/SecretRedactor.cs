using System.Text.RegularExpressions;

namespace Evidgate.BuildingBlocks.Observability.Redaction;

/// <summary>
/// The 00 §5.7 S3 redaction list of 01.1.2-T1.2: it decides what a log state may
/// carry. Keys that name a whole secret (<c>authorization</c>, tokens, passwords,
/// API keys, cookies) are erased; connection-string shaped values are erased
/// inline, so the host/database stays visible while <c>Password=…</c> never is.
/// Applied by <see cref="RedactingLoggerFactory"/> to every provider.
/// </summary>
public sealed class SecretRedactor
{
    /// <summary>The replacement written wherever a secret was (00 §5.7 S3: never the value).</summary>
    public const string Mask = "***";

    /// <summary>Connection-string/assignment keywords whose value is a secret.</summary>
    private const string SecretKeys = "password|pwd|accountkey|sharedaccesskey|secret|token|apikey|api_key|key";

    /// <summary>Key markers (lowercased, separators stripped) whose whole value is a secret.</summary>
    private static readonly string[] WholeValueKeyMarkers =
    [
        "authorization",
        "token",
        "secret",
        "password",
        "apikey",
        "credential",
        "cookie",
        "privatekey",
        "sharedaccesskey",
        "accountkey",
    ];

    /// <summary>Key markers whose value keeps its non-secret parts and is masked inline.</summary>
    private static readonly string[] InlineValueKeyMarkers =
    [
        "connectionstring",
        "connstring",
        "dsn",
    ];

    /// <summary>
    /// Masks a <em>quoted</em> secret first: a password containing the <c>;</c>
    /// separator must be quoted (Npgsql both accepts and re-renders
    /// <c>Password="Sup3r;Secret-P4ss"</c>), so an unquoted-only pattern would mask
    /// up to the first <c>;</c> and emit the tail verbatim. Both quote kinds are
    /// handled, including doubled quotes, and the closing quote may not appear in
    /// the value.
    /// </summary>
    private readonly Regex _quotedSecretAssignment = new(
        $@"(?<key>{SecretKeys})\s*=\s*(?<quote>"")(?<value>(?:[^""]|"""")*)\k<quote>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Same for single-quoted values (e.g. <c>Password='a;b'</c>).</summary>
    private readonly Regex _singleQuotedSecretAssignment = new(
        $@"(?<key>{SecretKeys})\s*=\s*(?<quote>')(?<value>(?:[^']|'')*)\k<quote>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Masks unquoted <c>secret=value</c> pairs anywhere in a text: connection-string
    /// keywords (<c>Password</c>, <c>Pwd</c>, Azure <c>AccountKey</c>/<c>SharedAccessKey</c>)
    /// and environment-style assignments such as <c>EVIDGATE__X__TOKEN=…</c>.
    /// </summary>
    private readonly Regex _secretAssignment = new(
        $@"(?<key>{SecretKeys})\s*=\s*(?<value>[^;\r\n]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Masks <c>Authorization: Bearer …</c> style header text.</summary>
    private readonly Regex _authorizationHeader = new(
        @"(?<key>authorization)\s*:\s*(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Redacts a structured state value by its key.</summary>
    public string RedactValue(string? key, object? value)
    {
        var text = value as string ?? value?.ToString() ?? string.Empty;

        return IsWholeValueSecret(key) ? Mask : RedactText(text);
    }

    /// <summary>Redacts secrets inside free text (formatted messages, template strings, values).</summary>
    public string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        // Quoted forms first: their value may contain the `;`/`'` separators the
        // unquoted pattern stops at.
        var redacted = _quotedSecretAssignment.Replace(text, "${key}=" + Mask);
        redacted = _singleQuotedSecretAssignment.Replace(redacted, "${key}=" + Mask);
        redacted = _secretAssignment.Replace(redacted, "${key}=" + Mask);

        return _authorizationHeader.Replace(redacted, "${key}: " + Mask);
    }

    internal static bool IsWholeValueSecret(string? key)
    {
        var normalizedKey = Normalize(key);

        return normalizedKey.Length > 0
            && !ContainsAny(normalizedKey, InlineValueKeyMarkers)
            && ContainsAny(normalizedKey, WholeValueKeyMarkers);
    }

    private static bool ContainsAny(string normalizedKey, string[] markers) =>
        markers.Any(marker => normalizedKey.Contains(marker, StringComparison.Ordinal));

    private static string Normalize(string? key) =>
        string.IsNullOrEmpty(key)
            ? string.Empty
            : key.Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace(".", string.Empty, StringComparison.Ordinal)
                .Replace(":", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();
}
