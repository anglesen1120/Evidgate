using System.Text;

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
/// also to every decoded form of the text: percent-decoded, <c>+</c> read as a space, <c>\uXXXX</c>, <c>\u{X}</c>,
/// <c>\UXXXXXXXX</c> and <c>\xHH</c>-decoded, JSON-unescaped, HTML-entity-decoded, hex-text and base64-decoded, the
/// forms without invisible characters (removed, each run read as a space, or fillers read as spaces and the rest
/// removed) and with fullwidth look-alikes mapped to ASCII; it also compares the HTML-entity-decoded form of the window
/// with its masked text. A payload that cannot be scanned inside its time budget, whose decode or rule-pass budget
/// runs out, that still decodes after the layer limit, or whose window cannot be cut where no rule spans the seam, is
/// refused, never sent unscanned.
/// </summary>
/// <remarks>
/// Limits, by construction: the rules recognise secrets by shape or by the name beside them. A bare secret that has
/// neither, a secret split across two fields, a rule-less obfuscation (reversed or homoglyph text) and a value
/// that is itself longer than a window (a quoted value or key body that closes more than one window past the cut) are
/// not seen. Decoders compose up to <see cref="MaxVariantDepth"/> layers; a window whose forms still decode at that
/// depth is refused. A text that mixes invisible characters some of which must read as spaces and others as joiners
/// in a way none of the three readings matches (the same code point read both ways) is not seen. A window with no cut
/// where the rules mask the same text joined and apart is refused, and a window without whitespace is never cut
/// inside a base64- or hex-shaped token.
/// </remarks>
internal sealed partial class RedactionPipeline(TimeProvider clock) : IRedactionPipeline
{
    /// <summary>The longest text one rule pass is given.</summary>
    internal const int MaxWindowChars = 64 * 1024;

    /// <summary>How long one payload may take in total before it is refused.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private const string BlockStart = "<<<EVG-DATA ";
    private const int MaxVariantDepth = 3;

    // A 64K window holds at most about 8,000 base64-shaped tokens of seven characters or more; ordinary dense markup
    // and identifier lists decode far fewer to valid UTF-8, so only a window built to exhaust the budget reaches it.
    private const int MaxBase64TokensPerWindow = 4096;
    private const int SeamReach = 256;
    private const int MaxSeamAttempts = 16;

    // Rule passes over one window's text and all its decoded forms: 16 window lengths. Ordinary text decodes to a few
    // forms; only one built to multiply them reaches it, and it is then refused.
    private const int MaxScannedCharsPerWindow = 16 * MaxWindowChars;

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
    /// A text that still decodes after <see cref="MaxVariantDepth"/> layers truncates the window, which refuses it.
    /// </summary>
    private static void Scan(string text, string? label, int depth, ScanBudget budget, Dictionary<string, int> hits)
    {
        budget.ScannedChars += text.Length;
        if (budget.Check())
        {
            return;
        }

        if (budget.ScannedChars > MaxScannedCharsPerWindow)
        {
            budget.Truncated = true;
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

        if (RedactionRules.HasResidualPrivateKey(text))
        {
            AddHit(hits, label ?? "residual_private_key", 1);
        }

        if (depth >= MaxVariantDepth)
        {
            budget.Truncated |= DecodesFurther(text, budget);
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

        foreach (var (decodedText, textLabel) in Base64Texts(text, budget).Select(value => (value, "residual_base64"))
                     .Concat(HexTexts(text, budget).Select(value => (value, "residual_hex"))))
        {
            Scan(decodedText, textLabel, depth + 1, budget, hits);
            if (budget.Aborted)
            {
                return;
            }
        }
    }

    private static void AddHit(Dictionary<string, int> hits, string rule, int count) =>
        hits[rule] = hits.GetValueOrDefault(rule) + count;
}
