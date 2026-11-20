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

    /// <summary>Free-text keys that never identify a variant: the normaliser leaves them out of the hash and does not check their values.</summary>
    private static readonly HashSet<string> NonIdentifying = new(StringComparer.OrdinalIgnoreCase)
    {
        "description", "label", "labels", "note", "notes", "comment", "comments", "title", "tags", "display_name", "displayname",
    };

    /// <summary>True for a free-text key (or a <c>_</c>-prefixed one) that is not part of a variant's identity.</summary>
    /// <param name="key">The parameter name.</param>
    public static bool IsNonIdentifying(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return key.StartsWith('_') || NonIdentifying.Contains(key);
    }

    /// <summary>
    /// True when a value of a free-text key is not clean text and, with its control and format characters removed, has a
    /// secret's shape: legitimate ZWJ/ZWNJ text stays accepted, a token hidden behind invisible characters does not.
    /// </summary>
    /// <param name="key">The parameter name.</param>
    /// <param name="text">The value as sent.</param>
    public static bool HiddenSecret(string key, string text) =>
        IsNonIdentifying(key) && !UnicodeCleanliness.IsClean(text) && ValueLooksSecret(key, UnicodeCleanliness.StripInvisible(text));

    /// <summary>The form a string value is stored in under <paramref name="key"/>.</summary>
    /// <param name="key">The parameter name.</param>
    /// <param name="text">The value as sent.</param>
    public static string StoredForm(string key, string text)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(text);

        return EnumLike.Contains(key) ? text.Trim().ToLowerInvariant() : text;
    }

    /// <summary>True when the value as sent, or as stored, has a secret's shape.</summary>
    /// <param name="key">The parameter name.</param>
    /// <param name="text">The value as sent.</param>
    public static bool ValueLooksSecret(string key, string text) =>
        SecretShape.ValueLooksSecret(text) || SecretShape.ValueLooksSecret(StoredForm(key, text));
}
