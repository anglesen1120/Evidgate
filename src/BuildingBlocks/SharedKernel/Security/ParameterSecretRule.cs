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

    /// <summary>True when the value as sent, or as stored, has a secret's shape.</summary>
    /// <param name="key">The parameter name.</param>
    /// <param name="text">The value as sent.</param>
    public static bool ValueLooksSecret(string key, string text) =>
        SecretShape.ValueLooksSecret(text) || SecretShape.ValueLooksSecret(StoredForm(key, text));
}
