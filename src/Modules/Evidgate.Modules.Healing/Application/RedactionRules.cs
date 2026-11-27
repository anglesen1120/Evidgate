using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.Observability.Redaction;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// The versioned rule catalogue of the egress redaction pipeline. Credentials, cookies, bearer headers and token shapes
/// come from <see cref="SecretRedactor"/> (the one list of secret-shaped text the log path already uses); the classes a
/// log never needed — personal data and typed input — are added here. Masking is a fixed marker per rule, so the same
/// text always masks to the same bytes.
/// </summary>
internal static class RedactionRules
{
    /// <summary>
    /// Changes whenever a rule is added, removed or changed, so a stored report says which catalogue produced it.
    /// </summary>
    internal const string Version = "evg-redact-v1";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly SecretRedactor Secrets = new();

    // Quoted spans are single-line and atomic. A backslash before a quote outside a span is consumed as one unit
    // (Escaped), so a run of \' pairs can never open a span: a span that fails to close can then fail only once per
    // quote kind and line, not again from every later candidate start. A call argument may hold brackets nested up to
    // three deep and may wrap over lines; deeper nesting is not matched (known limit).
    private const string Escaped = @"\\['""`\\]";
    private const string Quoted = @"'(?>(?:\\.|[^'\\\r\n]|\\)*)'|""(?>(?:\\.|[^""\\\r\n]|\\)*)""|`(?>(?:\\.|[^`\\\r\n]|\\)*)`";
    private const string Group1 = @"[(\[](?>(?:" + Escaped + "|" + Quoted + @"|[^()\[\]'""`])*)[)\]]";
    private const string Group2 = @"[(\[](?>(?:" + Escaped + "|" + Quoted + "|" + Group1 + @"|[^()\[\]'""`])*)[)\]]";
    private const string Group3 = @"[(\[](?>(?:" + Escaped + "|" + Quoted + "|" + Group2 + @"|[^()\[\]'""`])*)[)\]]";

    // The selector argument of a fill-style call: a literal or an expression such as SEL.a or sel('a,b').
    private const string Selector = @"(?>(?:" + Escaped + "|" + Quoted + "|" + Group3 + @"|[^,()\[\]'""`])+)";

    // What follows a literal value up to the end of its argument ('a' + 'b', 'a' + x). A stray quote is consumed as text.
    private const string ArgumentTail = @"(?>(?:" + Escaped + "|" + Quoted + "|" + Group3 + @"|[^,()\[\]'""`]|['""`])*)";

    // The same for an object member, which also ends at the closing brace.
    private const string MemberTail = @"(?>(?:" + Escaped + "|" + Quoted + "|" + Group3 + @"|[^,()\[\]{}'""`]|['""`])*)";

