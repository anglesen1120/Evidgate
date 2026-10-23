using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Evidgate.BuildingBlocks.Canonicalization;

/// <summary>
/// The 00 §7 digest: <c>sha256:&lt;lowercase hex&gt;</c> of the RFC 8785
/// canonical JSON of a manifest, policy, candidate or evidence record. The CLI's
/// <c>canonicalHash</c> (<c>packages/cli/src/canonical.ts</c>) must return the
/// same string for the same document (01.3.2 AC5).
/// </summary>
public static class CanonicalHash
{
    /// <summary>Algorithm prefix written in front of every digest (00 §7).</summary>
    public const string Prefix = "sha256:";

    /// <summary>Digest of the canonical form of a JSON document given as UTF-8.</summary>
    public static string Compute(ReadOnlySpan<byte> utf8Json)
    {
        var writer = new ArrayBufferWriter<byte>(utf8Json.Length + 16);
        JsonCanonicalizer.Write(utf8Json, writer);
        return FromCanonical(writer.WrittenSpan);
    }

    /// <summary>Digest of the canonical form of a JSON text document.</summary>
    /// <exception cref="JsonCanonicalizationException">
    /// The text cannot be canonicalized — including an unpaired surrogate in
    /// <paramref name="json"/>, which the strict UTF-8 encoder rejects instead of
    /// substituting U+FFFD.
    /// </exception>
    public static string Compute(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Compute(JsonCanonicalizer.EncodeStrict(json));
    }

    /// <summary>Digest of a parsed <see cref="JsonElement"/>.</summary>
    public static string Compute(JsonElement element) => Compute(element.GetRawText());

    /// <summary>Digest of JSON bytes that are already canonical; no normalization is applied.</summary>
    public static string FromCanonical(ReadOnlySpan<byte> canonicalUtf8)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(canonicalUtf8, digest);
        return Prefix + Convert.ToHexStringLower(digest);
    }
}
