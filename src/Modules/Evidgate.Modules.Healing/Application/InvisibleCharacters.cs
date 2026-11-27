using System.Globalization;

namespace Evidgate.Modules.Healing.Application;

/// <summary>How a code point that draws nothing reads to a person: dropped, or as the gap between two words.</summary>
internal enum InvisibleKind
{
    /// <summary>A visible code point (or tab, line feed, carriage return).</summary>
    None,

    /// <summary>Joins its neighbours: zero-width and format characters, selectors, controls.</summary>
    Remove,

    /// <summary>Reads as a space: the Hangul and halfwidth fillers U+115F, U+1160, U+3164, U+FFA0 and the line and paragraph separators.</summary>
    Space,
}

/// <summary>
/// The one definition of an invisible code point, used by <see cref="PromptTextEscaper"/> (which drops them from a
/// prompt) and by <see cref="RedactionPipeline"/> (which reads text without them). The set is category Cc (except tab,
/// line feed, carriage return), Cf, Zl, Zp and Cn, plus the default-ignorable code points outside those categories.
/// The category data is compiled into the runtime, so it holds under invariant globalization too.
/// </summary>
internal static class InvisibleCharacters
{
    /// <summary>Classifies <paramref name="scalar"/> (a code point, or a lone surrogate, which is visible).</summary>
    internal static InvisibleKind Classify(int scalar)
    {
        if (scalar < 0x80)
        {
            return scalar is '\t' or '\n' or '\r' || (scalar >= 0x20 && scalar != 0x7F)
                ? InvisibleKind.None
                : InvisibleKind.Remove;
        }

        if (scalar is 0x115F or 0x1160 or 0x3164 or 0xFFA0)
        {
            return InvisibleKind.Space;
        }

        return CharUnicodeInfo.GetUnicodeCategory(scalar) switch
        {
            UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator => InvisibleKind.Space,
            UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned => InvisibleKind.Remove,
            _ => IsDefaultIgnorable(scalar) ? InvisibleKind.Remove : InvisibleKind.None,
        };
    }

    // The default-ignorable code points outside the categories above: combining grapheme joiner, Arabic letter mark,
    // Khmer vowel inherents, Mongolian and variation selectors, the reserved invisible-operator slot U+2065 and
    // U+FFF0-U+FFF8, and the whole tag and variation selectors supplement block, assigned or not.
    private static bool IsDefaultIgnorable(int scalar) =>
        scalar is 0x034F or 0x061C or 0x17B4 or 0x17B5 or 0x2065
            or >= 0x180B and <= 0x180F
            or >= 0xFE00 and <= 0xFE0F
            or >= 0xFFF0 and <= 0xFFF8
            or >= 0xE0000 and <= 0xE0FFF;
}
