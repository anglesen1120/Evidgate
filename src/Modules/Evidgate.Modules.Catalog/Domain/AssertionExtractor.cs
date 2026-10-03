using System.Security.Cryptography;
using System.Text;

namespace Evidgate.Modules.Catalog.Domain;

/// <summary>The assertion descriptors found in one test file.</summary>
/// <param name="Extracted">False when the framework is not one this extractor understands (the descriptor list is empty then).</param>
/// <param name="Descriptors">One short, normalised text per assertion, in source order.</param>
internal sealed record AssertionExtraction(bool Extracted, IReadOnlyList<string> Descriptors);

/// <summary>
/// Extracts assertion descriptors from test source (03.1.3-T1.2). A descriptor keeps what makes an assertion
/// <em>this</em> assertion — the matcher, a negation, and the expected-value arguments — and drops its subject, so
/// healing a locator (<c>expect(page.locator('#a')).toBeVisible()</c> → <c>'#b'</c>) leaves the hash alone while a
/// removed, weakened or re-targeted assertion changes it. Comments and string literals are skipped; the scan is one
/// linear pass with bounded nesting, and a file with more than <see cref="MaxDescriptors"/> assertions is truncated
/// deterministically. Frameworks: JavaScript/TypeScript (Playwright, Jest, Vitest, Mocha, Jasmine, Cypress) and
/// Python (pytest, unittest); any other framework yields an empty, flagged extraction.
/// </summary>
internal static class AssertionExtractor
{
    /// <summary>Descriptors kept per file.</summary>
    internal const int MaxDescriptors = 20_000;

    private const int MaxArgChars = 512;
    private const int MaxSourceChars = 2 * 1024 * 1024;
    private const int MaxDepth = 64;

    private static readonly HashSet<string> ScriptFrameworks = new(StringComparer.OrdinalIgnoreCase)
    {
        "playwright", "jest", "vitest", "mocha", "jasmine", "cypress",
    };

    private static readonly HashSet<string> PythonFrameworks = new(StringComparer.OrdinalIgnoreCase)
    {
        "pytest", "unittest",
    };

    /// <summary>Whether <paramref name="framework"/> is understood.</summary>
    /// <param name="framework">The framework name.</param>
    public static bool Supports(string framework) => ScriptFrameworks.Contains(framework) || PythonFrameworks.Contains(framework);

    /// <summary>Scans <paramref name="source"/>.</summary>
    /// <param name="framework">The framework of the file.</param>
    /// <param name="source">The test source.</param>
    public static AssertionExtraction Extract(string framework, string source)
    {
        ArgumentNullException.ThrowIfNull(framework);
        ArgumentNullException.ThrowIfNull(source);

        var text = source.Length > MaxSourceChars ? source[..MaxSourceChars] : source;
        var found = new List<string>();

        if (ScriptFrameworks.Contains(framework))
        {
            ScanScript(text, found);
        }
        else if (PythonFrameworks.Contains(framework))
        {
            ScanPython(text, found);
        }
        else
        {
            return new AssertionExtraction(false, []);
        }

        return new AssertionExtraction(true, found);
    }

    // ------------------------------------------------------------------ JavaScript / TypeScript

    private static void ScanScript(string s, List<string> found)
    {
        var i = 0;
        while (i < s.Length && found.Count < MaxDescriptors)
        {
            if (SkipScriptTrivia(s, ref i))
            {
                continue;
            }

            var c = s[i];
            if (c == '.')
            {
                var j = i + 1;
                var name = ReadIdentifier(s, ref j);
                if (name is "should" or "and" && TryArgs(s, ref j, out var arguments))
                {
                    found.Add($"{name}::{Normalize(arguments)}");
                    i = j;
                    continue;
                }

                i++;
                continue;
            }

            if (IsIdentifierStart(c) && (i == 0 || !IsIdentifierPart(s[i - 1])))
            {
                var j = i;
                var word = ReadIdentifier(s, ref j);
                if (word == "expect" && ParseExpect(s, ref j) is { } descriptor)
                {
                    found.Add(descriptor);
                }

                i = Math.Max(j, i + 1);
                continue;
            }

            i++;
        }
    }

    /// <summary>Parses <c>expect[.soft|.poll](subject)[.not|.resolves|.rejects]*.matcher(args)</c> after the word <c>expect</c>.</summary>
    private static string? ParseExpect(string s, ref int j)
    {
        var modifier = string.Empty;
        var k = SkipSpace(s, j);

        if (k < s.Length && s[k] == '.')
        {
            var m = k + 1;
            var word = ReadIdentifier(s, ref m);
            if (word is "soft" or "poll")
            {
                modifier = word;
                k = SkipSpace(s, m);
            }
        }

        if (k >= s.Length || s[k] != '(' || SkipBalanced(s, ref k) is null)
        {
            return null;
        }

        var negated = false;
        var chain = string.Empty;
        while (true)
        {
            k = SkipSpace(s, k);
            if (k >= s.Length || s[k] != '.')
            {
                j = k;

                return $"expect:{modifier}:{(negated ? "not" : string.Empty)}{chain}:?:";
            }

            var m = k + 1;
            var word = ReadIdentifier(s, ref m);
            if (word.Length == 0)
            {
                j = k;

                return null;
            }

            if (word == "not")
            {
                negated = true;
                k = m;
                continue;
            }

            if (word is "resolves" or "rejects")
            {
                chain += "|" + word;
                k = m;
                continue;
            }

            string arguments = string.Empty;
            var open = SkipSpace(s, m);
            if (open < s.Length && s[open] == '(')
            {
                var close = open;
                arguments = SkipBalanced(s, ref close) ?? string.Empty;
                m = close;
            }

            j = m;

            return $"expect:{modifier}:{(negated ? "not" : string.Empty)}{chain}:{word}:{Normalize(arguments)}";
        }
    }

