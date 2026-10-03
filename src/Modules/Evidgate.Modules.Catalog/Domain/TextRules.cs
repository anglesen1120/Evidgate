using System.Globalization;
using System.Text;

namespace Evidgate.Modules.Catalog.Domain;

/// <summary>
/// Text hygiene for everything a customer repository can put into a catalog row (paths, names, labels, framework):
/// well-formed Unicode only, no control characters (PostgreSQL <c>text</c> refuses NUL) and no format characters
/// (bidi overrides, zero-width and byte-order marks), which would let two different paths look identical on screen.
/// Truncation never splits a surrogate pair. A value that fails is refused, never repaired silently.
/// </summary>
internal static class TextRules
{
    /// <summary>True when <paramref name="text"/> is non-null, well-formed and free of control and format characters.</summary>
    /// <param name="text">The candidate.</param>
    public static bool IsClean(string? text)
    {
        if (text is null)
        {
            return false;
        }

        for (var i = 0; i < text.Length;)
        {
            if (!Rune.TryGetRuneAt(text, i, out var rune))
            {
                return false;
            }

            if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
            {
                return false;
            }

            i += rune.Utf16SequenceLength;
        }

        return true;
    }

    /// <summary>The longest prefix of <paramref name="text"/> with at most <paramref name="maxChars"/> UTF-16 units that ends on a rune boundary.</summary>
    /// <param name="text">The text.</param>
    /// <param name="maxChars">The limit.</param>
    public static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var end = maxChars;
        if (end > 0 && char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        return text[..end];
    }

    /// <summary>A short, single-line, control-free rendering of untrusted text for a message.</summary>
    /// <param name="text">The text.</param>
    /// <param name="maxChars">The limit.</param>
    public static string Brief(string text, int maxChars = 120)
    {
        var builder = new StringBuilder(Math.Min(text.Length, maxChars));
        foreach (var rune in Truncate(text, maxChars).EnumerateRunes())
        {
            builder.Append(Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format ? '?' : rune.ToString());
        }

        return builder.Length < text.Length ? builder + "…" : builder.ToString();
    }
}
