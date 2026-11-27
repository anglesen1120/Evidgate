using System.Globalization;

namespace Evidgate.Modules.Healing.Application;

internal sealed partial class RedactionPipeline
{
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
    /// The latest cut, after whitespace when one exists, where no rule joins the text on both sides, as read plain or
    /// through any decoder. A window with no such cut is refused instead of cut where a keyword and its value would be parted.
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

    internal static int FindCut(string payload, int position, int limit, int end, bool whitespaceOnly, ScanBudget budget)
    {
        // A whitespace cut may sit anywhere in the window: a short line before one long unbreakable token is the only
        // place the rest can be cut, and a cut inside that token is refused. Other cuts stay in the far half.
        var floor = whitespaceOnly ? position + 1 : position + (MaxWindowChars / 2);
        var failures = 0;
        for (var cut = limit; cut >= floor && failures < MaxSeamAttempts;)
        {
            var candidate = whitespaceOnly
                ? char.IsWhiteSpace(payload[cut - 1])
                : !char.IsLowSurrogate(payload[cut]) && !SplitsToken(payload, cut);
            if (!candidate)
            {
                cut--;
                continue;
            }

            if (budget.Check())
            {
                return -1;
            }

            // One attempt runs the rules over up to two windows, so the deadline is read again before its answer counts.
            var closed = SeamIsClosed(payload, position, cut, end);
            if (budget.Check())
            {
                return -1;
            }

            if (closed)
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
    /// True when the rules mask the window so far and the text after the cut exactly as they mask the two joined,
    /// read plain and through every decoder. The left side is the whole window, so a quoted value or key body that
    /// opened anywhere in it counts, and the right side reaches a window length (or past a whitespace run) so the match
    /// can close. A value longer than that is not seen. A decoder that changes the joined text differently from the
    /// parts (an escape or entity cut in two) closes nothing, so the cut moves.
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
        var joined = left + right;
        if (!MasksAlike(joined, left, right))
        {
            return false;
        }

        foreach (var (_, decode) in TextDecoders)
        {
            if (decode(joined) is { } decodedJoined
                && !MasksAlike(decodedJoined, decode(left) ?? left, decode(right) ?? right))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MasksAlike(string joined, string left, string right) =>
        string.Equals(
            RedactionRules.Apply(joined, new Dictionary<string, int>()),
            RedactionRules.Apply(left, new Dictionary<string, int>())
            + RedactionRules.Apply(right, new Dictionary<string, int>()),
            StringComparison.Ordinal);

    // A base64 or hex token cut in two decodes to nothing on either side. Whitespace cuts never split one.
    private static bool SplitsToken(string payload, int cut)
    {
        var before = 0;
        for (var i = cut - 1; i >= 0 && before < 7 && IsTokenChar(payload[i]); i--)
        {
            before++;
        }

        var after = 0;
        for (var i = cut; i < payload.Length && after < 7 && IsTokenChar(payload[i]); i++)
        {
            after++;
        }

        return before > 0 && after > 0 && before + after >= 7;
    }

    private static bool IsTokenChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '+' or '/' or '_' or '-' or '=';
}