    private static bool SkipScriptTrivia(string s, ref int i)
    {
        var c = s[i];
        if (c is '\'' or '"' or '`')
        {
            i = EndOfString(s, i);

            return true;
        }

        if (c == '/' && i + 1 < s.Length)
        {
            if (s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n')
                {
                    i++;
                }

                return true;
            }

            if (s[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? s.Length : end + 2;

                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ Python

    private static void ScanPython(string s, List<string> found)
    {
        var i = 0;
        var lineStart = true;
        while (i < s.Length && found.Count < MaxDescriptors)
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
                i = EndOfPythonString(s, i);
                lineStart = false;
                continue;
            }

            if (c == '\n')
            {
                lineStart = true;
                i++;
                continue;
            }

            if (c is ' ' or '\t' or '\r' or ';')
            {
                lineStart = lineStart || c == ';';
                i++;
                continue;
            }

            if (IsIdentifierStart(c) && (i == 0 || !IsIdentifierPart(s[i - 1])))
            {
                var j = i;
                var word = ReadIdentifier(s, ref j);
                if (word == "assert" && lineStart)
                {
                    var end = EndOfLogicalLine(s, j);
                    found.Add("assert::" + Normalize(StripPythonComment(s[j..end])));
                    i = end;
                    lineStart = false;
                    continue;
                }

                lineStart = false;
                i = Math.Max(j, i + 1);

                continue;
            }

            if (c == '.')
            {
                var j = i + 1;
                var name = ReadIdentifier(s, ref j);
                if ((name.StartsWith("assert", StringComparison.Ordinal) || name is "raises" or "warns" or "approx") && TryArgs(s, ref j, out var arguments))
                {
                    found.Add($"{name}::{Normalize(arguments)}");
                    i = j;
                    lineStart = false;
                    continue;
                }
            }

            lineStart = false;
            i++;
        }
    }

    private static int EndOfLogicalLine(string s, int from)
    {
        var depth = 0;
        var i = from;
        while (i < s.Length)
        {
            var c = s[i];
            if (c is '\'' or '"')
            {
                i = EndOfPythonString(s, i);
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

    private static string StripPythonComment(string expression)
    {
        var i = 0;
        while (i < expression.Length)
        {
            if (expression[i] is '\'' or '"')
            {
                i = EndOfPythonString(expression, i);
                continue;
            }

            if (expression[i] == '#')
            {
                return expression[..i];
            }

            i++;
        }

        return expression;
    }

    private static int EndOfPythonString(string s, int start)
    {
        var quote = s[start];
        if (start + 2 < s.Length && s[start + 1] == quote && s[start + 2] == quote)
        {
            var end = s.IndexOf(new string(quote, 3), start + 3, StringComparison.Ordinal);

            return end < 0 ? s.Length : end + 3;
        }

        return EndOfString(s, start);
    }

    // ------------------------------------------------------------------ shared scanning

    /// <summary>Index just past the string literal that starts at <paramref name="start"/> (single-line for ' and ", multi-line for backticks).</summary>
    private static int EndOfString(string s, int start)
    {
        var quote = s[start];
        var i = start + 1;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == quote)
            {
                return i + 1;
            }

            if (c == '\n' && quote != '`')
            {
                return i;
            }

            i++;
        }

        return s.Length;
    }

    private static bool TryArgs(string s, ref int j, out string arguments)
    {
        var k = SkipSpace(s, j);
        if (k < s.Length && s[k] == '(' && SkipBalanced(s, ref k) is { } inner)
        {
            arguments = inner;
            j = k;

            return true;
        }

        arguments = string.Empty;

        return false;
    }

    /// <summary>With <c>s[pos] == '('</c>, returns the text between the parentheses and moves <paramref name="pos"/> past the closing one; null when unbalanced or too deep.</summary>
    private static string? SkipBalanced(string s, ref int pos)
    {
        var start = pos + 1;
        var depth = 0;
        var i = pos;
        while (i < s.Length)
        {
            var c = s[i];
            if (c is '\'' or '"' or '`')
            {
                i = EndOfString(s, i);
                continue;
            }

            if (c == '/' && i + 1 < s.Length && s[i + 1] is '/' or '*')
            {
                if (s[i + 1] == '/')
                {
                    while (i < s.Length && s[i] != '\n')
                    {
                        i++;
                    }
                }
                else
                {
                    var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? s.Length : end + 2;
                }

                continue;
            }

            if (c is '(' or '[' or '{')
            {
                if (++depth > MaxDepth)
                {
                    return null;
                }
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    pos = i + 1;

                    return s[start..i];
                }
            }

            i++;
        }

        return null;
    }

    private static int SkipSpace(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i]))
        {
            i++;
        }

        return i;
    }

    private static string ReadIdentifier(string s, ref int i)
    {
        var start = i;
        while (i < s.Length && IsIdentifierPart(s[i]))
        {
            i++;
        }

        return s[start..i];
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c is '_' or '$';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    /// <summary>
    /// Collapses whitespace <em>outside</em> string literals so formatting never changes a descriptor while the text
    /// of an expected value always does; very long arguments are replaced by their digest.
    /// </summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                i++;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            if (c is '\'' or '"' or '`')
            {
                var end = EndOfString(text, i);
                builder.Append(text, i, end - i);
                i = end;
                continue;
            }

            builder.Append(c);
            i++;
        }

        var normalised = builder.ToString();

        return normalised.Length <= MaxArgChars
            ? normalised
            : "h:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)))[..32];
    }
}