    // A value literal that is not exactly the generated marker followed by the end of its argument. An unclosed quote
    // ends at the line end (a template literal at the end of the window) and is masked. A Python string prefix
    // (f, r, b, u and the two-letter pairs) belongs to the literal.
    private const string ArgumentEnd = @"(?=\s*(?:[,)]|\z))";
    private const string MemberEnd = @"(?=\s*(?:[,)}\]]|\z))";
    private const string Marker = @"\[redacted:input_value\]";
    private const string StringPrefix = @"(?:fr|rf|br|rb|ur|ru|[fbru])?";
    private const string ValueLiteral =
        StringPrefix + @"'(?!" + Marker + "'" + ArgumentEnd + @")(?>(?:\\.|[^'\\\r\n]|\\)*)(?:'|(?=[\r\n]|\z))"
        + "|" + StringPrefix + @"[@$]{0,2}""(?!" + Marker + @"""" + ArgumentEnd + @")(?>(?:\\.|[^""\\\r\n]|\\)*)(?:""|(?=[\r\n]|\z))"
        + @"|`(?!" + Marker + "`" + ArgumentEnd + @")(?>(?:\\.|[^`\\]|\\)*)(?:`|\z)";

    // The value of an object-literal or JSON member, in either quote kind. A quote that never closes runs to the end
    // of the window: a string may wrap over lines, and masking too much is safer than leaving a line of the value.
    private const string MemberSingle = @"'(?!" + Marker + "'" + MemberEnd + @")(?>(?:\\.|[^'\\]|\\)*)(?:'|\z)";
    private const string MemberDouble = @"""(?!" + Marker + @"""" + MemberEnd + @")(?>(?:\\.|[^""\\]|\\)*)(?:""|\z)";

    // Calls whose first literal argument may be the typed text (the selector, when there is one, comes first).
    private const string TypeCall = @"\.(?:fill|type|pressSequentially|press_sequentially)(?:Async)?\(";

    // Calls whose first argument is always the typed text.
    private const string KeysCall = @"\.(?:sendKeys|send_keys|setValue)\(";

    // Rules without look-around use the linear-time engine, so a long run of rule-shaped characters cannot cost
    // more than its length; the rest carry a match timeout, and a timeout masks the whole window.
    // An input value is left alone only when it is exactly the generated marker (an unquoted one also needs a closing
    // delimiter after it: whitespace, > or />); any other text, including a marker prefix, a marker followed by a tail
    // or a different marker, is masked. Names are matched without case; a two-argument call is masked before
    // the one-argument form so the selector of fill('#sel', 'x') is never taken for the value.
    // A private key is masked up to its END line; with none, up to the closing quote of the string it sits in, or the
    // end of the window. Text after a closing quote is out of reach, so a surviving END line is refused by pass two.
    private static readonly Rule[] Catalogue =
    [
        new("private_key", @"-----BEGIN [A-Z ]*PRIVATE KEY(?: BLOCK)?-----(?:[^""'`]*?-----END [A-Z ]*PRIVATE KEY(?: BLOCK)?-----|[^""'`]*)", Linear: true, Prefilter: "PRIVATE KEY"),
        new("api_key_shape", @"\b(?:(?:AKIA|ASIA)[0-9A-Z]{16}|sk-[A-Za-z0-9_\-]{20,}|xox[baprs]-[A-Za-z0-9\-]{10,}|AIza[0-9A-Za-z_\-]{35})", Linear: true),
        new("bearer_token", @"\bbearer\s+[A-Za-z0-9._~+/=\-]{8,}", Linear: true, Replacement: "Bearer [redacted:bearer_token]", Prefilter: "earer"),
        new("email", @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+", Linear: true, Prefilter: "@"),
        new("phone", @"(?<!\d)(?:\+84|0)[\s.\-]?[2-9](?:[\s.\-]?\d){8,9}(?!\d)"),
        new("phone", @"(?<![\w+])\+\d[\d\s.\-()]{8,16}\d"),
        new("input_value", @"(\bvalue\s*=\s*)""(?!\[redacted:input_value\]"")[^""]*""", Replacement: "$1\"[redacted:input_value]\"", Prefilter: "value"),
        new("input_value", @"(\bvalue\s*=\s*)'(?!\[redacted:input_value\]')[^']*'", Replacement: "$1'[redacted:input_value]'", Prefilter: "value"),
        new("input_value", @"(\bvalue\s*=\s*)(?![""'\s]|\[redacted:input_value\](?:\s|/?>|\z))[^\s""'<>`=]+", Replacement: "$1\"[redacted:input_value]\"", Prefilter: "value"),
        new("input_value", @"(""value""\s*:\s*)(?:" + MemberDouble + ")" + MemberTail, Replacement: "$1\"[redacted:input_value]\"", Prefilter: "\"value\""),
        new("input_value", @"([{,]\s*(?:value|'value'|""value"")\s*:\s*)(?:" + MemberSingle + "|" + MemberDouble + ")" + MemberTail, Replacement: "$1\"[redacted:input_value]\"", Prefilter: "value"),
        new("input_value", "(" + TypeCall + @"(?>\s*)" + Selector + @"\s*,\s*)(?:" + ValueLiteral + ")" + ArgumentTail, Replacement: "$1\"[redacted:input_value]\"", Prefilter: "("),
        new("input_value", "(" + TypeCall + @"(?>\s*))(?:" + ValueLiteral + ")" + ArgumentTail + @"(?=\s*(?:\)|,(?>\s*)(?!['""`])))", Replacement: "$1\"[redacted:input_value]\"", Prefilter: "("),
        new("input_value", "(" + KeysCall + @"(?>\s*))(?:" + ValueLiteral + ")" + ArgumentTail, Replacement: "$1\"[redacted:input_value]\"", Prefilter: "("),
    ];

    // A BEGIN line is masked above; an END line or a PGP block label that survives means a key was cut from its start.
    private static readonly Regex ResidualPrivateKey = new(
        @"-----END [A-Z ]*PRIVATE KEY|PRIVATE KEY BLOCK",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
        Regex.InfiniteMatchTimeout);

    /// <summary>True when <paramref name="text"/> still holds the END line or label of a private key block.</summary>
    internal static bool HasResidualPrivateKey(string text) =>
        text.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase) && ResidualPrivateKey.IsMatch(text);

    /// <summary>Masks <paramref name="text"/> with every rule and counts the replacements per rule id.</summary>
    /// <param name="text">One window of the payload.</param>
    /// <param name="counts">Receives the replacements per rule id.</param>
    internal static string Apply(string text, Dictionary<string, int> counts)
    {
        var masked = Secrets.RedactTextCounted(text, counts, int.MaxValue);
        foreach (var rule in Catalogue)
        {
            masked = rule.Apply(masked, counts);
        }

        return masked;
    }

    private sealed record Rule(string Id, string Pattern, bool Linear = false, string? Replacement = null, string? Prefilter = null)
    {
        private readonly Regex _regex = new(
            Pattern,
            Linear
                ? RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking
                : RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            Linear ? Regex.InfiniteMatchTimeout : MatchTimeout);

        private readonly string _marker = Replacement ?? $"[redacted:{Id}]";

        internal string Apply(string text, Dictionary<string, int> counts)
        {
            if (Prefilter is not null && !text.Contains(Prefilter, StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }

            try
            {
                var matches = 0;
                var result = _regex.Replace(text, match =>
                {
                    matches++;
                    return match.Result(_marker);
                });
                if (matches > 0)
                {
                    counts[Id] = counts.GetValueOrDefault(Id) + matches;
                }

                return result;
            }
            catch (RegexMatchTimeoutException)
            {
                counts["regex_timeout"] = counts.GetValueOrDefault("regex_timeout") + 1;
                return "[redacted:regex_timeout]";
            }
        }
    }
}
