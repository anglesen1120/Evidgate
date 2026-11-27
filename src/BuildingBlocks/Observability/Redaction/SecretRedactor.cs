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
/// masks a secret that is introduced by a name it knows (<c>password=</c>,
/// <c>"password":"…"</c>, a cookie header, an <c>EVIDGATE_*</c> key that contains one
/// of the secret words) and the whole value of a key that names a secret. A bare secret
/// with no name beside it — a token pasted into an exception message as the entire text —
/// is not detectable here; the contract is still 00 §5.5 C8, that code never puts a
/// secret in a message, and this list is the second line that catches the shapes it can
/// see.
/// </para>
/// <para>
/// A connection-string pair ends at <c>;</c>, so <c>DB_PASSWORD=ab;cd</c> becomes <c>DB_PASSWORD=***;cd</c>: the text
/// after the separator is kept, and a second pass cannot tell it from the next pair. A password that contains
/// <c>;</c> must be quoted to be masked whole.
/// </para>
/// <para>
/// Cost, because it runs on the logging thread twice per line: a line where no secret-named
/// identifier is followed by an assignment separator returns unchanged and unallocated, and the
/// patterns only ever see the first <see cref="MaxTextLength"/> characters. One pass that exceeds
/// its match timeout masks the whole text rather than continue over it, so a pathological
/// input costs a bounded time and never leaks (10.3.1-T1.4 review F7/F8).
/// </para>
/// </remarks>
public sealed partial class SecretRedactor
{
    /// <summary>The replacement written wherever a secret was (00 §5.7 S3: never the value).</summary>
    public const string Mask = "***";

    /// <summary>What a value that is too deep or too long to render becomes.</summary>
    private const string Ellipsis = "…";

    /// <summary>Nesting limit for a structured value: enough for a log dictionary, never an unbounded walk.</summary>
    private const int MaxDepth = 4;

    /// <summary>Entry limit per structured value, so one state pair cannot turn into a megabyte of log line.</summary>
    private const int MaxEntries = 32;

    /// <summary>
    /// Longest text the patterns are given. Anything beyond it is cut before any pattern runs, so
    /// one pathological message cannot become an unbounded scan on the logging thread (10.3.1-T1.4
    /// review F7).
    /// </summary>
    internal const int MaxTextLength = 8 * 1024;

