using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;

namespace Evidgate.Modules.Catalog.Application;

/// <summary>
/// The fixture references of a suite revision's <c>environment_requirements</c> (03.3.1 AC7): each entry names a fixture
/// key and the revision number it pins, and both must exist in the suite's own project. Read-only, so the create command
/// and the dry-run preview answer the same and neither can write a reference the other would refuse.
/// </summary>
internal static class FixtureReferences
{
    /// <summary>The most references one revision may pin; the schema caps the array at the same number.</summary>
    internal const int MaxFixtures = 32;

    /// <summary>The <c>fixtures</c> member of an environment document that already passed its schema.</summary>
    private const string Member = "fixtures";

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
            var revisionText = Text(entry, "revision");
            if (name is null || revisionText is null || !FixtureRules.IsValidKey(name) || !int.TryParse(revisionText, out var revision) || revision < 1)
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

            pinned.Add((path, new FixtureRef(name, revision)));
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
    private static string? Text(JsonElement entry, string member)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty(member, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetRawText();
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
