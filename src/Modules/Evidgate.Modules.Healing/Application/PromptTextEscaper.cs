using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// Makes untrusted text inert inside a prompt block: markup characters become character references and characters
/// that can hide or forge a delimiter are removed (<see cref="InvisibleCharacters"/>; a filler that reads as a space
/// stays one space, so a keyword and its value keep their gap). Output is plain text that never contains a raw
/// <c>&lt;</c>, <c>&gt;</c> or <c>&amp;</c>.
/// </summary>
internal static class PromptTextEscaper
{
    /// <summary>
    /// Code points that read as, or normalise (NFKC) to, <c>&lt;</c>, <c>&gt;</c> or <c>&amp;</c>: full-width and small
    /// forms, "not less/greater than", and angle-bracket lookalikes. A fixed list, not derived from
    /// <c>Normalize</c>, because the Worker runs with invariant globalization where normalisation is a no-op.
    /// </summary>
    private static readonly FrozenSet<int> LookalikeMarkup = new int[]
    {
        0x2039, 0x203A, 0x226E, 0x226F, 0x2329, 0x232A, 0x276E, 0x276F, 0x27E8, 0x27E9, 0x3008, 0x3009,
        0xFE60, 0xFE64, 0xFE65, 0xFF06, 0xFF1C, 0xFF1E,
    }.ToFrozenSet();

    /// <summary>Appends <paramref name="text"/> to <paramref name="builder"/> in escaped, sanitised form.</summary>
    /// <param name="builder">The prompt body being built.</param>
    /// <param name="text">Untrusted text.</param>
    internal static void AppendEscaped(StringBuilder builder, string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '<': builder.Append("&lt;"); continue;
                case '>': builder.Append("&gt;"); continue;
                case '&': builder.Append("&amp;"); continue;
                case '\n' or '\t': builder.Append(c); continue;
                case '\r':
                    // CRLF and a bare CR are both one line break; a CR must not survive to act as one the block end misses.
                    if (i + 1 < text.Length && text[i + 1] == '\n') { i++; }
                    builder.Append('\n');
                    continue;
            }

            if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    var rune = char.ConvertToUtf32(c, text[i + 1]);
                    i++;
                    AppendRead(builder, rune, c, text[i]);
                }
                else
                {
                    builder.Append('\uFFFD');
                }

                continue;
            }

            if (LookalikeMarkup.Contains(c))
            {
                builder.Append("&#x").Append(((int)c).ToString("X", CultureInfo.InvariantCulture)).Append(';');
            }
            else
            {
                AppendRead(builder, c, c, low: null);
            }
        }
    }

    private static void AppendRead(StringBuilder builder, int scalar, char first, char? low)
    {
        switch (InvisibleCharacters.Classify(scalar))
        {
            case InvisibleKind.Space:
                builder.Append(' ');
                break;
            case InvisibleKind.None:
                builder.Append(first);
                if (low is { } second) { builder.Append(second); }
                break;
        }
    }
}
