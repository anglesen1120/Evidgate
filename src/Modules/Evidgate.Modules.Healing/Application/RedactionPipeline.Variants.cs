using System.Text;

namespace Evidgate.Modules.Healing.Application;

internal sealed partial class RedactionPipeline
{
    /// <summary>
    /// <paramref name="text"/> without invisible format characters and with the fullwidth and compatibility letters the
    /// rules spell in ASCII mapped to them; null when nothing changes. The maps are written out so the result does not
    /// depend on the culture data of the host (the Worker runs with invariant globalization).
    /// </summary>
    private static string? Normalised(string text)
    {
        var first = -1;
        for (var i = 0; i < text.Length && first < 0; i++)
        {
            if (IsFormatCharacter(text[i]) || Fold(text[i]) != text[i])
            {
                first = i;
            }
        }

        if (first < 0)
        {
            return null;
        }

        var folded = new StringBuilder(text.Length).Append(text, 0, first);
        for (var i = first; i < text.Length; i++)
        {
            if (!IsFormatCharacter(text[i]))
            {
                folded.Append(Fold(text[i]));
            }
        }

        return folded.ToString();
    }

    private static bool IsFormatCharacter(char c) =>
        c is >= '\u200B' and <= '\u200F'
            or >= '\u202A' and <= '\u202E'
            or >= '\u2060' and <= '\u2064'
            or >= '\u2066' and <= '\u2069'
            or '\u00AD' or '\u180E' or '\uFEFF';

    private static char Fold(char c) => c switch
    {
        >= '\uFF01' and <= '\uFF5E' => (char)(c - 0xFEE0),
        '\u3000' => ' ',
        '\u212A' => 'K',
        '\u017F' => 's',
        _ => c,
    };
}
