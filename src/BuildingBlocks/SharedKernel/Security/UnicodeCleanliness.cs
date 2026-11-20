using System.Globalization;
using System.Text;

namespace Evidgate.BuildingBlocks.SharedKernel.Security;

/// <summary>
/// The one text-hygiene rule for customer-supplied names and values that are stored or scanned for secrets: well-formed
/// Unicode, no control characters (PostgreSQL <c>text</c> refuses NUL) and no format characters (bidi overrides,
/// zero-width characters, soft hyphen, byte-order mark), which would hide a token from a scan or make two different
/// texts look identical. Shared so the catalog's variant normaliser and the orchestrator's manifest check cannot disagree.
/// </summary>
public static class UnicodeCleanliness
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

    /// <summary><paramref name="text"/> without its control and format characters and without unpaired surrogates.</summary>
    /// <param name="text">The text.</param>
    public static string StripInvisible(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length;)
        {
            if (!Rune.TryGetRuneAt(text, i, out var rune))
            {
                i++;
                continue;
            }

            if (!Rune.IsControl(rune) && Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format)
            {
                builder.Append(rune.ToString());
            }

            i += rune.Utf16SequenceLength;
        }

        return builder.ToString();
    }
}
