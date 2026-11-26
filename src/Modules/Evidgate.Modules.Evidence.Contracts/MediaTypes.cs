using System.Globalization;
using System.Text;

namespace Evidgate.Modules.Evidence.Contracts;

/// <summary>
/// The one comparison of a declared media type with a list of accepted types. The upload mint, the completion check,
/// the intake reader, the intake planner and the extraction job all ask the same question, and a second copy that
/// trimmed, lowercased or compared the parameters would accept in one place what another refuses.
/// </summary>
/// <remarks>
/// The rule is exactly the upload rule: a control or format character anywhere refuses the type; the bare type is the
/// text before the first <c>;</c>, never trimmed, so the string that is accepted is the string that is persisted;
/// the bare type is compared with the accepted types ignoring case.
/// </remarks>
public static class MediaTypes
{
    /// <summary>True when <paramref name="value"/> is not empty and carries no control or format character.</summary>
    /// <param name="value">The text to check.</param>
    public static bool HasNoControlCharacter(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The media type without its parameters, verbatim, or null when it is absent or has a control character.</summary>
    /// <param name="mediaType">The declared media type.</param>
    public static string? Bare(string? mediaType) =>
        HasNoControlCharacter(mediaType) ? mediaType!.Split(';', 2)[0] : null;

    /// <summary>The accepted type that <paramref name="mediaType"/> names, or null when it names none.</summary>
    /// <param name="allowed">The accepted types.</param>
    /// <param name="mediaType">The declared media type.</param>
    /// <returns>The accepted type in its own spelling, so a caller stores the canonical form, not the declared one.</returns>
    public static string? Match(IEnumerable<string> allowed, string? mediaType)
    {
        ArgumentNullException.ThrowIfNull(allowed);

        return Bare(mediaType) is { } bare
            ? allowed.FirstOrDefault(candidate => string.Equals(candidate, bare, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    /// <summary>True when <paramref name="mediaType"/> names one of the accepted types.</summary>
    /// <param name="allowed">The accepted types.</param>
    /// <param name="mediaType">The declared media type.</param>
    public static bool IsAllowed(IEnumerable<string> allowed, string? mediaType) => Match(allowed, mediaType) is not null;

    /// <summary>True when both names carry the same bare type, ignoring case and parameters.</summary>
    /// <param name="left">One media type.</param>
    /// <param name="right">The other media type.</param>
    public static bool Equivalent(string? left, string? right) =>
        Bare(left) is { } a && Bare(right) is { } b && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
