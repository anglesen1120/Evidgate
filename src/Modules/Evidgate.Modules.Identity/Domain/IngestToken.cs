namespace Evidgate.Modules.Identity.Domain;

/// <summary>A revocable, rotatable ingest token (02.4.2 §4). Only the prefix and the pepper HMAC are stored (T5).</summary>
internal sealed class IngestToken
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid ProjectId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Prefix { get; set; } = string.Empty;

    public byte[] TokenHmac { get; set; } = [];

    public int PepperVersion { get; set; }

    public string[] Scopes { get; set; } = [];

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? OverlapEndsAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public Guid? RotatedToId { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int Version { get; set; } = 1;

    /// <summary>The derived status at <paramref name="now"/> (revocation is stored, expiry is computed).</summary>
    public string StatusAt(DateTimeOffset now) =>
        RevokedAt is not null ? "revoked"
        : (ExpiresAt is { } expires && expires <= now) || (OverlapEndsAt is { } overlap && overlap <= now) ? "expired"
        : RotatedToId is not null ? "rotated"
        : "active";
}
