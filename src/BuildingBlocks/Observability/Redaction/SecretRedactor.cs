using System.Collections;
using System.Text.RegularExpressions;

namespace Evidgate.BuildingBlocks.Observability.Redaction;

/// <summary>
/// The 00 §5.7 S3 redaction list of 01.1.2-T1.2, extended by 10.3.1-T1.4: it decides
/// what a log state may carry.
/// </summary>
/// <remarks>
/// <para>
/// Keys that name a whole secret (<c>authorization</c>, tokens, passwords, API keys,
/// cookies, peppers, <c>__Host-</c>/<c>__Secure-</c> cookie names) are erased;
/// connection-string shaped values are erased inline, so the host/database stays
/// visible while <c>Password=…</c> never is. Applied by
/// <see cref="RedactingLoggerFactory"/> to every provider and again at export by
/// <see cref="LogRedactionProcessor"/>.
/// </para>
/// <para>
/// Limits, by construction: this is a shape-aware scrubber, not a value oracle. It
/// masks a secret that is introduced by a name it knows (<c>password=</c>, a cookie
/// header, an <c>EVIDGATE_*</c> key that names one of the secret words) and the whole
/// value of a key that names a secret. A bare secret with no name beside it — a token
/// pasted into an exception message as the entire text — is not detectable here; the
/// contract is still 00 §5.5 C8, that code never puts a secret in a message, and this
/// list is the second line that catches the shapes it can see.
/// </para>
/// </remarks>
public sealed class SecretRedactor
{
    /// <summary>The replacement written wherever a secret was (00 §5.7 S3: never the value).</summary>
    public const string Mask = "***";

    /// <summary>What a value that is too deep or too long to render becomes.</summary>
    private const string Ellipsis = "…";

    /// <summary>Nesting limit for a structured value: enough for a log dictionary, never an unbounded walk.</summary>
    private const int MaxDepth = 4;

    /// <summary>Entry limit per structured value, so one state pair cannot turn into a megabyte of log line.</summary>
    private const int MaxEntries = 32;

    /// <summary>Connection-string/assignment keywords whose value is a secret.</summary>
    private const string SecretKeys =
        "password|passwd|pwd|accountkey|sharedaccesskey|secret|token|apikey|api_key|key|pepper|cookie|credential";

