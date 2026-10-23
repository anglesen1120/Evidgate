using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Canonicalization;

namespace Evidgate.BuildingBlocks.Persistence.Audit;

/// <summary>
/// The one implementation of the chain's hash rule (TL T8), shared by the writer and the
/// verifier: <c>entry_hash = sha256(prev_hash ‖ RFC 8785 canonical row fields)</c>. The genesis
/// <c>prev_hash</c> is 32 zero bytes.
/// </summary>
public static class AuditHashing
{
    /// <summary>The <c>prev_hash</c> of an org's first row.</summary>
    public static readonly byte[] Genesis = new byte[32];

    /// <summary>The canonical bytes of the hashed fields (no hashes of the chain itself).</summary>
    /// <param name="row">The row (its own <c>PrevHash</c>/<c>EntryHash</c> are not part of the content).</param>
    public static byte[] CanonicalFields(AuditRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var fields = new JsonObject
        {
            ["action"] = row.Action,
            ["actor_id"] = row.ActorId,
            ["actor_type"] = row.ActorType,
            ["after_hash"] = row.AfterHash is null ? null : Convert.ToHexStringLower(row.AfterHash),
            ["before_hash"] = row.BeforeHash is null ? null : Convert.ToHexStringLower(row.BeforeHash),
            ["created_at"] = FormatTimestamp(row.CreatedAt),
            ["id"] = row.Id.ToString("D"),
            ["metadata"] = MetadataNode(row.Metadata),
            ["object_id"] = row.ObjectId,
            ["object_type"] = row.ObjectType,
            ["org_id"] = row.OrgId.ToString("D"),
            ["request_id"] = row.RequestId,
            ["seq"] = row.Seq,
            ["trace_id"] = row.TraceId,
        };

        return JsonCanonicalizer.CanonicalizeUtf8(fields);
    }

    /// <summary>Computes the <c>entry_hash</c> of <paramref name="row"/> given its predecessor's hash.</summary>
    /// <param name="prevHash">The previous row's <c>entry_hash</c>, or <see cref="Genesis"/>.</param>
    /// <param name="row">The row to hash.</param>
    public static byte[] EntryHash(byte[] prevHash, AuditRow row)
    {
        ArgumentNullException.ThrowIfNull(prevHash);

        var canonical = CanonicalFields(row);
        var input = new byte[prevHash.Length + canonical.Length];
        prevHash.CopyTo(input, 0);
        canonical.CopyTo(input, prevHash.Length);

        return SHA256.HashData(input);
    }

    /// <summary>Microsecond-precision UTC, the precision PostgreSQL stores (so a round trip hashes identically).</summary>
    /// <param name="value">The instant.</param>
    public static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    private static JsonObject? MetadataNode(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }

        var node = new JsonObject();
        foreach (var (key, value) in metadata)
        {
            node[key] = value;
        }

        return node;
    }

    /// <summary>UTF-8 helper for tests and callers hashing an object's canonical JSON.</summary>
    /// <param name="canonicalJson">Canonical JSON text.</param>
    public static byte[] Sha256(string canonicalJson) => SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
}
