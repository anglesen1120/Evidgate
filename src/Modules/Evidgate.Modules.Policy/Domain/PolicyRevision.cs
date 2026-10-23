namespace Evidgate.Modules.Policy.Domain;

/// <summary>States of <c>policy_revision.state</c> (07.1.1 §6).</summary>
internal static class PolicyRevisionState
{
    /// <summary>Written, not yet the project's active policy.</summary>
    public const string Draft = "draft";

    /// <summary>The project's active policy revision.</summary>
    public const string Active = "active";

    /// <summary>Superseded by a later activation; terminal.</summary>
    public const string Retired = "retired";
}

/// <summary>
/// The write-model row of <c>public.policy_revision</c> (migration 0025, 07.1.1-T1): one immutable policy revision.
/// No property carries a <c>set</c> or <c>init</c> accessor (00 §5.5 C6), so the write model cannot be mutated after
/// construction; EF Core binds the row through this constructor and the compiler-generated backing fields, and the
/// database refuses UPDATE/DELETE for the app role while the <c>policy_revision_immutable</c> trigger refuses them
/// for every role.
/// </summary>
internal sealed class PolicyRevision
{
    /// <summary>Materialises one revision (EF Core constructor binding, 00 §5.5 C4).</summary>
    public PolicyRevision(
        Guid id,
        Guid orgId,
        Guid projectId,
        int revisionNo,
        string hash,
        string body,
        string state,
        Guid createdBy,
        DateTimeOffset createdAt,
        int version)
    {
        Id = id;
        OrgId = orgId;
        ProjectId = projectId;
        RevisionNo = revisionNo;
        Hash = hash;
        Body = body;
        State = state;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
        Version = version;
    }

    /// <summary>UUIDv7 id (00 §7).</summary>
    public Guid Id { get; }

    /// <summary>RLS scope (00 §5.4).</summary>
    public Guid OrgId { get; }

    /// <summary>The project the revision belongs to.</summary>
    public Guid ProjectId { get; }

    /// <summary>Append-only counter per project (07.1.1 §4).</summary>
    public int RevisionNo { get; }

    /// <summary>The content address, <c>sha256:&lt;64 hex&gt;</c> (00 §7).</summary>
    public string Hash { get; }

    /// <summary>The canonical revision body (00 §8.6), stored as <c>jsonb</c>.</summary>
    public string Body { get; }

    /// <summary>The lifecycle state (07.1.1 §6).</summary>
    public string State { get; }

    /// <summary>The member that wrote the revision.</summary>
    public Guid CreatedBy { get; }

    /// <summary>Commit timestamp (00 §7).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Optimistic-concurrency token (00 §7).</summary>
    public int Version { get; }
}
