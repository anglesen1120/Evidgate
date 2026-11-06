using System.Globalization;
using System.Text;

namespace Evidgate.Modules.Usage.Domain;

/// <summary>
/// The shapes PostgreSQL <c>text</c> can hold, and the shape a value a human reads must have: no U+0000 (the database
/// cannot store it in a text value at all), no UTF-16 surrogate that is not the first half of a real pair (it has no
/// UTF-8 encoding to write), and, for a value an operator reads back, no character that changes the line it is on. A
/// value outside these shapes would be a server error or a misleading row, so every text a module writes is checked
/// here first.
/// </summary>
internal static class PostgresText
{
    /// <summary>Whether <paramref name="value"/> is text PostgreSQL can store.</summary>
    /// <param name="value">The candidate text; never null at the call sites, which have already checked for null.</param>
    internal static bool IsStorable(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];

            if (character == '\0' || char.IsLowSurrogate(character))
            {
                return false;
            }

            if (!char.IsHighSurrogate(character))
            {
                continue;
            }

            // A high surrogate is storable only as the first half of a pair.
            if (index + 1 == value.Length || !char.IsLowSurrogate(value[index + 1]))
            {
                return false;
            }

            index++;
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="value"/> reads as one line of plain text: no control character, no Unicode format
    /// character (a bidi override or a zero-width mark) and no line or paragraph separator (U+2028 and U+2029 are
    /// neither of those categories, yet a reader that honours them starts a new line), any of which would let two
    /// different values read as the same row on screen or in an exported page.
    /// </summary>
    /// <param name="value">The candidate text; the caller has already checked it is <see cref="IsStorable"/>.</param>
    internal static bool IsPlain(string value)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) is
                UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator)
            {
                return false;
            }
        }

        return true;
    }
}
