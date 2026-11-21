using System.Globalization;
using System.Text;

namespace Evidgate.Modules.Catalog.Domain;

/// <summary>
/// A bounded scanner for the string and template literals of rendered template text, so a conformance rule sees
/// what a TypeScript file actually carries instead of the raw characters around it. It follows quotes of all three
/// kinds, escapes, both comment forms and nested <c>${…}</c> bodies; anything it cannot finish (an unterminated
/// quote, comment, template or substitution, or a file over the cap) is reported as incomplete and refused.
/// </summary>
/// <remarks>
/// This is not a TypeScript parser: a regular-expression literal that holds a quote is read as a string, which can
/// only make a rule stricter. Rules that must be exact run in the Node CLI over the compiler's AST.
/// </remarks>
internal static class TemplateLiteralScanner
{
    /// <summary>Largest file scanned; a bigger one is incomplete, never partially read.</summary>
    internal const int MaxChars = 65536;

    /// <summary>One literal: where it opens in the source, and its text with simple escapes decoded.</summary>
    /// <param name="Offset">Index of the opening quote, or of the segment inside a template literal.</param>
    /// <param name="Value">The literal's text.</param>
    internal readonly record struct Literal(int Offset, string Value);

    /// <summary>The scan: whether the source ended cleanly, and every literal it holds.</summary>
    /// <param name="Complete">False when some literal, comment or template never closed.</param>
    /// <param name="Literals">The literals in source order.</param>
    internal readonly record struct ScanResult(bool Complete, IReadOnlyList<Literal> Literals);

    private readonly record struct Frame(bool InTemplate, int Depth, int SegmentStart);

    /// <summary>Scans one rendered file.</summary>
    /// <param name="source">The file's text.</param>
    internal static ScanResult Scan(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var literals = new List<Literal>();
        if (source.Length > MaxChars)
        {
            return new ScanResult(false, literals);
        }

        var frames = new List<Frame> { new(InTemplate: false, Depth: 0, SegmentStart: 0) };
        var index = 0;

        while (index < source.Length)
        {
            if (frames[^1].InTemplate)
            {
                index = StepTemplate(source, frames, literals, index);
                continue;
            }

            index = StepCode(source, frames, literals, index);
            if (index < 0)
            {
                return new ScanResult(false, literals);
            }
        }

        return new ScanResult(frames.Count == 1 && !frames[0].InTemplate, literals);
    }

    private static int StepTemplate(string source, List<Frame> frames, List<Literal> literals, int index)
    {
        var frame = frames[^1];
        switch (source[index])
        {
            case '\\':
                return index + 2;
            case '`':
                AddSegment(source, frame.SegmentStart, index, literals);
                frames.RemoveAt(frames.Count - 1);
                return index + 1;
            case '$' when index + 1 < source.Length && source[index + 1] == '{':
                AddSegment(source, frame.SegmentStart, index, literals);
                frames.Add(new Frame(InTemplate: false, Depth: 0, SegmentStart: 0));
                return index + 2;
            default:
                return index + 1;
        }
    }

    private static int StepCode(string source, List<Frame> frames, List<Literal> literals, int index)
    {
        var character = source[index];
        switch (character)
        {
            case '\'':
            case '"':
                var close = FindQuoteEnd(source, index + 1, character);
                if (close < 0)
                {
                    return -1;
                }

                literals.Add(new Literal(index, Unescape(source[(index + 1)..close])));
                return close + 1;
            case '`':
                frames.Add(new Frame(InTemplate: true, Depth: 0, SegmentStart: index + 1));
                return index + 1;
            case '/' when index + 1 < source.Length && source[index + 1] == '/':
                var lineEnd = source.IndexOf('\n', index + 2);
                return lineEnd < 0 ? source.Length : lineEnd + 1;
            case '/' when index + 1 < source.Length && source[index + 1] == '*':
                var commentEnd = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                return commentEnd < 0 ? -1 : commentEnd + 2;
            case '{':
                frames[^1] = frames[^1] with { Depth = frames[^1].Depth + 1 };
                return index + 1;
            case '}' when frames[^1].Depth == 0 && frames.Count > 1:
                frames.RemoveAt(frames.Count - 1);
                if (frames[^1].InTemplate)
                {
                    frames[^1] = frames[^1] with { SegmentStart = index + 1 };
                }

                return index + 1;
            case '}' when frames[^1].Depth > 0:
                frames[^1] = frames[^1] with { Depth = frames[^1].Depth - 1 };
                return index + 1;
            default:
                return index + 1;
        }
    }

    private static void AddSegment(string source, int start, int end, List<Literal> literals)
    {
        if (end < start)
        {
            return;
        }

        literals.Add(new Literal(start, Unescape(source[start..end])));
    }

    private static int FindQuoteEnd(string source, int start, char quote)
    {
        for (var index = start; index < source.Length; index++)
        {
            var character = source[index];
            if (character == '\\')
            {
                index++;
                continue;
            }

            if (character == quote)
            {
                return index;
            }

            if (character is '\n' or '\r')
            {
                return -1;
            }
        }

        return -1;
    }

    private static string Unescape(string text)
    {
        if (!text.Contains('\\', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\\' || index + 1 >= text.Length)
            {
                builder.Append(text[index]);
                continue;
            }

            var escaped = text[++index];
            switch (escaped)
            {
                case 'n':
                    builder.Append('\n');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case 'b':
                    builder.Append('\b');
                    break;
                case 'f':
                    builder.Append('\f');
                    break;
                case 'v':
                    builder.Append('\v');
                    break;
                case '0':
                    builder.Append('\0');
                    break;
                case 'x' when index + 2 < text.Length && int.TryParse(text.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var simple):
                    builder.Append((char)simple);
                    index += 2;
                    break;
                case 'u' when index + 1 < text.Length && text[index + 1] == '{':
                    index = AppendCodePoint(text, builder, index + 2);
                    break;
                case 'u' when index + 4 < text.Length && int.TryParse(text.AsSpan(index + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                    builder.Append((char)code);
                    index += 4;
                    break;
                default:
                    builder.Append(escaped);
                    break;
            }
        }

        return builder.ToString();
    }

    private static int AppendCodePoint(string text, StringBuilder builder, int start)
    {
        var end = text.IndexOf('}', start);
        if (end < 0)
        {
            builder.Append("\\u");
            return start - 2;
        }

        if (int.TryParse(text.AsSpan(start, end - start), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)
            && code is > 0 and <= 0x10FFFF && (code < 0xD800 || code > 0xDFFF))
        {
            builder.Append(char.ConvertFromUtf32(code));
        }

        return end;
    }
}
