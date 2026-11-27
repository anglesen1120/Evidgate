using System.Text;

namespace Evidgate.Modules.Healing.Application;

internal sealed partial class RedactionPipeline
{
    /// <summary>How <see cref="Normalised"/> reads the invisible characters of a text.</summary>
    private enum InvisibleReading
    {
        /// <summary>Every invisible character is dropped (<c>pass&lt;ZWSP&gt;word</c>).</summary>
        Strip,

        /// <summary>Each run of them is one space, because they can be the only separator (<c>Bearer&lt;U+3164&gt;token</c>).</summary>
        SpaceRuns,

        /// <summary>Fillers and separators are spaces and every other invisible character is dropped, for a run that mixes both.</summary>
        Mixed,
    }

    /// <summary>
    /// <paramref name="text"/> read without its invisible characters, in one of the three <see cref="InvisibleReading"/>s,
    /// with the fullwidth and compatibility letters the rules spell in ASCII mapped to them. Null when the reading
    /// changes nothing: the spaced form needs an invisible character, the mixed form needs both kinds. The maps are
    /// written out so the result does not depend on the culture data of the host (the Worker runs with invariant
    /// globalization).
    /// </summary>
    private static string? Normalised(string text, InvisibleReading reading)
    {
        var first = -1;
        var removes = false;
        var spaces = false;
        for (var i = 0; i < text.Length;)
        {
            var width = ScalarWidth(text, i, out var scalar);
            var kind = InvisibleCharacters.Classify(scalar);
            if (first < 0 && (kind != InvisibleKind.None || Fold(scalar) != scalar))
            {
                first = i;
            }

            removes |= kind == InvisibleKind.Remove;
            spaces |= kind == InvisibleKind.Space;
            i += width;
            if (ReadingDecided(reading, first >= 0, removes, spaces))
            {
                break;
            }
        }

        if (first < 0 || (reading == InvisibleReading.SpaceRuns && !removes && !spaces)
            || (reading == InvisibleReading.Mixed && !(removes && spaces)))
        {
            return null;
        }

        var folded = new StringBuilder(text.Length).Append(text, 0, first);
        var inRun = false;
        for (var i = first; i < text.Length;)
        {
            var width = ScalarWidth(text, i, out var scalar);
            var kind = InvisibleCharacters.Classify(scalar);
            if (kind != InvisibleKind.None)
            {
                if (reading == InvisibleReading.SpaceRuns ? !inRun : reading == InvisibleReading.Mixed && kind == InvisibleKind.Space)
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

    private static bool ReadingDecided(InvisibleReading reading, bool changes, bool removes, bool spaces) => reading switch
    {
        InvisibleReading.Strip => changes,
        InvisibleReading.SpaceRuns => removes || spaces,
        _ => removes && spaces,
    };

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
