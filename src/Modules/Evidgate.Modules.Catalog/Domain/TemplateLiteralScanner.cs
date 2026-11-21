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
/// This is not a TypeScript parser: a <c>/</c> where a regular-expression literal could begin (the start of the input,
/// after <c>( , = : [ ! &amp; | ? { } ;</c>, or after a keyword that ends an expression such as <c>return</c> or
/// <c>typeof</c>) ends the scan as incomplete instead of reading a quote inside it as a string, while division such as
/// <c>a / b</c> keeps scanning. Rules that must be exact run in the Node CLI over the compiler's AST.
/// </remarks>
internal static class TemplateLiteralScanner
{
    /// <summary>Largest file scanned; a bigger one is incomplete, never partially read.</summary>
    internal const int MaxChars = TemplateLimits.MaxTextChars;

    /// <summary>
    /// Keywords after which a <c>/</c> begins a regular expression rather than division, as a JavaScript lexer reads
    /// them: each ends an expression that still expects a value.
    /// </summary>
    private static readonly string[] RegexKeywords =
    [
        "return", "typeof", "instanceof", "in", "of", "new", "delete", "void",
        "throw", "case", "do", "else", "yield", "await",
    ];

    /// <summary>One literal: where it opens in the source, and its text with simple escapes decoded.</summary>
    /// <param name="Offset">
    /// Index of the opening quote or backtick. A template segment carries its template's opening backtick, not the
    /// segment start, so a rule can still find the key the template is the value of.
    /// </param>
    /// <param name="Value">The literal's text.</param>
    internal readonly record struct Literal(int Offset, string Value);

    /// <summary>The scan: whether the source ended cleanly, and every literal it holds.</summary>
    /// <param name="Complete">False when some literal, comment or template never closed.</param>
    /// <param name="Literals">The literals in source order.</param>
    internal readonly record struct ScanResult(bool Complete, IReadOnlyList<Literal> Literals);

    /// <summary>One nesting level: a template keeps its opening backtick so each segment can still be linked to its key.</summary>
    private readonly record struct Frame(bool InTemplate, int Depth, int ValueStart, int Offset);

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

        var frames = new List<Frame> { new(InTemplate: false, Depth: 0, ValueStart: 0, Offset: 0) };
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
                AddSegment(source, frame.ValueStart, index, frame.Offset, literals);
                frames.RemoveAt(frames.Count - 1);
                return index + 1;
            case '$' when index + 1 < source.Length && source[index + 1] == '{':
                AddSegment(source, frame.ValueStart, index, frame.Offset, literals);
                frames.Add(new Frame(InTemplate: false, Depth: 0, ValueStart: 0, Offset: 0));
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
                frames.Add(new Frame(InTemplate: true, Depth: 0, ValueStart: index + 1, Offset: index));
                return index + 1;
            case '/' when index + 1 < source.Length && source[index + 1] == '/':
                return SkipLineComment(source, index + 2);
            case '/' when index + 1 < source.Length && source[index + 1] == '*':
                var commentEnd = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                return commentEnd < 0 ? -1 : commentEnd + 2;
            case '/':
                return MayStartRegex(source, index) ? -1 : index + 1;
            case '{':
                frames[^1] = frames[^1] with { Depth = frames[^1].Depth + 1 };
                return index + 1;
            case '}' when frames[^1].Depth == 0 && frames.Count > 1:
                frames.RemoveAt(frames.Count - 1);
                if (frames[^1].InTemplate)
                {
                    frames[^1] = frames[^1] with { ValueStart = index + 1 };
                }

                return index + 1;
            case '}' when frames[^1].Depth > 0:
                frames[^1] = frames[^1] with { Depth = frames[^1].Depth - 1 };
                return index + 1;
            default:
                return index + 1;
        }
    }

    private static void AddSegment(string source, int start, int end, int offset, List<Literal> literals)
    {
        if (end < start)
        {
            return;
        }

        literals.Add(new Literal(offset, Unescape(source[start..end])));
    }

    /// <summary>True when a <c>/</c> sits where a regular-expression literal may begin, so the scan cannot tell it from division.</summary>
    private static bool MayStartRegex(string source, int index)
    {
        var previous = index - 1;
        while (previous >= 0 && char.IsWhiteSpace(source[previous]))
        {
            previous--;
        }

        if (previous < 0)
        {
            return true;
        }

        if (source[previous] is '(' or ',' or '=' or ':' or '[' or '!' or '&' or '|' or '?' or '{' or '}' or ';')
        {
            return true;
        }

        if (!IsIdentifierPart(source[previous]))
        {
            return false;
        }

        var start = previous;
        while (start > 0 && IsIdentifierPart(source[start - 1]))
        {
            start--;
        }

        return !FollowsMemberAccess(source, start) && IsRegexKeyword(source.AsSpan(start, previous + 1 - start));
    }

    /// <summary>True for a character that continues a JavaScript identifier, so a keyword has to match the whole run.</summary>
    private static bool IsIdentifierPart(char character) =>
        char.IsLetterOrDigit(character)
        || character is '_' or '$' or '\u200C' or '\u200D'
        || CharUnicodeInfo.GetUnicodeCategory(character) is
            UnicodeCategory.LetterNumber or
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.ConnectorPunctuation;

    /// <summary>True when the token is a member name after a dot, so it names a property and is not the keyword itself.</summary>
    private static bool FollowsMemberAccess(string source, int tokenStart)
    {
        var previous = tokenStart - 1;
        while (previous >= 0 && char.IsWhiteSpace(source[previous]))
        {
            previous--;
        }

        return previous >= 0 && source[previous] == '.';
    }

    private static bool IsRegexKeyword(ReadOnlySpan<char> token)
    {
        foreach (var keyword in RegexKeywords)
        {
            if (token.SequenceEqual(keyword))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A line comment ends at any JavaScript line terminator, not only at a line feed.</summary>
    private static int SkipLineComment(string source, int index)
    {
        for (var cursor = index; cursor < source.Length; cursor++)
        {
            if (source[cursor] is '\n' or '\r' or '\u2028' or '\u2029')
            {
                return cursor + 1;
            }
        }

        return source.Length;
    }

    private static int FindQuoteEnd(string source, int start, char quote)
    {
        for (var index = start; index < source.Length; index++)
        {
            var character = source[index];
            if (character == '\\')
            {
                // A backslash before CRLF escapes the whole sequence, as it does before any other line terminator.
                index += index + 2 < source.Length && source[index + 1] == '\r' && source[index + 2] == '\n' ? 2 : 1;
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
            if (!IsLineContinuation(text, ref index, escaped))
            {
                AppendEscape(text, builder, ref index, escaped);
            }
        }

        return builder.ToString();
    }

    /// <summary>True for a backslash before a line terminator, which a JavaScript literal drops instead of carrying.</summary>
    private static bool IsLineContinuation(string text, ref int index, char escaped)
    {
        if (escaped is '\n' or '\u2028' or '\u2029')
        {
            return true;
        }

        if (escaped != '\r')
        {
            return false;
        }

        if (index + 1 < text.Length && text[index + 1] == '\n')
        {
            index++;
        }

        return true;
    }

    private static void AppendEscape(string text, StringBuilder builder, ref int index, char escaped)
    {
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
