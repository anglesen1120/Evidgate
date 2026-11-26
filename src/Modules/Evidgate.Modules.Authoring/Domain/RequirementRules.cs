using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.Canonicalization;

namespace Evidgate.Modules.Authoring.Domain;

/// <summary>The closed vocabulary and field rules of a requirement and its immutable versions (migration 0132).</summary>
internal static partial class RequirementRules
{
    /// <summary>Highest body length, counted in Unicode code points after NFC (matches the column CHECK).</summary>
    internal const int MaximumBodyCodePoints = 20_000;

    /// <summary>Highest title length in code points.</summary>
    internal const int MaximumTitleCodePoints = 200;

    /// <summary>Highest reason length in code points.</summary>
    internal const int MaximumReasonCodePoints = 500;

    /// <summary>Highest external reference length.</summary>
    internal const int MaximumExternalRefLength = 128;

    /// <summary>The five requirement kinds.</summary>
    internal static IReadOnlyList<string> Kinds { get; } =
        ["user_story", "acceptance_criterion", "epic", "spec_section", "other"];

    /// <summary>Who produced a version.</summary>
    internal static IReadOnlyList<string> Sources { get; } = ["manual", "import", "tracker", "ai"];

    /// <summary>True when <paramref name="kind"/> is one of <see cref="Kinds"/>.</summary>
    internal static bool IsKind(string? kind) => kind is not null && Kinds.Contains(kind, StringComparer.Ordinal);

    /// <summary>True when <paramref name="source"/> is one of <see cref="Sources"/>.</summary>
    internal static bool IsSource(string? source) => source is not null && Sources.Contains(source, StringComparer.Ordinal);

    /// <summary>
    /// True for a trimmed 1..128 character label of the catalog's requirement-reference alphabet; the same rule as
    /// <c>test_case.requirement_ref</c>. It is a label, never a key to a tracker.
    /// </summary>
    internal static bool IsExternalRef(string? value) =>
        value is not null
        && value.Length is >= 1 and <= MaximumExternalRefLength
        && ExternalRefPattern().IsMatch(value);

    /// <summary>False when the text holds a NUL (PostgreSQL <c>text</c> refuses it) or an unpaired surrogate (NFC throws on it).</summary>
    /// <param name="value">The submitted text; null is storable (the caller decides if it is required).</param>
    internal static bool IsStorable(string? value)
    {
        if (value is null)
        {
            return true;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\0' || !Rune.TryGetRuneAt(value, index, out var rune))
            {
                return false;
            }

            if (!rune.IsBmp)
            {
                index++;
            }
        }

        return true;
    }

    /// <summary>
    /// The stored form of a body: NFC first, then the code-point cap, so a decomposed input that composes to the cap
    /// is accepted and its stored length is the one the database CHECK counts.
    /// </summary>
    /// <param name="body">The submitted body; null is refused by the caller.</param>
    /// <param name="normalized">The NFC body when it fits.</param>
    /// <returns>True when the normalized body is within the cap.</returns>
    internal static bool TryNormalizeBody(string body, out string normalized)
    {
        ArgumentNullException.ThrowIfNull(body);

        normalized = AuthoringText.Normalize(body) ?? string.Empty;

        return AuthoringText.Fits(normalized, MaximumBodyCodePoints);
    }

    /// <summary><c>sha256:&lt;hex&gt;</c> over the RFC 8785 form of <c>{"body": body}</c> (00 §7).</summary>
    /// <param name="normalizedBody">The NFC body.</param>
    internal static string ContentHash(string normalizedBody)
    {
        ArgumentNullException.ThrowIfNull(normalizedBody);

        return CanonicalHash.Compute(new JsonObject { ["body"] = normalizedBody }.ToJsonString());
    }

    [GeneratedRegex("\\A[A-Za-z0-9._:/#-]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExternalRefPattern();
}
