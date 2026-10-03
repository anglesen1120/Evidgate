namespace Evidgate.Modules.Identity.Domain;

/// <summary>The states of a <c>ci_binding</c> (00 §8.9, T70).</summary>
internal static class CiBindingStatus
{
    public const string PendingVerification = "pending_verification";
    public const string Active = "active";
    public const string Disabled = "disabled";
}

/// <summary>
/// Which repository of which provider may mint <c>ci_producer</c> tokens for a project, from which refs
/// and workflow files (02.4.1 §4). Trust follows the immutable repository id (D4), never the name.
/// </summary>
internal sealed class CiBinding
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid ProjectId { get; set; }

    public string Provider { get; set; } = "github_actions";

    public long RepositoryId { get; set; }

    public string RepositoryFullName { get; set; } = string.Empty;

    public string[] AllowedRefPatterns { get; set; } = [];

    public string[] AllowedWorkflowRefs { get; set; } = [];

    public string Status { get; set; } = CiBindingStatus.PendingVerification;

    public byte[]? BindingChallengeHash { get; set; }

    public int? ChallengePepperVersion { get; set; }

    public DateTimeOffset? ChallengeIssuedAt { get; set; }

    public DateTimeOffset? ActivatedAt { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int Version { get; set; } = 1;
}

/// <summary>One minted OIDC exchange (<c>ci_token_issue</c>); <c>jti</c> is unique, which makes a replay fail.</summary>
internal sealed class CiTokenIssue
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid BindingId { get; set; }

    public string Provider { get; set; } = "github_actions";

    public string Jti { get; set; } = string.Empty;

    public long RepositoryId { get; set; }

    public string RepositoryFullName { get; set; } = string.Empty;

    public string Ref { get; set; } = string.Empty;

    public string Sha { get; set; } = string.Empty;

    public long RunId { get; set; }

    public int RunAttempt { get; set; }

    public string JobWorkflowRef { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int Version { get; set; } = 1;
}

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
