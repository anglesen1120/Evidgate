using System.Buffers;
using System.Text.Json;
using Evidgate.Modules.Orchestrator.Contracts;

namespace Evidgate.Modules.Orchestrator.Application.Releases;

/// <summary>One component of a release as the match compares it: the key and the digest the release requires.</summary>
/// <param name="ComponentKey">The component's key inside its release.</param>
/// <param name="Digest">The digest the release requires for that key.</param>
internal sealed record ReleaseComponentDigest(string ComponentKey, string Digest);

/// <summary>The outcome of comparing one release with one run's recorded components.</summary>
/// <param name="Bound">The components whose digests agree, in release component-key order.</param>
/// <param name="Mismatched">The components that do not agree, in release component-key order.</param>
internal sealed record ReleaseMatchOutcome(
    IReadOnlyList<ReleaseBoundComponent> Bound,
    IReadOnlyList<ReleaseMismatch> Mismatched);

/// <summary>
/// The digest match: release components against the run's recorded <c>components</c> object. Pure and in-memory, so the
/// same function serves the run-create hook (on the body it is about to store) and the explicit bind (on the stored
/// row). There is no time or recency input: a component binds only on an exact digest equality, and a release key the
/// run does not record is a mismatch, never a wildcard. Keys the run records but the release does not declare are
/// ignored — a release says what it requires, and a run's extra build inputs are not its concern.
/// </summary>
internal static class ReleaseMatch
{
    /// <summary>The digest hex alphabet, cached because every mismatched component is checked against it.</summary>
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    /// <summary>The reason for a key both sides carry at different digests.</summary>
    internal const string DigestMismatch = "digest_mismatch";

    /// <summary>The reason for a release key the run recorded no usable digest for.</summary>
    internal const string ComponentMissing = "component_missing";

    /// <summary>No run component: every release component is missing, and nothing binds.</summary>
    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The bound and mismatched halves of <paramref name="components"/> against <paramref name="runDigests"/>.</summary>
    /// <param name="components">The release's components in <c>component_key</c> order.</param>
    /// <param name="runDigests">The run's recorded key → digest pairs.</param>
    internal static ReleaseMatchOutcome Match(
        IEnumerable<ReleaseComponentDigest> components,
        IReadOnlyDictionary<string, string> runDigests)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(runDigests);

        var bound = new List<ReleaseBoundComponent>();
        var mismatched = new List<ReleaseMismatch>();

        foreach (var component in components)
        {
            if (!runDigests.TryGetValue(component.ComponentKey, out var actual))
            {
                mismatched.Add(new ReleaseMismatch(component.ComponentKey, ComponentMissing, component.Digest, null));
            }
            else if (string.Equals(actual, component.Digest, StringComparison.Ordinal))
            {
                bound.Add(new ReleaseBoundComponent(component.ComponentKey, component.Digest));
            }
            else
            {
                // The run's value is echoed only when it is a digest: a body may record any string for a key, and an
                // arbitrary one would be reflected back to a caller that needs only `run:create` and stored in the
                // idempotency record. Anything else is "recorded nothing usable", the same answer a non-string gets.
                mismatched.Add(IsDigest(actual)
                    ? new ReleaseMismatch(component.ComponentKey, DigestMismatch, component.Digest, actual)
                    : new ReleaseMismatch(component.ComponentKey, ComponentMissing, component.Digest, null));
            }
        }

        return new ReleaseMatchOutcome(bound, mismatched);
    }

    /// <summary>
    /// True when a run's recorded value has the release digest's own shape (<c>sha256:</c> + 64 lower-case hex). The
    /// match compares strings, so this only decides whether a mismatched value is safe to echo back.
    /// </summary>
    /// <param name="value">The run's recorded value for one component key.</param>
    private static bool IsDigest(string value) =>
        value.Length == 71
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && value.AsSpan(7).IndexOfAnyExcept(LowerHex) < 0;

    /// <summary>
    /// The run's recorded components as the match reads them: string members only. The column is <c>jsonb</c> the
    /// client filled, so a member's value may be any JSON; only a string can be a digest, and a key whose value is not
    /// one is treated as recorded with nothing usable rather than compared against a rendering of that value. A
    /// repeated member resolves last-wins, exactly as <c>jsonb</c> stores it.
    /// </summary>
    /// <param name="components">The stored <c>run.components</c> text, or null.</param>
    internal static IReadOnlyDictionary<string, string> ParseRunComponents(string? components)
    {
        if (string.IsNullOrWhiteSpace(components))
        {
            return Empty;
        }

        using var document = JsonDocument.Parse(components);
        return Digests(document.RootElement);
    }

    /// <summary>The same reading for a body element the caller has already parsed and is about to store.</summary>
    /// <param name="components">The body's <c>components</c> member, or null when it carried none.</param>
    internal static IReadOnlyDictionary<string, string> ParseRunComponents(JsonElement? components) =>
        components is { } element ? Digests(element) : Empty;

    private static IReadOnlyDictionary<string, string> Digests(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in root.EnumerateObject())
        {
            if (member.Value.ValueKind == JsonValueKind.String)
            {
                digests[member.Name] = member.Value.GetString()!;
            }
            else
            {
                digests.Remove(member.Name);
            }
        }

        return digests;
    }
}
