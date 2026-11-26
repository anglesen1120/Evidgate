using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// The egress boundary's redaction step: everything that is about to leave toward a provider goes through here first.
/// </summary>
internal interface IRedactionPipeline
{
    /// <summary>
    /// Masks <paramref name="payload"/> (pass one), then scans the masked text again for anything still secret-shaped
    /// (pass two). A residual hit blocks the payload: <see cref="RedactionOutcome.Text"/> is then empty.
    /// </summary>
    /// <param name="payload">The prompt text as the prompt builder wrapped it.</param>
    /// <returns>The masked text and the report of what was done.</returns>
    RedactionOutcome Redact(string payload);
}

/// <summary>
/// Two passes over the payload, window by window. Pass one is <see cref="RedactionRules.Apply"/>. Pass two applies the
/// same rules to the result and requires that nothing changes (a changed text means a value survived pass one), and
/// also to the percent-decoded, <c>\uXXXX</c>-decoded, JSON-unescaped and base64-decoded forms of the text, and to its
/// form without invisible format characters and with fullwidth look-alikes mapped to ASCII, and
/// compares the HTML-entity-decoded form of the window with its masked text, so a secret that was only encoded is
/// still caught. A payload that cannot be scanned inside its time budget, whose base64 decode budget runs out, or
/// whose window cannot be cut where no rule spans the seam, is refused, never sent unscanned.
/// </summary>
/// <remarks>
/// Limits, by construction: the rules recognise secrets by shape or by the name beside them. A bare secret that has
/// neither, a secret split across two fields, a rule-less obfuscation (reversed or homoglyph text) and a value
/// that is itself longer than a window (a quoted value or key body that closes more than one window past the cut) are
/// not seen. A window with no cut where the rules mask the same text joined and apart is refused.
/// </remarks>
internal sealed partial class RedactionPipeline(TimeProvider clock) : IRedactionPipeline
{
    /// <summary>The longest text one rule pass is given.</summary>
    internal const int MaxWindowChars = 64 * 1024;

    /// <summary>How long one payload may take in total before it is refused.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private const string BlockStart = "<<<EVG-DATA ";
    private const int MaxVariantDepth = 2;

