using System.Buffers;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;

namespace Evidgate.Modules.Catalog.Domain;

/// <summary>
/// The identity of one fixture revision (03.3.1): its content hash and the resource prefix the executor names objects
/// with. Both are pure functions of the fixture and the request, so a retry, a preview and a concurrent create of the
/// same content agree without coordination — and the prefix in particular is never taken from a caller, because a
/// caller-chosen prefix would let one tenant's cleanup reach objects that are not its own.
/// </summary>
internal static class FixtureHasher
{
    /// <summary>The prefix every resource of a fixture revision carries.</summary>
    private const string PrefixRoot = "fx_";

    /// <summary>The names a revision is identified by; <c>hash_input_version</c> names the shape, so it can grow later.</summary>
    private const int HashInputVersion = 1;

    /// <summary>
    /// The content identity: the fixture key, the seed version, the timeout, the cleanup policy and the body pointer.
    /// Row ids, the resource prefix and the creation time are not part of it — a prefix is derived from it, and a
    /// revision that only moved in time is the same revision.
    /// </summary>
    /// <param name="fixtureKey">The fixture's key.</param>
    /// <param name="seedVersion">The seed version the body is at.</param>
    /// <param name="timeoutSeconds">How long the executor may take.</param>
    /// <param name="cleanupPolicy">How the executor cleans up.</param>
    /// <param name="bodyRef">The pointer to the body.</param>
    public static string Hash(string fixtureKey, string seedVersion, int timeoutSeconds, string cleanupPolicy, string bodyRef)
    {
        var buffer = new ArrayBufferWriter<byte>(fixtureKey.Length + seedVersion.Length + bodyRef.Length + 128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("hash_input_version", HashInputVersion);
            writer.WriteString("fixture_key", fixtureKey);
            writer.WriteString("seed_version", seedVersion);
            writer.WriteNumber("timeout_s", timeoutSeconds);
            writer.WriteString("cleanup_policy", cleanupPolicy);
            writer.WriteString("body_ref", bodyRef);
            writer.WriteEndObject();
        }

        return CanonicalHash.Compute(buffer.WrittenSpan);
    }

    /// <summary>
    /// The resource prefix of one revision: <c>fx_</c> and sixteen hex characters taken from the fixture id (the random
    /// half of the UUIDv7, so two fixtures created in the same millisecond differ) and from the content hash (so two
    /// revisions of one fixture differ). Deterministic, and never a value a caller can choose.
    /// </summary>
    /// <param name="fixtureId">The fixture the revision belongs to.</param>
    /// <param name="revisionHash">The revision's content hash, <c>sha256:</c> and lower-case hex.</param>
    public static string ResourcePrefix(Guid fixtureId, string revisionHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(revisionHash);
        if (!revisionHash.StartsWith(CanonicalHash.Prefix, StringComparison.Ordinal) || revisionHash.Length <= CanonicalHash.Prefix.Length + 8)
        {
            throw new ArgumentException("a revision hash is 'sha256:' and lower-case hex", nameof(revisionHash));
        }

        // The last 8 hex characters of the id are its random part; UUIDv7's timestamp prefix would collide.
        var identified = fixtureId.ToString("N");
        var hex = string.Concat(PrefixRoot.AsSpan(), identified.AsSpan(identified.Length - 8), revisionHash.AsSpan(CanonicalHash.Prefix.Length, 8));

        return hex;
    }
}
