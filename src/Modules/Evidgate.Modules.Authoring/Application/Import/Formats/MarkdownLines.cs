using System.Text;

namespace Evidgate.Modules.Authoring.Application.Import.Formats;

/// <summary>
/// The line-level rules of the Markdown requirement reader: what a heading, a list marker, a fence and a Gherkin step
/// line look like. Each rule reads one line and allocates nothing but the substring it returns, so a hostile file costs
/// time linear in its size.
/// </summary>
internal static class MarkdownLines
{
    private static readonly string[] StepKeywords = ["Given", "When", "Then", "And", "But"];

    private static readonly string[] FenceKeywords = ["Feature", "Scenario", "Given", "When", "Then"];

    /// <summary>The number of leading columns of <paramref name="line"/>, a tab advancing to the next multiple of four.</summary>
    /// <param name="line">The line.</param>
    /// <param name="start">Set to the index of the first character that is not a space or a tab.</param>
    internal static int Indent(string line, out int start)
    {
        var columns = 0;
        start = 0;
        while (start < line.Length && line[start] is ' ' or '\t')
        {
            columns = line[start] == '\t' ? columns + 4 - (columns % 4) : columns + 1;
            start++;
        }

        return columns;
    }

    /// <summary>True when the line holds nothing but spaces and tabs.</summary>
    /// <param name="line">The line.</param>
    internal static bool IsBlank(string line)
    {
        Indent(line, out var start);

        return start == line.Length;
    }

    /// <summary>
    /// An ATX heading: one to six <c>#</c> after at most three columns of indent, then a space or the end of the line.
    /// Setext headings (an underline of <c>=</c> or <c>-</c>) are deliberately not headings.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="title">The heading text, with the optional closing <c>#</c> run removed and trimmed.</param>
    internal static bool TryHeading(string line, out string title)
    {
        title = string.Empty;
        if (Indent(line, out var start) > 3)
        {
            return false;
        }

        var end = start;
        while (end < line.Length && line[end] == '#')
        {
            end++;
        }

        var level = end - start;
        if (level is < 1 or > 6 || (end < line.Length && line[end] is not (' ' or '\t')))
        {
            return false;
        }

        title = StripClosing(line[end..].Trim());

        return true;
    }

    /// <summary>
    /// A list item opener: <c>-</c>, <c>*</c>, <c>+</c> or up to nine digits and <c>.</c> or <c>)</c>, followed by a space,
    /// a tab or the end of the line. A thematic break (<c>---</c>, <c>* * *</c>) is not an item.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="indent">The columns before the marker.</param>
    /// <param name="contentIndent">The columns at which the item's text starts (continuation lines are measured against it).</param>
    /// <param name="text">The text after the marker, trimmed at its start.</param>
    internal static bool TryMarker(string line, out int indent, out int contentIndent, out string text)
    {
        indent = Indent(line, out var start);
        contentIndent = 0;
        text = string.Empty;

        var markerEnd = MarkerEnd(line, start);
        if (markerEnd < 0 || IsThematicBreak(line))
        {
            return false;
        }

        var spaces = 0;
        while (markerEnd + spaces < line.Length && line[markerEnd + spaces] is ' ' or '\t')
        {
            spaces++;
        }

        var width = markerEnd - start;
        var rest = line[(markerEnd + spaces)..];
        contentIndent = indent + width + (spaces is >= 1 and <= 4 && rest.Length > 0 ? spaces : 1);
        text = rest;

        return true;
    }

    /// <summary>A fenced code block delimiter: three or more backticks or tildes after at most three columns of indent.</summary>
    /// <param name="line">The line.</param>
    /// <param name="fence">The fence character.</param>
    /// <param name="length">The fence run length.</param>
    /// <param name="info">The text after the run, trimmed.</param>
    internal static bool TryFence(string line, out char fence, out int length, out string info)
    {
        fence = '\0';
        length = 0;
        info = string.Empty;
        if (Indent(line, out var start) > 3 || start >= line.Length || line[start] is not ('`' or '~'))
        {
            return false;
        }

        fence = line[start];
        var end = start;
        while (end < line.Length && line[end] == fence)
        {
            end++;
        }

        length = end - start;
        info = line[end..].Trim();

        // A backtick fence's info string may not hold a backtick, otherwise it is inline code.
        return length >= 3 && !(fence == '`' && info.Contains('`'));
    }

    /// <summary>True when <paramref name="line"/> closes a fence opened with <paramref name="fence"/> and <paramref name="length"/>.</summary>
    /// <param name="line">The line.</param>
    /// <param name="fence">The opening fence character.</param>
    /// <param name="length">The opening run length.</param>
    internal static bool ClosesFence(string line, char fence, int length) =>
        TryFence(line, out var closing, out var run, out var info)
        && closing == fence
        && run >= length
        && info.Length == 0;

