using System.Security.Cryptography;
using System.Text;

namespace Evidgate.Modules.Ingestion.Domain;

/// <summary>
/// Accepted manifest. Skeleton aggregate of 01.1.2-T3.1: it only assigns the
/// identity (UUIDv7, 00 §7) and the content hash — shard, publisher and build
/// rules arrive with 04. Immutable by construction (00 §5.5 C6).
/// </summary>
internal sealed record Manifest(string ManifestId, string ProjectRef, string SourceRef, string Hash)
{
    /// <summary>Accepts a submission: new UUIDv7 id and the hash of its content.</summary>
    internal static Manifest Accept(string projectRef, string sourceRef, string content) => new(
        Guid.CreateVersion7().ToString(),
        projectRef,
        sourceRef,
        HashContent(content));

    /// <summary>
    /// 00 §7 hash shape (<c>sha256:&lt;hex&gt;</c>) over the submitted content.
    /// RFC 8785 canonicalization of the manifest document lands with 01.3.2/04,
    /// so the skeleton hashes the bytes it was given.
    /// </summary>
    private static string HashContent(string content) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