    // A 64K window holds at most about 8,000 base64-shaped tokens of seven characters or more; ordinary dense markup
    // and identifier lists decode far fewer to valid UTF-8, so only a window built to exhaust the budget reaches it.
    private const int MaxBase64TokensPerWindow = 4096;
    private const int SeamReach = 256;
    private const int MaxSeamAttempts = 16;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Seven characters is the base64 of five bytes, the shortest text a rule matches ("key=a", "a@b.c"). Only tokens
    // that decode to valid UTF-8 count against the budget, which a word of prose rarely does.
    private static readonly Regex Base64Token = new(
        @"[A-Za-z0-9+/_\-]{7,}={0,2}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex UnicodeEscape = new(
        @"\\u([0-9a-fA-F]{4})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex JsonEscape = new(
        @"\\([""\\nt])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>A pipeline on the system clock.</summary>
    public RedactionPipeline()
        : this(TimeProvider.System)
    {
    }

    /// <inheritdoc />
    public RedactionOutcome Redact(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var budget = new ScanBudget(clock);
        var findings = new Dictionary<(string Path, string Rule), int>();
        var output = new StringBuilder(payload.Length);
        var blocked = false;

        foreach (var (path, start, end) in Sections(payload))
        {
            foreach (var (windowStart, windowEnd) in Windows(payload, start, end, budget))
            {
                if (windowEnd < 0 || budget.Check())
                {
                    var rule = budget.Aborted ? "redaction_budget_exceeded" : "residual_window_seam";
                    Add(findings, path, rule, 1);
                    return Finish(string.Empty, findings, blocked: true);
                }

                var masked = ScanWindow(payload[windowStart..windowEnd], budget, path, findings, out var residual);
                if (budget.Aborted)
                {
                    Add(findings, path, "redaction_budget_exceeded", 1);
                    return Finish(string.Empty, findings, blocked: true);
                }

                blocked |= residual;
                if (!blocked)
                {
                    output.Append(masked);
                }
            }
        }

        // The last window may finish after the deadline without any check having run in between.
        if (budget.Check())
        {
            Add(findings, "payload", "redaction_budget_exceeded", 1);
            return Finish(string.Empty, findings, blocked: true);
        }

        return Finish(blocked ? string.Empty : output.ToString(), findings, blocked);
    }

    private static RedactionOutcome Finish(string text, Dictionary<(string Path, string Rule), int> findings, bool blocked)
    {
        // A fixed order makes the report, and so its hash, a function of the findings alone.
        var ordered = findings
            .OrderBy(pair => pair.Key.Path, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Rule, StringComparer.Ordinal)
            .Select(pair => new RedactionFinding(pair.Key.Rule, pair.Key.Path, pair.Value))
            .ToList();
        return new RedactionOutcome(text, new RedactionReport(RedactionRules.Version, ordered), blocked);
    }

    private static void Add(Dictionary<(string Path, string Rule), int> findings, string path, string rule, int count) =>
        findings[(path, rule)] = findings.GetValueOrDefault((path, rule)) + count;

    /// <summary>Both passes over one window; <paramref name="residual"/> is true when pass two found anything.</summary>
    private static string ScanWindow(
        string text,
        ScanBudget budget,
        string path,
        Dictionary<(string Path, string Rule), int> findings,
        out bool residual)
    {
        budget.StartWindow();
        var counts = new Dictionary<string, int>();
        var masked = RedactionRules.Apply(text, counts);
        foreach (var (rule, count) in counts)
        {
            Add(findings, path, rule, count);
        }

        var hits = new Dictionary<string, int>();
        Scan(masked, label: null, depth: 0, budget, hits);
        ScanEntities(text, masked, budget, hits);
        if (budget.Truncated)
        {
            AddHit(hits, "residual_scan_truncated", 1);
        }

        foreach (var (rule, count) in hits)
        {
            Add(findings, path, rule, count);
        }

        residual = hits.Count > 0;
        return masked;
    }

    /// <summary>
    /// Pass two on one text: the rules must find nothing to change, and neither may they on any decoded form of it.
    /// </summary>
    private static void Scan(string text, string? label, int depth, ScanBudget budget, Dictionary<string, int> hits)
    {
        if (budget.Check())
        {
            return;
        }

        var counts = new Dictionary<string, int>();
        if (!string.Equals(RedactionRules.Apply(text, counts), text, StringComparison.Ordinal))
        {
            foreach (var (rule, count) in counts)
            {
                AddHit(hits, label ?? "residual_" + rule, count);
            }
        }

        if (depth >= MaxVariantDepth)
        {
            return;
        }

        foreach (var (decoded, variantLabel) in DecodedVariants(text))
        {
            Scan(decoded, variantLabel, depth + 1, budget, hits);
            if (budget.Aborted)
            {
                return;
            }
        }

        foreach (var decodedText in Base64Texts(text, budget))
        {
            Scan(decodedText, "residual_base64", depth + 1, budget, hits);
            if (budget.Aborted)
            {
                return;
            }
        }
    }

    private static void AddHit(Dictionary<string, int> hits, string rule, int count) =>
        hits[rule] = hits.GetValueOrDefault(rule) + count;

    /// <summary>The forms of <paramref name="text"/> that differ from it after one decoding step.</summary>
    private static IEnumerable<(string Text, string Label)> DecodedVariants(string text)
    {
        if (text.Contains('%'))
        {
            var decoded = Uri.UnescapeDataString(text);
            if (!string.Equals(decoded, text, StringComparison.Ordinal))
            {
                yield return (decoded, "residual_urlencoded");
            }
        }

        if (text.Contains("\\u", StringComparison.Ordinal))
        {
            var unescaped = UnicodeEscape.Replace(
                text,
                match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());
            if (!string.Equals(unescaped, text, StringComparison.Ordinal))
            {
                yield return (unescaped, "residual_unicode_escape");
            }
        }

        if (text.Contains('\\'))
        {
            var unescaped = JsonEscape.Replace(text, match => match.Groups[1].Value[0] switch
            {
                'n' => "\n",
                't' => "\t",
                var literal => literal.ToString(),
            });
            if (!string.Equals(unescaped, text, StringComparison.Ordinal))
            {
                yield return (unescaped, "residual_json_escape");
            }
        }

        if (Normalised(text) is { } normalised)
        {
            yield return (normalised, "residual_normalised");
        }
    }

    /// <summary>
    /// A prompt text has its ampersands entity-escaped, and a value rule may stop at the <c>;</c> of an entity and leave
    /// the rest of the value. The window read with entities decoded must mask the same spans as the masked window does.
    /// </summary>
    private static void ScanEntities(string original, string masked, ScanBudget budget, Dictionary<string, int> hits)
    {
        if (budget.Check() || !original.Contains('&'))
        {
            return;
        }

        var decoded = WebUtility.HtmlDecode(original);
        if (string.Equals(decoded, original, StringComparison.Ordinal))
        {
            return;
        }

        var expected = RedactionRules.Apply(decoded, new Dictionary<string, int>());
        if (!string.Equals(expected, WebUtility.HtmlDecode(masked), StringComparison.Ordinal))
        {
            AddHit(hits, "residual_html_entity", 1);
        }
    }

    /// <summary>The tokens of <paramref name="text"/> that decode to valid UTF-8 text, within the window's budget.</summary>
    private static List<string> Base64Texts(string text, ScanBudget budget)
    {
        var texts = new List<string>();
        foreach (var match in Base64Token.EnumerateMatches(text))
        {
            if (budget.Check())
            {
                break;
            }

            if (TryDecode(text.AsSpan(match.Index, match.Length)) is not { } decoded)
            {
                continue;
            }

            if (++budget.DecodedTokens > MaxBase64TokensPerWindow)
            {
                budget.Truncated = true;
                break;
            }

            texts.Add(decoded);
        }

        return texts;
    }

    private static string? TryDecode(ReadOnlySpan<char> token)
    {
        var normalised = new StringBuilder(token.Length + 3);
        foreach (var c in token)
        {
            _ = c switch
            {
                '-' => normalised.Append('+'),
                '_' => normalised.Append('/'),
                '=' => normalised,
                _ => normalised.Append(c),
            };
        }

        if (normalised.Length % 4 == 1)
        {
            return null;
        }

        normalised.Append('=', (4 - (normalised.Length % 4)) % 4);
        var bytes = new byte[(normalised.Length / 4 * 3) + 3];
        if (!Convert.TryFromBase64String(normalised.ToString(), bytes, out var written))
        {
            return null;
        }

        try
        {
            return StrictUtf8.GetString(bytes, 0, written);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// The payload cut at the start of every prompt data block, each part named for the field report: the fixed
    /// preamble, <c>segment:&lt;ordinal&gt;</c> per block, or the whole text as <c>payload</c> when there is no block.
    /// The ordinal, never the source id, because a source id is caller text and the report is stored.
    /// </summary>
    private static IEnumerable<(string Path, int Start, int End)> Sections(string payload)
    {
        var starts = new List<int>();
        for (var index = payload.IndexOf(BlockStart, StringComparison.Ordinal);
             index >= 0;
             index = payload.IndexOf(BlockStart, index + 1, StringComparison.Ordinal))
        {
            if (index == 0 || payload[index - 1] == '\n')
            {
                starts.Add(index);
            }
        }

        if (starts.Count == 0)
        {
            yield return ("payload", 0, payload.Length);
            yield break;
        }

        if (starts[0] > 0)
        {
            yield return ("preamble", 0, starts[0]);
        }

        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1] : payload.Length;
            yield return ("segment:" + (i + 1).ToString(CultureInfo.InvariantCulture), starts[i], end);
        }
    }

    /// <summary>A section cut into windows of at most <see cref="MaxWindowChars"/>; an end of -1 means no safe cut exists.</summary>
    private static IEnumerable<(int Start, int End)> Windows(string payload, int start, int end, ScanBudget budget)
    {
        for (var position = start; position < end;)
        {
            var limit = WindowEnd(payload, position, end, budget);
            yield return (position, limit);
            if (limit < 0)
            {
                yield break;
            }

            position = limit;
        }
    }

    /// <summary>
    /// The latest cut, after whitespace when one exists, where no rule joins the text on both sides. A window with no
    /// such cut is refused instead of cut where a keyword and its value would be parted.
    /// </summary>
    private static int WindowEnd(string payload, int position, int end, ScanBudget budget)
    {
        var limit = Math.Min(position + MaxWindowChars, end);
        if (limit >= end)
        {
            return end;
        }

        var cut = FindCut(payload, position, limit, end, whitespaceOnly: true, budget);
        return cut >= 0 ? cut : FindCut(payload, position, limit, end, whitespaceOnly: false, budget);
    }

    private static int FindCut(string payload, int position, int limit, int end, bool whitespaceOnly, ScanBudget budget)
    {
        var floor = position + (MaxWindowChars / 2);
        var failures = 0;
        for (var cut = limit; cut >= floor && failures < MaxSeamAttempts;)
        {
            var candidate = whitespaceOnly ? char.IsWhiteSpace(payload[cut - 1]) : !char.IsLowSurrogate(payload[cut]);
            if (!candidate)
            {
                cut--;
                continue;
            }

            if (budget.Check())
            {
                return -1;
            }

            if (SeamIsClosed(payload, position, cut, end))
            {
                return cut;
            }

            // The step doubles after every failure so 16 tries reach the far half of the window, not only the
            // characters beside the first unsafe cut.
            cut -= 1 << failures++;
        }

        return -1;
    }

    /// <summary>
    /// True when the rules mask the window so far and the text after the cut exactly as they mask the two joined. The
    /// left side is the whole window, so a quoted value or key body that opened anywhere in it counts, and the right
    /// side reaches a window length (or past a whitespace run) so the match can close. A value longer than that is not seen.
    /// </summary>
    private static bool SeamIsClosed(string payload, int position, int cut, int end)
    {
        var runEnd = cut;
        while (runEnd < end && char.IsWhiteSpace(payload[runEnd]))
        {
            runEnd++;
        }

        var left = payload[position..cut];
        var right = payload[cut..Math.Min(end, Math.Max(cut + MaxWindowChars, runEnd + SeamReach))];
        var joined = RedactionRules.Apply(left + right, new Dictionary<string, int>());
        var apart = RedactionRules.Apply(left, new Dictionary<string, int>())
            + RedactionRules.Apply(right, new Dictionary<string, int>());
        return string.Equals(joined, apart, StringComparison.Ordinal);
    }

    /// <summary>The deadline of one payload and the decode budget of the window being scanned.</summary>
    private sealed class ScanBudget(TimeProvider clock)
    {
        private readonly long _start = clock.GetTimestamp();

        internal bool Aborted { get; private set; }

        internal int DecodedTokens { get; set; }

        internal bool Truncated { get; set; }

        internal void StartWindow()
        {
            DecodedTokens = 0;
            Truncated = false;
        }

        /// <summary>True once the payload's time is spent; stays true afterwards.</summary>
        internal bool Check()
        {
            if (!Aborted && clock.GetElapsedTime(_start) > Budget)
            {
                Aborted = true;
            }

            return Aborted;
        }
    }
}
