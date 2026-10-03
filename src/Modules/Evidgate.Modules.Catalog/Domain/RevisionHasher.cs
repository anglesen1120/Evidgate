using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;

namespace Evidgate.Modules.Catalog.Domain;

/// <summary>What identifies one version of a test (03.1.3 AC3, AC4).</summary>
/// <param name="ContentSha256"><c>sha256:</c> of the exact source bytes (UTF-8).</param>
/// <param name="AssertionHash"><c>sha256:</c> over the canonical array of assertion descriptors.</param>
/// <param name="MetadataJson">The small metadata object that is hashed and stored (counts and flags only).</param>
/// <param name="RevisionHash"><c>sha256:</c> over the canonical {content_sha256, framework, language, assertion_hash, metadata}; the path is not part of it.</param>
internal sealed record RevisionDescriptor(string ContentSha256, string AssertionHash, string MetadataJson, string RevisionHash);

/// <summary>
/// Computes the identity of a test revision (03.1.3-T1.2). Pure and deterministic: the same source, framework and
/// language always give the same <c>revision_hash</c> on every node (RFC 8785 canonical JSON, 00 §7), and
/// <c>file_path</c> is deliberately absent so a rename-only commit reuses the revision (AC3). Only hashes and counts
/// leave this class — the source text itself is never stored (S3).
/// </summary>
internal static class RevisionHasher
{
    /// <summary>Describes one test file.</summary>
    /// <param name="framework">The framework name.</param>
    /// <param name="language">The language, when known.</param>
    /// <param name="source">The exact source text.</param>
    public static RevisionDescriptor Describe(string framework, string? language, string source)
    {
        ArgumentNullException.ThrowIfNull(framework);
        ArgumentNullException.ThrowIfNull(source);

        var contentHash = Sha256(Encoding.UTF8.GetBytes(source));
        var extraction = AssertionExtractor.Extract(framework, source);
        var assertionHash = CanonicalHash.Compute(JsonSerializer.Serialize(extraction.Descriptors));
        var metadata = JsonSerializer.Serialize(new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["assertion_count"] = extraction.Descriptors.Count,
            ["assertions_extracted"] = extraction.Extracted,
        });

        return new RevisionDescriptor(contentHash, assertionHash, metadata, RevisionHash(contentHash, framework, language, assertionHash, metadata));
    }

    /// <summary>The revision hash of the five identity members.</summary>
    /// <param name="contentSha256">Hash of the source.</param>
    /// <param name="framework">Framework.</param>
    /// <param name="language">Language, or null.</param>
    /// <param name="assertionHash">Hash of the assertion descriptors.</param>
    /// <param name="metadataJson">The metadata object as JSON.</param>
    public static string RevisionHash(string contentSha256, string framework, string? language, string assertionHash, string metadataJson)
    {
        using var metadata = JsonDocument.Parse(metadataJson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("content_sha256", contentSha256);
            writer.WriteString("framework", framework);
            if (language is null)
            {
                writer.WriteNull("language");
            }
            else
            {
                writer.WriteString("language", language);
            }

            writer.WriteString("assertion_hash", assertionHash);
            writer.WritePropertyName("metadata");
            metadata.RootElement.WriteTo(writer);
            writer.WriteEndObject();
        }

        return CanonicalHash.Compute(stream.ToArray());
    }

    private static string Sha256(byte[] bytes) => CanonicalHash.Prefix + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
