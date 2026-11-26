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
            if (first < 0 && (invisible || (width == 1 && Fold(text[i]) != text[i])))
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
                folded.Append(width == 1 ? Fold(text[i]) : text[i]);
                if (width == 2)
                {
                    folded.Append(text[i + 1]);
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

    private static char Fold(char c) => c switch
    {
        >= '\uFF01' and <= '\uFF5E' => (char)(c - 0xFEE0),
        '\u3000' => ' ',
        '\u212A' => 'K',
        '\u017F' => 's',
        _ => c,
    };
}
