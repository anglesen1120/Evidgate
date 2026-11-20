namespace Evidgate.BuildingBlocks.SharedKernel.Security;

/// <summary>
/// How a variant parameter value is judged for secrets, shared by the catalog's variant normaliser and the
/// orchestrator's manifest check so the two can never disagree: the text as sent and the text as it would be
/// stored (enum-like keys are trimmed and lower-cased) are both scanned.
/// </summary>
public static class ParameterSecretRule
{
    /// <summary>Keys whose string values are case-insensitive enum-like names: lower-cased and trimmed before hashing.</summary>
    private static readonly HashSet<string> EnumLike = new(StringComparer.OrdinalIgnoreCase)
    {
        "browser", "engine", "platform", "os", "mode", "env", "environment", "channel", "device", "locale", "viewport",
    };

    /// <summary>The form a string value is stored in under <paramref name="key"/>.</summary>
    /// <param name="key">The parameter name.</param>
    /// <param name="text">The value as sent.</param>
    public static string StoredForm(string key, string text)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(text);

        return EnumLike.Contains(key) ? text.Trim().ToLowerInvariant() : text;
    }

    /// <summary>
    /// True when the value as sent, as stored, or as stored with its invisible characters (control and format, such as a
    /// zero-width space or a byte-order mark) removed has a secret's shape.
    /// </summary>
    /// <param name="key">The parameter name.</param>
    /// <param name="text">The value as sent.</param>
    public static bool ValueLooksSecret(string key, string text)
    {
        var stored = StoredForm(key, text);
        if (SecretShape.ValueLooksSecret(text) || (!string.Equals(stored, text, StringComparison.Ordinal) && SecretShape.ValueLooksSecret(stored)))
        {
            return true;
        }

        return HasInvisible(stored) && SecretShape.ValueLooksSecret(string.Concat(stored.Where(c => !IsInvisible(c))));
    }

    private static bool HasInvisible(string text)
    {
        foreach (var c in text)
        {
            if (IsInvisible(c))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInvisible(char c) =>
        char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Control;
}
