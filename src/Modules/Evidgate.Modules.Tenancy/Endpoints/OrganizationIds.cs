using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Evidgate.Modules.Tenancy.Endpoints;

/// <summary>
/// The id of the organization a <c>POST /v1/orgs</c> creates (02.1.1 §3, AC6). The
/// request has no tenant yet, and the C2 pipeline opens its transaction — and its
/// idempotency lookup, which is tenant-keyed — in the request tenant. Deriving the
/// new org's id from the caller and the <c>Idempotency-Key</c> makes a retry land in
/// the same tenant, where the stored response replays; a changed body under the same
/// key is the 409 <c>idempotency_conflict</c> of T11.
/// </summary>
/// <remarks>
/// A name-based UUID (RFC 9562 version 8): SHA-256 over a fixed domain label, the
/// user id and the key, truncated to 122 bits with the version and variant set. The
/// user id comes from the authenticated principal, never from input, so a caller can
/// only ever derive ids in its own key space; knowing another user's id and key gives
/// no access, because the org row and every tenant row stay behind RLS and membership.
/// It is not UUIDv7, so org ids carry no creation time; <c>organization</c> is a small
/// table keyed by this id alone and loses nothing measurable from random placement.
/// </remarks>
internal static class OrganizationIds
{
    private static readonly byte[] DomainLabel = "evidgate:organization:v1"u8.ToArray();

    /// <summary>The org id for <paramref name="userId"/> creating under <paramref name="idempotencyKey"/>.</summary>
    internal static Guid For(Guid userId, string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKey);

        var keyBytes = Encoding.UTF8.GetBytes(idempotencyKey);
        var input = new byte[DomainLabel.Length + 1 + 16 + 4 + keyBytes.Length];
        var span = input.AsSpan();

        DomainLabel.CopyTo(span);
        var offset = DomainLabel.Length + 1; // a 0x00 separator after the label
        userId.TryWriteBytes(span[offset..], bigEndian: true, out _);
        offset += 16;
        BinaryPrimitives.WriteInt32BigEndian(span[offset..], keyBytes.Length);
        offset += 4;
        keyBytes.CopyTo(span[offset..]);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);

        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 9562 variant

        return new Guid(bytes, bigEndian: true);
    }
}
