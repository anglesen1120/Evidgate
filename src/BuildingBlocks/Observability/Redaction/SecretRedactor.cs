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
/// Cost, because it runs on the logging thread twice per line: a line where no secret-named
/// identifier is followed by an assignment separator returns unchanged and unallocated, and the
/// patterns only ever see the first <see cref="MaxTextLength"/> characters. One pass that exceeds
/// its match timeout masks the whole text rather than continue over it, so a pathological
/// input costs a bounded time and never leaks (10.3.1-T1.4 review F7/F8).
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

    /// <summary>Connection-string/assignment keywords whose value is a secret.</summary>
    private const string SecretWords =
        "password|passwd|pwd|accountkey|sharedaccesskey|secret|token|apikey|api_key|key|pepper|cookie|credential";

    /// <summary>
    /// A key token that <em>contains</em> one of <see cref="SecretWords"/> — <c>Password</c>,
    /// <c>api-key</c>, <c>SIGNING_KEY</c>, and an <c>EVIDGATE_*</c> name whose secret word is not
    /// the last segment (<c>EVIDGATE__X__SECRET_VALUE</c>, QA D3). The lookbehind starts the token at
    /// a boundary, so a long identifier with no secret word is scanned once, not once per character.
    /// The whole token is the <c>key</c> group, so the replacement keeps it as it was written.
    /// </summary>
    private const string SecretIdentifier =
        $@"(?<key>(?<![A-Za-z0-9_.\-])[A-Za-z0-9_.\-]*?(?:{SecretWords})[A-Za-z0-9_.\-]*)";

    /// <summary>What a masked assignment becomes: the key keeps its own text, so the line stays readable.</summary>
    private const string AssignmentReplacement = "${key}=" + Mask;

    /// <summary>What a masked header becomes (its separator stays a header separator).</summary>
    private const string HeaderReplacement = "${key}: " + Mask;

    /// <summary>
    /// Every literal some pattern needs before it can match: the fast pre-check of
    /// <see cref="MayContainSecret"/> walks this list, and a clean line then skips every pattern.
    /// </summary>
    /// <remarks>
    /// The pre-check is a necessary condition of every pattern, not a heuristic: a pattern can only
    /// match a boundary-anchored identifier that (a) contains one of these literals and (b) is
    /// followed by an optional quote, whitespace and <c>=</c>/<c>:</c>. A new pattern therefore
    /// needs its own literal here <em>and</em> the assignment shape it accepts in
    /// <see cref="MayContainSecret"/>, or the pre-check silently skips it.
    /// </remarks>
    private static readonly string[] TextMarkers =
    [
        "password",
        "passwd",
        "pwd",
        "accountkey",
        "sharedaccesskey",
        "secret",
        "token",
        "apikey",
        "api_key",
        "key",
        "pepper",
        "cookie",
        "credential",
        "authorization",
        "__host-",
        "__secure-",
    ];

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
    /// Masks a double-quoted secret value first: a password containing the <c>;</c>
    /// separator must be quoted (Npgsql both accepts and re-renders
    /// <c>Password="Sup3r;Secret-P4ss"</c>), so an unquoted-only pattern would mask
    /// up to the first <c>;</c> and emit the tail verbatim. Both quote kinds are
    /// handled, including doubled quotes, and the closing quote may not appear in
    /// the value.
    /// </summary>
    /// <remarks>
    /// The key may itself be quoted and the separator may be <c>:</c> (10.3.1-T1.4 QA D1/D2):
    /// <c>"Password"="…"</c> and <c>{"password":"…"}</c> are the shapes a rendered pair or a JSON
    /// body reaches a log line in, and a pattern that only knows <c>key=</c> leaves both intact.
    /// The value may span line breaks, because a quoted secret rendered into a multi-line exception
    /// message reads <c>Password="first\nsecond"</c> and a line-bounded class would mask only up to
    /// the first break and emit the tail; <see cref="MaxTextLength"/> and <see cref="MatchTimeout"/>
    /// bound what such a match may cost. The closing quote must be followed by a delimiter
    /// (<c>; , } ] )</c>, whitespace or the end of the text), so a run like <c>key="key="key="…</c> —
    /// closing quote, then another key — is not read as one quoted value whose tail is then bare
    /// text: that shape falls through to the unquoted pattern, which masks to the end of the line.
    /// The value may not already be <see cref="Mask"/> and stop there, so a second pass (the
    /// decorator and then the export processor) does not re-wrap a masked pair.
    /// </remarks>
    private readonly Regex _quotedSecretAssignment = new(
        $@"{SecretIdentifier}[""']?\s*[=:]\s*(?!\s*\*\*\*(?=$|[\s;,\)\]}}]))(?<quote>"")(?<value>(?:\\.|""""|[^""\\])*)\k<quote>(?=$|[\s;,\)\]}}])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>Same for single-quoted values (e.g. <c>Password='a;b'</c>, <c>'password': 'a;b'</c>).</summary>
    private readonly Regex _singleQuotedSecretAssignment = new(
        $@"{SecretIdentifier}[""']?\s*[=:]\s*(?!\s*\*\*\*(?=$|[\s;,\)\]}}]))(?<quote>')(?<value>(?:\\.|''|[^'\\])*)\k<quote>(?=$|[\s;,\)\]}}])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>
    /// Masks unquoted <c>secret=value</c> pairs anywhere in a text: connection-string
    /// keywords (<c>Password</c>, <c>Pwd</c>, Azure <c>AccountKey</c>/<c>SharedAccessKey</c>),
    /// environment-style assignments such as <c>EVIDGATE__X__TOKEN=…</c>, and their colon
    /// spellings (QA D2/D3). The value runs to the line end, so a value the other patterns
    /// cannot parse is masked rather than half-emitted.
    /// </summary>
    private readonly Regex _secretAssignment = new(
        $@"{SecretIdentifier}[""']?\s*[=:]\s*(?!\s*\*\*\*(?=$|[\s;,\)\]}}]))(?<value>[^;\r\n]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>
    /// Masks the value of a cookie by its name: a <c>__Host-</c>/<c>__Secure-</c> name is a
    /// session credential whatever the surface it leaks on, and its name carries no secret
    /// word the other patterns know (10.3.1-T1.4, AC2). The name stays, the value becomes
    /// <see cref="Mask"/>.
    /// </summary>
    private readonly Regex _cookieNameAssignment = new(
        @"(?<key>__Host-[A-Za-z0-9_.\-]+|__Secure-[A-Za-z0-9_.\-]+)\s*=\s*(?!\s*\*\*\*(?=$|[\s;,\)\]}]))(?<value>[^;,\r\n]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>Masks a whole <c>Cookie</c>/<c>Set-Cookie</c> header value, whatever cookie names it carries.</summary>
    private readonly Regex _cookieHeader = new(
        @"(?<key>set-cookie|cookie)\s*:\s*(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

    /// <summary>Masks <c>Authorization: Bearer …</c> style header text.</summary>
    private readonly Regex _authorizationHeader = new(
        @"(?<key>authorization)\s*:\s*(?<value>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);

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
    public string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        if (!MayContainSecret(text))
        {
            return text;
        }

        var subject = text.Length > MaxTextLength
            ? string.Concat(text.AsSpan(0, MaxTextLength), Ellipsis)
            : text;

        subject = Replace(_cookieHeader, subject, HeaderReplacement);
        subject = Replace(_authorizationHeader, subject, HeaderReplacement);

        // Quoted forms first: their value may contain the `;`/`'` separators the
        // unquoted pattern stops at.
        subject = Replace(_quotedSecretAssignment, subject, AssignmentReplacement);
        subject = Replace(_singleQuotedSecretAssignment, subject, AssignmentReplacement);
        subject = Replace(_secretAssignment, subject, AssignmentReplacement);

        return Replace(_cookieNameAssignment, subject, AssignmentReplacement);
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

    private static bool IsIdentifierCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '_' or '.' or '-';

    /// <summary>
    /// One pattern pass. A pass that exceeds <see cref="MatchTimeout"/> fails closed: the whole
    /// text becomes <see cref="Mask"/>, because a partially scanned text may still hold the secret
    /// the pattern was looking for (review F7).
    /// </summary>
    private static string Replace(Regex pattern, string text, string replacement)
    {
        try
        {
            return pattern.Replace(text, replacement);
        }
        catch (RegexMatchTimeoutException)
        {
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
