using System.Globalization;
using System.Text;

namespace Evidgate.Modules.Healing.Application;

internal sealed partial class RedactionPipeline
{
    /// <summary>
    /// The forms of <paramref name="text"/> read without invisible characters (every format character and the other
    /// default-ignorable code points) and with the fullwidth and compatibility letters the rules spell in ASCII mapped
    /// to them: once with the invisible characters removed (<c>pass&lt;ZWSP&gt;word</c>), and, when there are any, once
    /// with each run of them read as one space, because an invisible character can be the only separator
    /// (<c>Bearer&lt;U+3164&gt;token</c>). Nothing is returned when nothing changes.
    /// </summary>
    private static IEnumerable<string> NormalisedVariants(string text)
    {
        if (Normalised(text, invisibleAsSpace: false) is { } stripped)
        {
            yield return stripped;
        }

        if (Normalised(text, invisibleAsSpace: true) is { } spaced)
        {
            yield return spaced;
        }
    }

    /// <summary>
    /// <paramref name="text"/> with the invisible characters removed, or each run of them replaced by one space, and
    /// the compatibility letters mapped to ASCII; null when nothing changes (the spaced form changes only a text that
    /// holds an invisible character). The maps are written out so the result does not depend on the culture data of
    /// the host (the Worker runs with invariant globalization).
    /// </summary>
    private static string? Normalised(string text, bool invisibleAsSpace)
    {
        var first = -1;
        var hasInvisible = false;
        for (var i = 0; i < text.Length && (first < 0 || (invisibleAsSpace && !hasInvisible));)
        {
            var width = ScalarWidth(text, i, out var scalar);
            var invisible = IsInvisible(scalar);
            if (first < 0 && (invisible || Fold(scalar) != scalar))
            {
                first = i;
            }

            hasInvisible |= invisible;
            i += width;
        }

        // The spaced form differs from the stripped one only where an invisible character is read as a space.
        if (first < 0 || (invisibleAsSpace && !hasInvisible))
        {
            return null;
        }

        var folded = new StringBuilder(text.Length).Append(text, 0, first);
        var inRun = false;
        for (var i = first; i < text.Length;)
        {
            var width = ScalarWidth(text, i, out var scalar);
            if (IsInvisible(scalar))
            {
                if (invisibleAsSpace && !inRun)
                {
                    folded.Append(' ');
                }

                inRun = true;
            }
            else
            {
                inRun = false;

                // A lone surrogate is kept as it is: it is not a scalar value, so no rule can read it as invisible.
                var mapped = Fold(scalar);
                if (mapped != scalar)
                {
                    folded.Append((char)mapped);
                }
                else
                {
                    folded.Append(text, i, width);
                }
            }

            i += width;
        }

        return folded.ToString();
    }

    /// <summary>The UTF-16 width (1 or 2) of the code point at <paramref name="index"/>; a lone surrogate counts as 1.</summary>
    private static int ScalarWidth(string text, int index, out int scalar)
    {
        if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            scalar = char.ConvertToUtf32(text[index], text[index + 1]);
            return 2;
        }

        scalar = text[index];
        return 1;
    }

    // The Unicode category data is compiled into the runtime, so this holds under invariant globalization too. The
    // explicit list is the default-ignorable code points outside category Cf (combining joiner, Hangul and Khmer
    // fillers, Mongolian and variation selectors, the reserved invisible-operator slot U+2065 and U+FFF0-U+FFF8, and
    // the whole tag and variation selectors supplement block U+E0000-U+E0FFF, assigned or not).
    private static bool IsInvisible(int scalar) =>
        CharUnicodeInfo.GetUnicodeCategory(scalar) == UnicodeCategory.Format
        || scalar is 0x034F or 0x061C or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x2065 or 0x3164 or 0xFFA0
            or >= 0x180B and <= 0x180F
            or >= 0xFE00 and <= 0xFE0F
            or >= 0xFFF0 and <= 0xFFF8
            or >= 0xE0000 and <= 0xE0FFF;

    // Compatibility spellings of the ASCII the rules are written in: fullwidth forms and U+3000, small-form
    // punctuation, circled letters and digits, the mathematical alphanumeric block, the letter-like capitals, Kelvin
    // and long s. Written out, not NFKC, so the result does not depend on the host's globalization data.
    private static int Fold(int scalar) => scalar switch
    {
        >= 0xFF01 and <= 0xFF5E => scalar - 0xFEE0,
        0x3000 => ' ',
        0x212A => 'K',
        0x017F => 's',
        >= 0x2460 and <= 0x2468 => '1' + (scalar - 0x2460),
        >= 0x24B6 and <= 0x24CF => 'A' + (scalar - 0x24B6),
        >= 0x24D0 and <= 0x24E9 => 'a' + (scalar - 0x24D0),
        0x24EA => '0',
        >= 0x2100 and <= 0x214F => LetterLike(scalar),
        >= 0xFE50 and <= 0xFE6B => SmallForm(scalar),
        >= 0x1D400 and <= 0x1D6A3 => MathematicalLetter(scalar),
        >= 0x1D7CE and <= 0x1D7FF => '0' + ((scalar - 0x1D7CE) % 10),
        _ => scalar,
    };

    // The mathematical block is thirteen runs of 26 capitals then 26 small letters; the script, fraktur and
    // double-struck holes live in the letter-like block, so LetterLike covers them.
    private static int MathematicalLetter(int scalar)
    {
        var index = (scalar - 0x1D400) % 52;
        return index < 26 ? 'A' + index : 'a' + (index - 26);
    }

    private static int LetterLike(int scalar) => scalar switch
    {
        0x2102 or 0x212D => 'C',
        0x210A => 'g',
        0x210B or 0x210C or 0x210D => 'H',
        0x210E => 'h',
        0x2110 or 0x2111 => 'I',
        0x2112 => 'L',
        0x2113 => 'l',
        0x2115 => 'N',
        0x2119 => 'P',
        0x211A => 'Q',
        0x211B or 0x211C or 0x211D => 'R',
        0x2124 or 0x2128 => 'Z',
        0x212C => 'B',
        0x212F => 'e',
        0x2130 => 'E',
        0x2131 => 'F',
        0x2133 => 'M',
        0x2134 => 'o',
        _ => scalar,
    };

    private static int SmallForm(int scalar) => scalar switch
    {
        0xFE50 => ',',
        0xFE52 => '.',
        0xFE54 => ';',
        0xFE55 => ':',
        0xFE56 => '?',
        0xFE57 => '!',
        0xFE59 => '(',
        0xFE5A => ')',
        0xFE5B => '{',
        0xFE5C => '}',
        0xFE5F => '#',
        0xFE60 => '&',
        0xFE61 => '*',
        0xFE62 => '+',
        0xFE63 => '-',
        0xFE64 => '<',
        0xFE65 => '>',
        0xFE66 => '=',
        0xFE68 => '\\',
        0xFE69 => '$',
        0xFE6A => '%',
        0xFE6B => '@',
        _ => scalar,
    };
}
