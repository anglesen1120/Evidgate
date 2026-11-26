using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace Evidgate.Modules.Healing.Application;

/// <summary>
/// Makes untrusted text inert inside a prompt block: markup characters become character references and characters
/// that can hide or forge a delimiter are removed. Output is plain text that never contains a raw <c>&lt;</c>,
/// <c>&gt;</c> or <c>&amp;</c>.
/// </summary>
internal static class PromptTextEscaper
{
    /// <summary>
    /// BMP code points whose NFKC form contains <c>&lt;</c>, <c>&gt;</c> or <c>&amp;</c> (full-width and small forms,
    /// "not less than" and the like), found once from the platform's own tables instead of a hand-kept list.
    /// </summary>
    private static readonly FrozenSet<int> LookalikeMarkup = FindLookalikes();

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
                    if (!IsInvisible(rune)) { builder.Append(c).Append(text[i]); }
                }
                else
                {
                    builder.Append('\uFFFD');
                }

                continue;
            }

            if (c < 0x80)
            {
                if (!char.IsControl(c)) { builder.Append(c); }
                continue;
            }

            if (LookalikeMarkup.Contains(c))
            {
                builder.Append("&#x").Append(((int)c).ToString("X", CultureInfo.InvariantCulture)).Append(';');
            }
            else if (!IsInvisible(c))
            {
                builder.Append(c);
            }
        }
    }

    /// <summary>Control, format (zero-width, bidi), line/paragraph separator and unassigned code points: nothing a reader needs.</summary>
    private static bool IsInvisible(int codePoint) =>
        CharUnicodeInfo.GetUnicodeCategory(codePoint) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned;

    private static FrozenSet<int> FindLookalikes()
    {
        var found = new List<int>();
        for (var cp = 0x80; cp <= 0xFFFF; cp++)
        {
            if (cp is >= 0xD800 and <= 0xDFFF) { continue; }
            var form = ((char)cp).ToString().Normalize(NormalizationForm.FormKC);
            if (form.AsSpan().IndexOfAny('<', '>', '&') >= 0) { found.Add(cp); }
        }

        return found.ToFrozenSet();
    }
}
