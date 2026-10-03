using System.Security.Cryptography;
using System.Text;

namespace Evidgate.Modules.Catalog.Domain;

/// <summary>The assertion descriptors found in one test file.</summary>
/// <param name="Extracted">False when the framework is not one this extractor understands (the descriptor list is empty then).</param>
/// <param name="Descriptors">One short, normalised text per assertion, in source order.</param>
/// <param name="Reliable">
/// False when the scan could not follow the file to its end (an unterminated string or comment, an unbalanced call, the
/// descriptor cap): the list may be missing assertions, so the hash derived from it must not be trusted to notice a change.
/// </param>
internal sealed record AssertionExtraction(bool Extracted, IReadOnlyList<string> Descriptors, bool Reliable = true);

/// <summary>
/// Extracts assertion descriptors from test source (03.1.3-T1.2). A descriptor keeps what makes an assertion
/// <em>this</em> assertion — the matcher, a negation, and the expected-value arguments — and drops its subject, so
/// healing a locator (<c>expect(page.locator('#a')).toBeVisible()</c> → <c>'#b'</c>) leaves the hash alone while a
/// removed, weakened or re-targeted assertion changes it. Assertions <em>inside</em> a subject
/// (<c>expect(async () =&gt; { expect(a).toBe(1) }).toPass()</c>) are extracted too. Comments and string literals are
/// skipped; the scan is one linear pass with bounded nesting. The scan never guesses: whatever it cannot follow sets
/// <see cref="AssertionExtraction.Reliable"/> to false, and an unsupported framework is not extracted at all — in both
/// cases <see cref="RevisionHasher"/> falls back to a hash that changes with <em>any</em> edit (fail closed).
/// Frameworks: JavaScript/TypeScript (Playwright, Jest, Vitest, Mocha, Jasmine, Cypress, with Chai chains and
/// <c>assert.*</c>) and Python (pytest, unittest, pytest-playwright's <c>expect</c>).
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

    /// <summary>Chai-style words that carry no meaning of their own: <c>expect(a).to.be.equal(1)</c>.</summary>
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "to", "be", "been", "is", "that", "which", "and", "has", "have", "with", "at", "of", "same", "but", "does", "still", "also", "deep", "nested", "own", "ordered", "any", "all",
    };

    /// <summary>Whether <paramref name="framework"/> is understood.</summary>
    /// <param name="framework">The framework name.</param>
    public static bool Supports(string framework) => ScriptFrameworks.Contains(framework) || PythonFrameworks.Contains(framework);

    /// <summary>Scans <paramref name="source"/>.</summary>
    /// <param name="framework">The framework of the file.</param>
    /// <param name="source">The test source.</param>
    /// <param name="language">The language when known; <c>python</c> selects the Python scanner whatever the framework.</param>
    public static AssertionExtraction Extract(string framework, string source, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(framework);
        ArgumentNullException.ThrowIfNull(source);

        var python = string.Equals(language, "python", StringComparison.OrdinalIgnoreCase)
            || (PythonFrameworks.Contains(framework) && !string.Equals(language, "typescript", StringComparison.OrdinalIgnoreCase));
        if (!python && !ScriptFrameworks.Contains(framework))
        {
            return new AssertionExtraction(false, [], Reliable: false);
        }

        var state = new ScanState { Truncated = source.Length > MaxSourceChars };
        var text = state.Truncated ? source[..MaxSourceChars] : source;

        // Almost every non-test file, and every test without an assertion, ends here.
        if (text.AsSpan().IndexOf("expect", StringComparison.Ordinal) < 0
            && text.AsSpan().IndexOf("assert", StringComparison.Ordinal) < 0
            && text.AsSpan().IndexOf("should", StringComparison.Ordinal) < 0
            && text.AsSpan().IndexOf("raises", StringComparison.Ordinal) < 0)
        {
            return new AssertionExtraction(true, [], !state.Truncated);
        }

        if (python)
        {
            ScanPython(text, state);
        }
        else
        {
            ScanScript(text, state);
        }

        return new AssertionExtraction(true, state.Found, !state.Degraded && !state.Truncated && state.Found.Count < MaxDescriptors);
    }

    private sealed class ScanState
    {
        public List<string> Found { get; } = [];

        public bool Truncated { get; init; }

        public bool Degraded { get; set; }
    }

    // ------------------------------------------------------------------ JavaScript / TypeScript

    private static void ScanScript(string s, ScanState state)
    {
        var i = 0;
        while (i < s.Length && state.Found.Count < MaxDescriptors)
        {
            if (SkipScriptTrivia(s, ref i, state))
            {
                continue;
            }

            var c = s[i];
            if (c == '.')
            {
                var j = i + 1;
                if (Is(s, j, "should") || Is(s, j, "and"))
                {
                    var name = ReadIdentifier(s, ref j);
                    if (name is "should" or "and" && TryArgs(s, ref j, out var arguments, state))
                    {
                        state.Found.Add($"{name}::{Normalize(arguments)}");
                        i = j;
                        continue;
                    }
                }

                i++;
                continue;
            }

            if (IsIdentifierStart(c) && (i == 0 || !IsIdentifierPart(s[i - 1])))
            {
                var j = i;
                if (Is(s, i, "expect") && IsBoundary(s, i + 6))
                {
                    j = i + 6;
                    ParseExpect(s, ref j, state, depth: 0);
                }
                else if (Is(s, i, "assert") && IsBoundary(s, i + 6))
                {
                    j = i + 6;
                    ParseAssertCall(s, ref j, state);
                }
                else
                {
                    ReadIdentifierEnd(s, ref j);
                }

                i = Math.Max(j, i + 1);
                continue;
            }

            i++;
        }
    }

    /// <summary>Parses <c>assert(x)</c> and <c>assert.equal(a, b)</c> (node:assert, chai assert) after the word <c>assert</c>.</summary>
    private static void ParseAssertCall(string s, ref int j, ScanState state)
    {
        var k = j;
        var name = string.Empty;
        if (k < s.Length && s[k] == '.')
        {
            var m = k + 1;
            name = ReadIdentifier(s, ref m);
            k = m;
        }

        if (TryArgs(s, ref k, out var arguments, state))
        {
            state.Found.Add($"assert:{name}:{Normalize(arguments)}");
            j = k;
        }
    }

    /// <summary>Parses <c>expect[.soft|.poll](subject)[.not|.resolves|.rejects|chai words]*.matcher(args)</c> after the word <c>expect</c>.</summary>
    private static void ParseExpect(string s, ref int j, ScanState state, int depth)
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

        if (k >= s.Length || s[k] != '(')
        {
            return;
        }

        var subject = SkipBalanced(s, ref k, state);
        if (subject is null)
        {
            state.Degraded = true;

            return;
        }

        // Assertions nested in the subject (expect(async () => { expect(a).toBe(1) }).toPass()) count as assertions too.
        if (depth < 4 && subject.AsSpan().IndexOf("expect", StringComparison.Ordinal) >= 0)
        {
            var nested = new ScanState();
            ScanScript(subject, nested);
            foreach (var inner in nested.Found)
            {
                state.Found.Add("in:" + inner);
            }

            state.Degraded |= nested.Degraded;
        }

        var negated = false;
        var chain = string.Empty;
        while (true)
        {
            k = SkipSpace(s, k);
            if (k >= s.Length || s[k] != '.')
            {
                j = k;
                state.Found.Add($"expect:{modifier}:{(negated ? "not" : string.Empty)}{chain}:?:");

                return;
            }

            var m = k + 1;
            var word = ReadIdentifier(s, ref m);
            if (word.Length == 0)
            {
                j = k;
                state.Degraded = true;

                return;
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

            // Chai chains: only the words after the filler carry meaning (expect(a).to.be.deep.equal(1)).
            var after = SkipSpace(s, m);
            if (Filler.Contains(word) && after < s.Length && s[after] == '.')
            {
                k = m;
                continue;
            }

            var arguments = string.Empty;
            var open = SkipSpace(s, m);
            if (open < s.Length && s[open] == '(')
            {
                var close = open;
                var inner = SkipBalanced(s, ref close, state);
                if (inner is null)
                {
                    state.Degraded = true;
                    j = m;

                    return;
                }

                arguments = inner;
                m = close;
            }

            j = m;
            state.Found.Add($"expect:{modifier}:{(negated ? "not" : string.Empty)}{chain}:{word}:{Normalize(arguments)}");

            return;
        }
    }

    private static bool SkipScriptTrivia(string s, ref int i, ScanState state)
    {
        var c = s[i];
        if (c is '\'' or '"' or '`')
        {
            i = EndOfString(s, i, state);

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
                if (end < 0)
                {
                    state.Degraded = true;
                }

                i = end < 0 ? s.Length : end + 2;

                return true;
            }
        }

        return false;
    }

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

    // ------------------------------------------------------------------ shared scanning

    /// <summary>Index just past the string literal that starts at <paramref name="start"/> (single-line for ' and ", multi-line for backticks).</summary>
    private static int EndOfString(string s, int start, ScanState state)
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
                // A regex literal such as /["']/ or a stray quote: the scan cannot tell, so it says so.
                state.Degraded = true;

                return i;
            }

            i++;
        }

        state.Degraded = true;

        return s.Length;
    }

    private static bool TryArgs(string s, ref int j, out string arguments, ScanState state)
    {
        var k = SkipSpace(s, j);
        if (k < s.Length && s[k] == '(')
        {
            if (SkipBalanced(s, ref k, state) is { } inner)
            {
                arguments = inner;
                j = k;

                return true;
            }

            state.Degraded = true;
        }

        arguments = string.Empty;

        return false;
    }

    /// <summary>With <c>s[pos] == '('</c>, returns the text between the parentheses and moves <paramref name="pos"/> past the closing one; null when unbalanced or too deep.</summary>
    private static string? SkipBalanced(string s, ref int pos, ScanState state)
    {
        var start = pos + 1;
        var depth = 0;
        var i = pos;
        while (i < s.Length)
        {
            var c = s[i];
            if (c is '\'' or '"' or '`')
            {
                i = EndOfString(s, i, state);
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

    private static bool Is(string s, int at, string word) => string.CompareOrdinal(s, at, word, 0, word.Length) == 0;

    private static bool IsBoundary(string s, int at) => at >= s.Length || !IsIdentifierPart(s[at]);

    private static int SkipSpace(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i]))
        {
            i++;
        }

        return i;
    }

    private static void ReadIdentifierEnd(string s, ref int i)
    {
        while (i < s.Length && IsIdentifierPart(s[i]))
        {
            i++;
        }
    }

    private static string ReadIdentifier(string s, ref int i)
    {
        var start = i;
        ReadIdentifierEnd(s, ref i);

        return s[start..i];
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c is '_' or '$';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    /// <summary>
    /// Collapses whitespace <em>outside</em> string literals so formatting never changes a descriptor while the text
    /// of an expected value always does; a line comment keeps its line break so it cannot swallow what follows it;
    /// very long arguments are replaced by their digest.
    /// </summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        var scratch = new ScanState();
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
                var end = EndOfString(text, i, scratch);
                builder.Append(text, i, end - i);
                i = end;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                // Keep the comment and mark its end, so `a, // c\n b` and `a, // c b` stay different.
                var end = text.IndexOf('\n', i);
                end = end < 0 ? text.Length : end;
                builder.Append(text, i, end - i).Append('\n');
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
