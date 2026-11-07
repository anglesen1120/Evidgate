using System.Security.Cryptography;

namespace Evidgate.Modules.Identity.Domain;

/// <summary>The statuses of an API token; only <c>revoked</c> is stored, expiry is computed from <c>expires_at</c>.</summary>
internal static class ApiTokenStatus
{
    public const string Active = "active";
    public const string Expired = "expired";
    public const string Revoked = "revoked";
}

/// <summary>
/// A project-scoped credential for scripts and automation: it carries an explicit permission
/// subset, always expires, and is shown once. Only the public prefix and the pepper HMAC of the
/// secret half are stored, so the row is useless without the pepper and the plaintext.
/// </summary>
internal sealed class ApiToken
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid ProjectId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Prefix { get; set; } = string.Empty;

    public byte[] TokenHmac { get; set; } = [];

    public int PepperVersion { get; set; }

    public string[] Permissions { get; set; } = [];

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public Guid CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int Version { get; set; } = 1;

    /// <summary>The derived status at <paramref name="now"/>; revocation wins over expiry.</summary>
    /// <param name="now">The request's clock.</param>
    public string StatusAt(DateTimeOffset now) =>
        RevokedAt is not null ? ApiTokenStatus.Revoked
        : ExpiresAt <= now ? ApiTokenStatus.Expired
        : ApiTokenStatus.Active;

    /// <summary>
    /// True when <paramref name="candidateHash"/> is the HMAC stored on this row. The comparison is
    /// constant-time: a wrong secret must not narrow down the stored digest.
    /// </summary>
    /// <param name="candidateHash">The HMAC of the presented secret under the row's pepper version.</param>
    public bool MatchesHash(ReadOnlySpan<byte> candidateHash) =>
        CryptographicOperations.FixedTimeEquals(TokenHmac, candidateHash);

    /// <summary>
    /// Revokes the token; revocation is final and a second call is refused as a bug, not a no-op. The instant is
    /// clamped to the row's <see cref="CreatedAt"/>: the column is written by the database's clock while the caller
    /// supplied this clock's, and a clock that sits behind the database by a few milliseconds would otherwise trip
    /// the table's <c>api_token_revoked_after_created_check</c> — a token revoked in the same breath it was minted.
    /// </summary>
    /// <param name="now">The transition time.</param>
    public void Revoke(DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            throw new InvalidOperationException($"api token {Id} is already revoked");
        }

        RevokedAt = now >= CreatedAt ? now : CreatedAt;
    }
}