    /// <summary>
    /// Backstop for one pattern pass. A pass that exceeds it fails closed — the whole text becomes
    /// <see cref="Mask"/> — because a half-scanned text may still hold the secret it was looking
    /// for (F7).
    /// </summary>
    /// <remarks>
    /// Sized to be unreachable for text that is not secret-shaped: a pass over a
    /// <see cref="MaxTextLength"/> subject costs well under a millisecond, so only pathological
    /// input can approach this budget. It must not be tightened towards the expected cost, because
    /// failing closed is not free - it masks a whole line, and a budget that a loaded host can
    /// exceed on a clean line turns a scheduling hiccup into a wrong log record.
    /// </remarks>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(500);

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
        // 10.3.1-T1.4 review F9: a key that names a signing/encryption key, a signature, a SAS or a
        // session is a credential whatever its value looks like, and none of its spellings carries
        // one of the words above (`EVIDGATE__X__SIGNING_KEY`, `encryption_key`, `Signature`).
        "signingkey",
        "encryptionkey",
        "signature",
        "sas",
        "session",
    ];

    /// <summary>Key markers whose value keeps its non-secret parts and is masked inline.</summary>
    private static readonly string[] InlineValueKeyMarkers =
    [
        "connectionstring",
        "connstring",
        "dsn",
    ];

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
    /// <remarks>
    /// The pre-check runs first and is exact (see <see cref="MayContainSecret"/>), so text that
    /// cannot carry an assignment - an ordinary exception message, a status line - is returned as
    /// the instance it came in, with no pattern pass and no allocation (review F8). The order of the
    /// passes is the mask order: a whole header first (its value is a session credential whatever it
    /// carries), then the quoted values, then the unquoted one. Text longer than
    /// <see cref="MaxTextLength"/> is cut <em>before</em> any pattern runs (review F7).
    /// </remarks>
    public string RedactText(string? text) => RedactCore(text, MaxTextLength, counts: null);

    /// <summary>
    /// The egress form of <see cref="RedactText"/>: the same patterns in the same order, but the text is cut at
    /// <paramref name="maxLength"/> (a caller that must not lose content passes a window it already bounded) and every
    /// replacement is counted per rule id in <paramref name="counts"/>, so a report can say what was masked without
    /// holding a value. A pass that times out masks the whole text and counts <c>regex_timeout</c>.
    /// </summary>
    /// <param name="text">The text to scrub.</param>
    /// <param name="counts">Receives the number of replacements per rule id.</param>
    /// <param name="maxLength">The longest text the patterns are given.</param>
    public string RedactTextCounted(string text, Dictionary<string, int> counts, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(counts);
        return RedactCore(text, maxLength, counts);
    }

    private string RedactCore(string? text, int maxLength, Dictionary<string, int>? counts)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        if (!MayContainSecret(text) && !MayContainTokenShape(text))
        {
            return text;
        }

        var subject = text.Length > maxLength
            ? string.Concat(text.AsSpan(0, maxLength), Ellipsis)
            : text;

        subject = Replace(_githubTokenShapes, subject, Mask, "token_shape", counts);
        subject = Replace(_cookieHeader, subject, HeaderReplacement, "cookie", counts);
        subject = Replace(_authorizationHeader, subject, HeaderReplacement, "authorization_header", counts);

        // Quoted forms first: their value may contain the `;`/`'` separators the
        // unquoted pattern stops at.
        subject = Replace(_quotedSecretAssignment, subject, AssignmentReplacement, "credential_assignment", counts);
        subject = Replace(_singleQuotedSecretAssignment, subject, AssignmentReplacement, "credential_assignment", counts);
        subject = Replace(_secretAssignment, subject, AssignmentReplacement, "credential_assignment", counts);

        return Replace(_cookieNameAssignment, subject, AssignmentReplacement, "cookie", counts);
    }

    /// <summary>
    /// Only an identifier followed by an assignment separator can introduce a secret.
    /// Checking the marker on that identifier (not anywhere in the message) keeps ordinary
    /// exception text such as "System.InvalidOperationException: nothing here is a secret"
    /// out of the regex path entirely.
    /// </summary>
    private static bool MayContainSecret(string text)
    {
        var span = text.AsSpan();
        for (var index = 0; index < span.Length;)
        {
            if (!IsIdentifierCharacter(span[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < span.Length && IsIdentifierCharacter(span[index]))
            {
                index++;
            }

            var key = span[start..index];
            var separator = index;
            if (separator < span.Length && span[separator] is '"' or '\'')
            {
                separator++;
            }

            while (separator < span.Length && char.IsWhiteSpace(span[separator]))
            {
                separator++;
            }

            if (separator >= span.Length || span[separator] is not ('=' or ':'))
            {
                continue;
            }

            foreach (var marker in TextMarkers)
            {
                if (key.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Cheap span test so only text carrying a GitHub/JWT prefix reaches the shape regex.</summary>
    private static bool MayContainTokenShape(ReadOnlySpan<char> text) =>
        text.Contains("ghs_", StringComparison.Ordinal)
        || text.Contains("ghu_", StringComparison.Ordinal)
        || text.Contains("ghr_", StringComparison.Ordinal)
        || text.Contains("gho_", StringComparison.Ordinal)
        || text.Contains("ghp_", StringComparison.Ordinal)
        || text.Contains("github_pat_", StringComparison.Ordinal)
        || text.Contains("eyJ", StringComparison.Ordinal);

    private static bool IsIdentifierCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '_' or '.' or '-';

    /// <summary>
    /// One pattern pass. A pass that exceeds <see cref="MatchTimeout"/> fails closed: the whole
    /// text becomes <see cref="Mask"/>, because a partially scanned text may still hold the secret
    /// the pattern was looking for (review F7).
    /// </summary>
    private static string Replace(
        Regex pattern,
        string text,
        string replacement,
        string ruleId,
        Dictionary<string, int>? counts)
    {
        try
        {
            if (counts is null)
            {
                return pattern.Replace(text, replacement);
            }

            var matches = 0;
            var result = pattern.Replace(text, match =>
            {
                matches++;
                return match.Result(replacement);
            });
            if (matches > 0)
            {
                counts[ruleId] = counts.GetValueOrDefault(ruleId) + matches;
            }

            return result;
        }
        catch (RegexMatchTimeoutException)
        {
            if (counts is not null)
            {
                counts["regex_timeout"] = counts.GetValueOrDefault("regex_timeout") + 1;
            }

            return Mask;
        }
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

    private static bool ContainsAny(string normalizedKey, string[] markers)
    {
        // A loop, not `markers.Any(...)`: this runs once per attribute per log record on the
        // export path, and the lambda would allocate a closure each time (review F8).
        foreach (var marker in markers)
        {
            if (normalizedKey.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

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