    /// <summary>True when the line starts with a Gherkin step keyword (case-sensitive) and then a space or the end of the line.</summary>
    /// <param name="line">The line.</param>
    internal static bool IsStep(string line) => StartsWithKeyword(line, StepKeywords, requireWordEnd: true);

    /// <summary>True for a Gherkin data-table row: a <c>|</c> after optional indentation.</summary>
    /// <param name="line">The line.</param>
    internal static bool IsTableRow(string line)
    {
        Indent(line, out var start);

        return start < line.Length && line[start] == '|';
    }

    /// <summary>True when the line opens a Gherkin docstring: <c>"""</c> after optional indentation, then an optional content type.</summary>
    /// <param name="line">The line.</param>
    internal static bool OpensDocString(string line)
    {
        Indent(line, out var start);

        return string.CompareOrdinal(line, start, "\"\"\"", 0, 3) == 0;
    }

    /// <summary>True when the line closes a docstring: <c>"""</c> alone between optional spaces and tabs.</summary>
    /// <param name="line">The line.</param>
    internal static bool ClosesDocString(string line) => line.AsSpan().Trim(" \t").SequenceEqual("\"\"\"");

    /// <summary>True when the text opens a Gherkin document or step: Feature, Scenario, Given, When or Then.</summary>
    /// <param name="line">The first non-blank line of a fenced block.</param>
    internal static bool IsGherkinOpening(string line) => StartsWithKeyword(line, FenceKeywords, requireWordEnd: false);

    /// <summary>True for a line of three or more <c>-</c>, <c>*</c> or <c>_</c> separated only by spaces.</summary>
    /// <param name="line">The line.</param>
    internal static bool IsThematicBreak(string line)
    {
        if (Indent(line, out var start) > 3 || start >= line.Length || line[start] is not ('-' or '*' or '_'))
        {
            return false;
        }

        var mark = line[start];
        var count = 0;
        for (var index = start; index < line.Length; index++)
        {
            if (line[index] == mark)
            {
                count++;
            }
            else if (line[index] is not (' ' or '\t'))
            {
                return false;
            }
        }

        return count >= 3;
    }

    /// <summary>The line without up to <paramref name="columns"/> leading columns of space (a tab counts as four).</summary>
    /// <param name="line">The line.</param>
    /// <param name="columns">The indent to remove.</param>
    internal static string Dedent(string line, int columns)
    {
        var removed = 0;
        var index = 0;
        while (index < line.Length && removed < columns && line[index] is ' ' or '\t')
        {
            var width = line[index] == '\t' ? 4 - (removed % 4) : 1;
            if (removed + width > columns)
            {
                break;
            }

            removed += width;
            index++;
        }

        return line[index..];
    }

    /// <summary>The longest prefix of <paramref name="text"/> of at most <paramref name="codePoints"/> code points.</summary>
    /// <param name="text">The text.</param>
    /// <param name="codePoints">The limit.</param>
    internal static string FirstCodePoints(string text, int codePoints)
    {
        var count = 0;
        var end = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (count == codePoints)
            {
                break;
            }

            count++;
            end += rune.Utf16SequenceLength;
        }

        return text[..end];
    }

    private static int MarkerEnd(string line, int start)
    {
        if (start >= line.Length)
        {
            return -1;
        }

        var end = start;
        if (line[start] is '-' or '*' or '+')
        {
            end = start + 1;
        }
        else
        {
            while (end < line.Length && end - start < 9 && char.IsAsciiDigit(line[end]))
            {
                end++;
            }

            if (end == start || end >= line.Length || line[end] is not ('.' or ')'))
            {
                return -1;
            }

            end++;
        }

        return end == line.Length || line[end] is ' ' or '\t' ? end : -1;
    }

    private static string StripClosing(string text)
    {
        var end = text.Length;
        while (end > 0 && text[end - 1] == '#')
        {
            end--;
        }

        // The closing run only counts when a space separates it from the text (or when it is the whole heading).
        return end == 0 || text[end - 1] is ' ' or '\t' ? text[..end].TrimEnd() : text;
    }

    private static bool StartsWithKeyword(string line, string[] keywords, bool requireWordEnd)
    {
        Indent(line, out var start);
        foreach (var keyword in keywords)
        {
            if (string.CompareOrdinal(line, start, keyword, 0, keyword.Length) != 0)
            {
                continue;
            }

            var next = start + keyword.Length;
            var boundary = next == line.Length || line[next] is ' ' or '\t' || (!requireWordEnd && line[next] == ':');
            if (boundary)
            {
                return true;
            }
        }

        return false;
    }
}
