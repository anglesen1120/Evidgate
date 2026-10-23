using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Canonicalization;

namespace Evidgate.Modules.Authoring.Domain;

/// <summary>
/// The two RFC 8785 digests of the authoring module (00 §7): <c>content_hash</c> of a version snapshot (13.1.1 AC5,
/// AC14) and the <c>"order:&lt;sha256&gt;"</c> sibling-order hash that is the <c>If-Match</c> of a reorder (AC2, AC19)
/// and the batch ETag 13.1.2 reuses (§4 [DECISION] line 513, H6).
/// </summary>
internal static class AuthoringHashes
{
    /// <summary>The prefix of the sibling-order hash: an order ETag never compares equal to a content hash.</summary>
    internal const string OrderPrefix = "order:";

    /// <summary>
    /// <c>content_hash</c> of a snapshot: <c>sha256:&lt;lowercase hex&gt;</c> over the RFC 8785 canonical form of the
    /// content object — the same function 00 §7 defines for manifests, so the export of AC14 and the timeline of AC9
    /// compare the same digest.
    /// </summary>
    /// <param name="snapshot">The normalised snapshot.</param>
    internal static string ContentHash(AuthoringSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return CanonicalHash.Compute(AuthoringContent.Serialize(snapshot));
    }

    /// <summary>
    /// The sibling-order hash: <c>order:&lt;sha256:hex&gt;</c> over the <c>(id, version)</c> pairs of the siblings the
    /// caller read, sorted by id so the same set in any order hashes the same. 13.1.2's batch ETag uses this shape too,
    /// which is why it lives in one helper rather than in the reorder command.
    /// </summary>
    /// <param name="versions">The siblings' ids and versions; order does not matter.</param>
    internal static string OrderHash(IEnumerable<(Guid Id, int Version)> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);

        var ordered = versions
            .OrderBy(pair => pair.Id)
            .Select(pair => (JsonNode)new JsonObject
            {
                ["id"] = pair.Id.ToString("D"),
                ["version"] = pair.Version,
            })
            .ToArray();

        var canonical = new JsonArray(ordered).ToJsonString();

        return string.Concat(OrderPrefix, CanonicalHash.Compute(canonical));
    }
}
