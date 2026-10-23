using System.Linq;

namespace Evidgate.Modules.Policy.Domain;

/// <summary>
/// Shape rules of a quarantine entry (07.3.1 §4, FR-07-9). The API checks them before the write; the database
/// repeats the state machine in <c>evidgate.quarantine_entry_guard()</c>, so a bug here cannot launder a critical
/// case into a pass.
/// </summary>
internal static class QuarantineRules
{
    /// <summary>The <c>quarantine_entry_reason_check</c> bound (07.3.1 §4).</summary>
    public const int ReasonMaxLength = 2000;

    /// <summary>
    /// The reason's length in Unicode code points — the unit <c>char_length</c> counts — not UTF-16 units, so a
    /// 2000-emoji reason is 2000 characters and accepted (07.3.1 §4). A lone surrogate enumerates as U+FFFD and
    /// counts as one; <see cref="HasUnstorableText"/> refuses it before this is reached.
    /// </summary>
    public static int CharacterCount(string reason) => reason.EnumerateRunes().Count();

    /// <summary>
    /// True when the text holds something PostgreSQL cannot carry: U+0000 or a UTF-16 surrogate without its partner
    /// (the 05.1.1/05.1.2 <c>JsonBody</c> rule, <c>PolicyRevisionRules.Unstorable</c>). A lone surrogate would be
    /// replaced by U+FFFD on the wire, so it is refused as <c>invalid_value</c> rather than silently altered.
    /// </summary>
    public static bool HasUnstorableText(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\0')
            {
                return true;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }
}
