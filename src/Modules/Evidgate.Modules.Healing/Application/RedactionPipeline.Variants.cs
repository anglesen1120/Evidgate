using System.Globalization;
using System.Text;

namespace Evidgate.Modules.Healing.Application;

internal sealed partial class RedactionPipeline
{
    /// <summary>
    /// <paramref name="text"/> without invisible characters (every format character and the other default-ignorable
    /// code points) and with the fullwidth and compatibility letters the rules spell in ASCII mapped to them; null when
    /// nothing changes. The maps are written out so the result does not depend on the culture data of the host (the
    /// Worker runs with invariant globalization).
    /// </summary>
    private static string? Normalised(string text)
    {
        var first = -1;
        for (var i = 0; i < text.Length && first < 0;)
        {
            var width = ScalarWidth(text, i, out var scalar);
            if (IsInvisible(scalar) || (width == 1 && Fold(text[i]) != text[i]))
            {
                first = i;
            }

            i += width;
        }

        if (first < 0)
        {
            return null;
        }

        var folded = new StringBuilder(text.Length).Append(text, 0, first);
        for (var i = first; i < text.Length;)
        {
            var width = ScalarWidth(text, i, out var scalar);
            if (!IsInvisible(scalar))
            {
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
    // fillers, Mongolian and variation selectors, tag and supplementary variation selectors).
    private static bool IsInvisible(int scalar) =>
        CharUnicodeInfo.GetUnicodeCategory(scalar) == UnicodeCategory.Format
        || scalar is 0x034F or 0x061C or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x3164 or 0xFFA0 or 0xE0001
            or >= 0x180B and <= 0x180D
            or >= 0xFE00 and <= 0xFE0F
            or >= 0xE0020 and <= 0xE007F
            or >= 0xE0100 and <= 0xE01EF;

    private static char Fold(char c) => c switch
    {
        >= '\uFF01' and <= '\uFF5E' => (char)(c - 0xFEE0),
        '\u3000' => ' ',
        '\u212A' => 'K',
        '\u017F' => 's',
        _ => c,
    };
}
