namespace Evidgate.Modules.Authoring.Domain;

/// <summary>
/// The text rules of the project AI settings: the composed Unicode form and the code-point limits every
/// settings field is validated and hashed under. Normalisation and counting themselves are
/// <see cref="AuthoringText"/>'s, so one implementation serves both authoring surfaces.
/// </summary>
internal static class AiSettingsText
{
    /// <summary>The writing-style cap, in code points.</summary>
    internal const int MaxWritingStyleLength = 250;

    /// <summary>The coverage-strategy cap, in code points.</summary>
    internal const int MaxCoverageStrategyLength = 1500;

    /// <summary>The exclusion-rules cap, in code points.</summary>
    internal const int MaxExclusionRulesLength = 1500;

    /// <summary>The step-detail cap, in code points.</summary>
    internal const int MaxStepDetailLength = 200;

    /// <summary>The description cap, in code points.</summary>
    internal const int MaxDescriptionLength = 1000;

    /// <summary>The environment-URL cap (server default, mirrors the column CHECK).</summary>
    internal const int MaxEnvironmentUrlLength = 2048;

    /// <summary>The cap of one priority criterion (server default, mirrors the column CHECK).</summary>
    internal const int MaxPriorityCriterionLength = 250;

    /// <summary>The reason cap of a save (mirrors the column CHECK).</summary>
    internal const int MaxReasonLength = 500;

    /// <summary>The two locales a settings document may ask a generation to write in.</summary>
    internal static readonly string[] SupportedLanguages = ["vi", "en"];

    /// <summary>
    /// The stored form of one text field: NFC-normalised, then trimmed at both ends. Interior whitespace is
    /// preserved, and a value that trims to empty becomes the empty string, never null.
    /// </summary>
    /// <param name="value">The caller's text, or null for an absent member.</param>
    /// <returns>The text every count and hash uses.</returns>
    internal static string Prepare(string? value) => AuthoringText.Normalize(value)?.Trim() ?? string.Empty;

    /// <summary>The length of <paramref name="value"/> in code points — the unit PostgreSQL <c>char_length</c> counts.</summary>
    /// <param name="value">The prepared text.</param>
    /// <returns>The number of code points.</returns>
    internal static int CountCodePoints(string? value) => AuthoringText.CodePointLength(value);

    /// <summary>
    /// The languages of a document in canonical form: ordinal-ascending, no duplicates. The caller has
    /// already refused a duplicate or an unsupported value, so this only fixes the order the hash depends on.
    /// </summary>
    /// <param name="languages">The requested languages.</param>
    /// <returns>The stored array.</returns>
    internal static string[] CanonicalLanguages(IEnumerable<string> languages)
    {
        ArgumentNullException.ThrowIfNull(languages);

        return languages.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>True when <paramref name="value"/> carries text PostgreSQL cannot store (U+0000).</summary>
    /// <param name="value">The text.</param>
    internal static bool ContainsNul(string? value) => AuthoringText.ContainsNul(value);

    /// <summary>True when <paramref name="value"/> is a supported locale code.</summary>
    /// <param name="value">The candidate.</param>
    internal static bool IsSupportedLanguage(string value) =>
        SupportedLanguages.Contains(value, StringComparer.Ordinal);
}
