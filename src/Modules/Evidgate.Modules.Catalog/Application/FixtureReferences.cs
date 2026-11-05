using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;

namespace Evidgate.Modules.Catalog.Application;

/// <summary>
/// The fixture references of a suite revision's <c>environment_requirements</c>: each entry names a fixture key and the
/// revision it pins, and both must exist in the suite's own project. Read-only, so the create command and the dry-run
/// preview answer alike and neither can accept a reference the other would refuse.
/// </summary>
internal static class FixtureReferences
{
    /// <summary>The <c>fixtures</c> member of an environment document that already passed its schema.</summary>
    private const string Member = "fixtures";

    /// <summary>Longest digit run a decimal holds without rounding; past it a number's own value is not what is read.</summary>
    private const int MaxExactDigits = 28;

    /// <summary>
    /// Checks the references of <paramref name="environment"/>. Returns null when every reference is held by a fixture of
    /// <paramref name="projectId"/> at its own revision number, otherwise the 422 that names the offending entries.
    /// </summary>
    /// <param name="registry">The fixture side of the catalog.</param>
    /// <param name="projectId">The project the referencing suite belongs to.</param>
    /// <param name="environment">The environment requirements, schema-checked by the caller.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<Error?> CheckAsync(
        IFixtureRegistry registry,
        Guid projectId,
        JsonElement environment,
        CancellationToken cancellationToken)
    {
        if (!environment.TryGetProperty(Member, out var fixtures) || fixtures.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var pinned = new List<(string Path, FixtureRef Reference)>(fixtures.GetArrayLength());
        var details = new List<ErrorDetail>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;
        foreach (var entry in fixtures.EnumerateArray())
        {
            var path = $"/environment_requirements/{Member}/{position}";
            position++;

            var name = Text(entry, "name");
            var revision = Revision(entry);
            if (name is null || !FixtureRules.IsValidKey(name) || revision is null)
            {
                details.Add(new ErrorDetail(path, "invalid_value"));

                continue;
            }

            // One fixture pinned twice would mean two versions of the same data in one run: the executor could satisfy
            // either, so the request is refused instead of resolved by order.
            if (!seen.Add(name))
            {
                details.Add(new ErrorDetail($"{path}/name", "duplicate"));

                continue;
            }

            pinned.Add((path, new FixtureRef(name, revision.Value)));
        }

        if (details.Count > 0)
        {
            return FixtureErrors.Invalid("a fixture requirement needs a fixture key and a revision number, once per fixture", [.. details]);
        }

        var known = await registry.ExistsAsync(projectId, [.. pinned.Select(pair => pair.Reference)], cancellationToken).ConfigureAwait(false);
        var missing = pinned.Where(pair => !known.Contains(pair.Reference)).ToList();
        if (missing.Count == 0)
        {
            return null;
        }

        // The path of the entry, not its ordinal among the valid ones, so the client can point at the member it sent.
        return FixtureErrors.CrossProject(
            [.. missing.Take(50).Select(pair => new ErrorDetail(pair.Path, "unknown_fixture_revision"))]);
    }

    /// <summary>A member as text, or null when it is absent, not a string, or an escaped lone surrogate.</summary>
    private static string? Text(JsonElement entry, string member) =>
        TryMember(entry, member, out var value) && value.ValueKind == JsonValueKind.String && TryText(value, out var text)
            ? text
            : null;

    /// <summary>
    /// The pinned revision: the schema reads <c>1.0</c> and <c>1e0</c> as integers, whose text is not an int, so a number
    /// is read as a number and only a whole one inside the column's range is accepted.
    /// </summary>
    private static int? Revision(JsonElement entry)
    {
        if (!TryMember(entry, "revision", out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => Whole(value),
            JsonValueKind.String => Numeric(value),
            _ => null,
        };
    }

    /// <summary>
    /// A revision sent as a JSON number, or null unless its own digits spell a whole number from 1 to the column's
    /// ceiling. A mantissa longer than a decimal can hold is refused rather than rounded, so a fraction can never pass
    /// as whole; every in-range integer needs at most ten digits, so nothing a client means is turned away here.
    /// </summary>
    private static int? Whole(JsonElement value)
    {
        var raw = value.GetRawText();
        var digits = 0;
        foreach (var symbol in raw)
        {
            if (symbol is < '0' or > '9')
            {
                continue;
            }

            if (++digits > MaxExactDigits)
            {
                return null;
            }
        }

        return decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && number == decimal.Truncate(number)
            && number is >= 1 and <= int.MaxValue
                ? (int)number
                : null;
    }

    /// <summary>A revision sent as text, which the schema forbids but an older client may still send.</summary>
    private static int? Numeric(JsonElement value) =>
        TryText(value, out var text)
        && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision)
        && revision >= 1
            ? revision
            : null;

    /// <summary>The text of a string element; false for an escaped lone surrogate, which has no UTF-16 text.</summary>
    private static bool TryText(JsonElement value, out string text)
    {
        try
        {
            text = value.GetString() ?? string.Empty;

            return true;
        }
        catch (InvalidOperationException)
        {
            text = string.Empty;

            return false;
        }
    }

    private static bool TryMember(JsonElement entry, string member, out JsonElement value)
    {
        value = default;

        return entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(member, out value);
    }
}
