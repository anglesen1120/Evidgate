using System.Diagnostics;
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
/// also to the percent-decoded, <c>\uXXXX</c>-decoded and base64-decoded forms of the text, so a secret that was only
/// encoded is still caught. A payload that cannot be scanned inside its time budget is refused, never sent unscanned.
/// </summary>
/// <remarks>
/// Limits, by construction: the rules recognise secrets by shape or by the name beside them. A bare secret that has
/// neither, a secret split across two fields, a base64 token beyond the per-window decode budget and a rule-less
/// obfuscation (reversed or homoglyph text) are not seen. Windows are cut at a line or word boundary, so a value
/// that is itself longer than a window is cut in the middle.
/// </remarks>
internal sealed class RedactionPipeline : IRedactionPipeline
{
    /// <summary>The longest text one rule pass is given.</summary>
    internal const int MaxWindowChars = 64 * 1024;

    /// <summary>How long one payload may take in total before it is refused.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private const string BlockStart = "<<<EVG-DATA ";
    private const int MaxVariantDepth = 2;
    private const int MaxBase64TokensPerText = 256;
    private const int MaxBase64DecodedBytesPerText = 1024 * 1024;
    private const int MinBase64TokenChars = 24;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Regex Base64Token = new(
        @"[A-Za-z0-9+/_\-]{24,}={0,2}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex UnicodeEscape = new(
        @"\\u([0-9a-fA-F]{4})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <inheritdoc />
    public RedactionOutcome Redact(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var clock = Stopwatch.StartNew();
        var findings = new Dictionary<(string Path, string Rule), int>();
        var output = new StringBuilder(payload.Length);
        var blocked = false;

        foreach (var (path, start, end) in Sections(payload))
        {
            foreach (var (windowStart, windowEnd) in Windows(payload, start, end))
            {
                if (clock.Elapsed > Budget)
                {
                    Add(findings, path, "redaction_budget_exceeded", 1);
                    return Finish(string.Empty, findings, blocked: true);
                }

                var counts = new Dictionary<string, int>();
                var masked = RedactionRules.Apply(payload[windowStart..windowEnd], counts);
                foreach (var (rule, count) in counts)
                {
                    Add(findings, path, rule, count);
                }

                var residual = new Dictionary<string, int>();
                Scan(masked, label: null, depth: 0, residual);
                foreach (var (rule, count) in residual)
                {
                    Add(findings, path, rule, count);
                    blocked = true;
                }

                if (!blocked)
                {
                    output.Append(masked);
                }
            }
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

    /// <summary>
    /// Pass two on one text: the rules must find nothing to change, and neither may they on any decoded form of it.
    /// </summary>
    private static void Scan(string text, string? label, int depth, Dictionary<string, int> hits)
    {
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

        if (text.Contains('%'))
        {
            var decoded = Uri.UnescapeDataString(text);
            if (!string.Equals(decoded, text, StringComparison.Ordinal))
            {
                Scan(decoded, "residual_urlencoded", depth + 1, hits);
            }
        }

        if (text.Contains("\\u", StringComparison.Ordinal))
        {
            var unescaped = UnicodeEscape.Replace(
                text,
                match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());
            Scan(unescaped, "residual_unicode_escape", depth + 1, hits);
        }

        foreach (var decodedText in Base64Texts(text))
        {
            Scan(decodedText, "residual_base64", depth + 1, hits);
        }
    }

    private static void AddHit(Dictionary<string, int> hits, string rule, int count) =>
        hits[rule] = hits.GetValueOrDefault(rule) + count;

    /// <summary>The tokens of <paramref name="text"/> that decode, within a budget, to valid UTF-8 text.</summary>
    private static List<string> Base64Texts(string text)
    {
        var texts = new List<string>();
        var tokens = 0;
        var decodedBytes = 0;
        foreach (var match in Base64Token.EnumerateMatches(text))
        {
            if (tokens++ >= MaxBase64TokensPerText || decodedBytes >= MaxBase64DecodedBytesPerText)
            {
                break;
            }

            if (match.Length < MinBase64TokenChars || TryDecode(text.AsSpan(match.Index, match.Length)) is not { } decoded)
            {
                continue;
            }

            decodedBytes += decoded.Length;
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
    /// preamble, <c>segment:&lt;source id&gt;</c> per block, or the whole text as <c>payload</c> when there is no block.
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
            yield return (SegmentPath(payload, starts[i], end), starts[i], end);
        }
    }

    private static string SegmentPath(string payload, int start, int end)
    {
        var lineEnd = payload.IndexOf('\n', start, end - start);
        var header = payload.AsSpan(start, (lineEnd < 0 ? end : lineEnd) - start);
        var idStart = header.IndexOf(" id=", StringComparison.Ordinal);
        if (idStart < 0)
        {
            return "segment:unknown";
        }

        var id = header[(idStart + 4)..];
        var close = id.IndexOf(">>>", StringComparison.Ordinal);
        id = close < 0 ? id : id[..close];
        foreach (var c in id)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-') || id.Length > 128)
            {
                return "segment:unknown";
            }
        }

        return "segment:" + id.ToString();
    }

    /// <summary>A section cut into windows of at most <see cref="MaxWindowChars"/>, at a line break or a space when one exists.</summary>
    private static IEnumerable<(int Start, int End)> Windows(string payload, int start, int end)
    {
        for (var position = start; position < end;)
        {
            var limit = WindowEnd(payload, position, end);
            yield return (position, limit);
            position = limit;
        }
    }

    private static int WindowEnd(string payload, int position, int end)
    {
        var limit = Math.Min(position + MaxWindowChars, end);
        if (limit >= end)
        {
            return end;
        }

        var span = payload.AsSpan(position, limit - position);
        var cut = span.LastIndexOf('\n');
        if (cut < span.Length / 2)
        {
            cut = span.LastIndexOf(' ');
        }

        return cut >= span.Length / 2 ? position + cut + 1 : limit;
    }
}
