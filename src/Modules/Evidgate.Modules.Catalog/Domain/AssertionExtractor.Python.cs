using System.Text;

namespace Evidgate.Modules.Catalog.Domain;

internal static partial class AssertionExtractor
{
    // ------------------------------------------------------------------ Python

    private static void ScanPython(string s, ScanState state)
    {
        var i = 0;
        var lineStart = true;
        while (i < s.Length && state.Found.Count < MaxDescriptors)
        {
            var c = s[i];
            if (c == '#')
            {
                while (i < s.Length && s[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                i = EndOfPythonString(s, i, state);
                lineStart = false;
                continue;
            }

            if (c == '\n')
            {
                lineStart = true;
                i++;
                continue;
            }

            if (c is ' ' or '\t' or '\r' or '\f' or ';' or ':')
            {
                // A statement may follow a semicolon or the colon of a one-line block (if c: assert x).
                lineStart = lineStart || c is ';' or ':';
                i++;
                continue;
            }

            if (IsIdentifierStart(c) && (i == 0 || !IsIdentifierPart(s[i - 1])))
            {
                var j = i;
                if (lineStart && Is(s, i, "assert") && IsBoundary(s, i + 6))
                {
                    j = i + 6;
                    var end = EndOfLogicalLine(s, j, state);
                    state.Found.Add("assert::" + Normalize(StripPythonComments(s[j..end])));
                    i = end;
                    lineStart = false;
                    continue;
                }

                if (Is(s, i, "expect") && IsBoundary(s, i + 6))
                {
                    j = i + 6;
                    ParseExpect(s, ref j, state, depth: 0);
                    i = Math.Max(j, i + 1);
                    lineStart = false;
                    continue;
                }

                ReadIdentifierEnd(s, ref j);
                lineStart = false;
                i = Math.Max(j, i + 1);

                continue;
            }

            if (c == '.')
            {
                var j = i + 1;
                var name = ReadIdentifier(s, ref j);
                if ((name.StartsWith("assert", StringComparison.Ordinal) || name is "raises" or "warns" or "approx") && TryArgs(s, ref j, out var arguments, state))
                {
                    state.Found.Add($"{name}::{Normalize(StripPythonComments(arguments))}");
                    i = j;
                    lineStart = false;
                    continue;
                }
            }

            lineStart = false;
            i++;
        }
    }

    private static int EndOfLogicalLine(string s, int from, ScanState state)
    {
        var depth = 0;
        var i = from;
        while (i < s.Length)
        {
            var c = s[i];
            if (c is '\'' or '"')
            {
                i = EndOfPythonString(s, i, state);
                continue;
            }

            if (c == '#')
            {
                while (i < s.Length && s[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (c == '\\' && i + 1 < s.Length && s[i + 1] is '\n' or '\r')
            {
                i += 2;
                continue;
            }
            else if (c == '\n' && depth == 0)
            {
                return i;
            }

            i++;
        }

        return s.Length;
    }

    /// <summary>Removes <c>#</c> comments (to the end of their physical line, keeping the line break) outside string literals.</summary>
    private static string StripPythonComments(string text)
    {
        if (text.IndexOf('#', StringComparison.Ordinal) < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var i = 0;
        var scratch = new ScanState();
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '\'' or '"')
            {
                var end = EndOfPythonString(text, i, scratch);
                builder.Append(text, i, end - i);
                i = end;
                continue;
            }

            if (c == '#')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            builder.Append(c);
            i++;
        }

        return builder.ToString();
    }

    private static int EndOfPythonString(string s, int start, ScanState state)
    {
        var quote = s[start];
        if (start + 2 < s.Length && s[start + 1] == quote && s[start + 2] == quote)
        {
            var end = s.IndexOf(new string(quote, 3), start + 3, StringComparison.Ordinal);
            if (end < 0)
            {
                state.Degraded = true;
            }

            return end < 0 ? s.Length : end + 3;
        }

        return EndOfString(s, start, state);
    }
}
