namespace Evidgate.Modules.Evidence.Domain;

/// <summary>
/// The write-model row of <c>public.artifact_kind_policy</c> (migration 0028, 06.1.1-T1): the per-project capture
/// policy of one artifact kind — the size cap, the accepted media types and the sensitivity a minted artifact
/// inherits. No property carries a <c>set</c> or <c>init</c> accessor (00 §5.5 C6); EF Core binds the row through
/// this constructor and the compiler-generated backing fields, and the closed lists of <c>kind</c> and
/// <c>default_sensitivity</c> are enforced here as well as by the migration's CHECK constraints (C6).
/// </summary>
internal sealed class ArtifactKindPolicy
{
    /// <summary>Materialises one kind policy (EF Core constructor binding, 00 §5.5 C4).</summary>
    public ArtifactKindPolicy(
        Guid id,
        Guid orgId,
        Guid projectId,
        string kind,
        long maxBytes,
        string[] mediaTypes,
        string defaultSensitivity,
        bool rejectOnSecret = false,
        bool captureEnabled = true,
        int version = 1,
        DateTimeOffset createdAt = default,
        DateTimeOffset updatedAt = default)
    {
        Id = id;
        OrgId = orgId;
        ProjectId = projectId;
        Kind = ArtifactKind.Ensure(kind);
        MaxBytes = maxBytes;
        MediaTypes = mediaTypes;
        DefaultSensitivity = ArtifactSensitivity.Ensure(defaultSensitivity);
        RejectOnSecret = rejectOnSecret;
        CaptureEnabled = captureEnabled;
        Version = version;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>UUIDv7 id (00 §7).</summary>
    public Guid Id { get; }

    /// <summary>RLS scope (00 §5.4).</summary>
    public Guid OrgId { get; }

    /// <summary>The project the policy belongs to.</summary>
    public Guid ProjectId { get; }

    /// <summary>One of <see cref="ArtifactKind.All"/> (00 §7); unique per project.</summary>
    public string Kind { get; }

    /// <summary>The largest artifact of this kind the project accepts.</summary>
    public long MaxBytes { get; }

    /// <summary>The media types the project accepts for this kind.</summary>
    public string[] MediaTypes { get; }

    /// <summary>One of <see cref="ArtifactSensitivity.All"/>; the sensitivity a minted artifact inherits.</summary>
    public string DefaultSensitivity { get; }

    /// <summary>True when a masking-scan hit rejects the artifact instead of flagging it (06.2.x).</summary>
    public bool RejectOnSecret { get; }

    /// <summary>False when the project has switched capture of this kind off.</summary>
    public bool CaptureEnabled { get; }

    /// <summary>Optimistic-concurrency token (00 §7); the trigger owns the increment.</summary>
    public int Version { get; }

    /// <summary>Commit timestamp (00 §7).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Last write, UTC; the trigger owns it (00 §7).</summary>
    public DateTimeOffset UpdatedAt { get; }
}