    /// <summary>Key markers (lowercased, separators stripped) whose whole value is a secret.</summary>
    private static readonly string[] WholeValueKeyMarkers =
    [
        "authorization",
        "token",
        "secret",
        "password",
        "passwd",
        "pwd",
        "apikey",
        "credential",
        "cookie",
        "privatekey",
        "sharedaccesskey",
        "accesskey",
        "accountkey",
        "pepper",
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

    /// <summary>
    /// Masks the value of a cookie by its name: a <c>__Host-</c>/<c>__Secure-</c> name is a
    /// session credential whatever the surface it leaks on, and its name carries no secret
    /// word the other patterns know (10.3.1-T1.4, AC2). The name stays, the value becomes
    /// <see cref="Mask"/>.
    /// </summary>
    private readonly Regex _cookieNameAssignment = new(
        @"(?<key>__Host-[A-Za-z0-9_.\-]+|__Secure-[A-Za-z0-9_.\-]+)\s*=\s*(?<value>[^;,\r\n]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Masks a whole <c>Cookie</c>/<c>Set-Cookie</c> header value, whatever cookie names it carries.</summary>
    private readonly Regex _cookieHeader = new(
        @"(?<key>set-cookie|cookie)\s*:\s*(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Masks <c>Authorization: Bearer …</c> style header text.</summary>
    private readonly Regex _authorizationHeader = new(
        @"(?<key>authorization)\s*:\s*(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Redacts a structured state value by its key (10.3.1-T1.4): a key that names a whole
    /// secret yields <see cref="Mask"/> whatever the value was, and anything else is rendered
    /// to text with its own secrets masked — a nested dictionary is walked by key (bounded in
    /// depth and entries), so a token inside it is not smuggled through its <c>ToString()</c>.
    /// </summary>
    /// <returns>The redacted text of the value; empty for <c>null</c>.</returns>
    public string RedactValue(string? key, object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return IsWholeValueSecret(key) ? Mask : Describe(value, depth: 0);
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
        redacted = _cookieNameAssignment.Replace(redacted, "${key}=" + Mask);
        redacted = _cookieHeader.Replace(redacted, "${key}: " + Mask);

        return _authorizationHeader.Replace(redacted, "${key}: " + Mask);
    }

    /// <summary>
    /// Redacts an exception for export (10.3.1-T1.4, AC2): the message and stack are text
    /// like any other, and a collector receives them as the <c>exception.message</c> /
    /// <c>exception.stacktrace</c> attributes. Returns the same instance when there is
    /// nothing to mask, so a clean exception keeps its identity and type.
    /// </summary>
    public Exception? RedactException(Exception? exception)
    {
        if (exception is null || exception is RedactedException)
        {
            return exception;
        }

        var text = exception.ToString();
        var redacted = RedactText(text);
        if (string.Equals(redacted, text, StringComparison.Ordinal))
        {
            return exception;
        }

        return new RedactedException(
            exception.GetType().FullName ?? exception.GetType().Name,
            RedactText(exception.Message),
            exception.StackTrace is { } stack ? RedactText(stack) : null,
            redacted);
    }

    /// <summary>The text a state pair contributes to a rendered message, whatever its type.</summary>
    internal static string TextOf(object? value) => value as string ?? value?.ToString() ?? string.Empty;

    internal static bool IsWholeValueSecret(string? key)
    {
        var normalizedKey = Normalize(key);
        if (normalizedKey.Length == 0)
        {
            return false;
        }

        // A connection string keeps its non-secret parts (host, database) and is masked
        // inline, whatever else its key name spells.
        if (ContainsAny(normalizedKey, InlineValueKeyMarkers))
        {
            return false;
        }

        // A cookie name is a credential by the browser's own contract, and neither the
        // `__Host-` nor the `__Secure-` prefix contains a secret word the marker list has.
        if (key!.Contains("__Host-", StringComparison.OrdinalIgnoreCase)
            || key.Contains("__Secure-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ContainsAny(normalizedKey, WholeValueKeyMarkers);
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

    /// <summary>
    /// Renders a structured value with its own secrets masked. Dictionaries are walked by
    /// key (the same rule a state pair gets), sequences element by element, and anything
    /// else through its text — which is what the exporter would have done anyway.
    /// </summary>
    private string Describe(object value, int depth)
    {
        if (depth >= MaxDepth)
        {
            return Ellipsis;
        }

        switch (value)
        {
            case string text:
                return RedactText(text);
            case Exception exception:
                return RedactText(exception.ToString());
            case IDictionary dictionary:
                return Describe(dictionary, depth);
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                return Describe(pairs, depth);
            case IEnumerable sequence:
                return Describe(sequence, depth);
            default:
                return RedactText(value.ToString() ?? string.Empty);
        }
    }

    private string Describe(IDictionary dictionary, int depth)
    {
        var parts = new List<string>(Math.Min(dictionary.Count, MaxEntries));
        foreach (DictionaryEntry entry in dictionary)
        {
            if (parts.Count == MaxEntries)
            {
                parts.Add(Ellipsis);
                break;
            }

            var key = TextOf(entry.Key);
            parts.Add($"{key}={DescribeValue(key, entry.Value, depth + 1)}");
        }

        return "{" + string.Join(", ", parts) + "}";
    }

    private string Describe(IEnumerable<KeyValuePair<string, object?>> pairs, int depth)
    {
        var parts = new List<string>();
        foreach (var pair in pairs)
        {
            if (parts.Count == MaxEntries)
            {
                parts.Add(Ellipsis);
                break;
            }

            parts.Add($"{pair.Key}={DescribeValue(pair.Key, pair.Value, depth + 1)}");
        }

        return "{" + string.Join(", ", parts) + "}";
    }

    private string Describe(IEnumerable sequence, int depth)
    {
        var parts = new List<string>();
        foreach (var item in sequence)
        {
            if (parts.Count == MaxEntries)
            {
                parts.Add(Ellipsis);
                break;
            }

            parts.Add(item is null ? string.Empty : Describe(item, depth + 1));
        }

        return "[" + string.Join(", ", parts) + "]";
    }

    private string DescribeValue(string key, object? value, int depth)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return IsWholeValueSecret(key) ? Mask : Describe(value, depth);
    }
}
