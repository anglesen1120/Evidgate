namespace Evidgate.Modules.Catalog.Domain;

internal static partial class AssertionExtractor
{
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
}
