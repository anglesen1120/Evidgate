namespace Evidgate.Modules.Catalog.Domain;

/// <summary>Statuses of <c>test_case.status</c> (03.1.1 §6).</summary>
internal static class CaseStatus
{
    /// <summary>The file is in the synced tree.</summary>
    public const string Active = "active";

    /// <summary>The file vanished from the synced tree; the id may come back.</summary>
    public const string Missing = "missing";

    /// <summary>Retired by hand; stays readable.</summary>
    public const string Archived = "archived";
}

/// <summary>
/// The write-model row of <c>public.test_case</c> (migration 0020). The identity columns
/// (<see cref="AnnotationId"/>, <see cref="FirstSeenCommit"/>, project, id) are assigned once at creation and
/// guarded by the <c>catalog_case_identity_guard</c> trigger as well.
/// </summary>
internal sealed class TestCase
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid AnnotationId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Framework { get; set; } = string.Empty;

    public string? Language { get; set; }

    public string? FilePath { get; set; }

    public string Status { get; set; } = CaseStatus.Active;

    public Guid? OwnerMembershipId { get; set; }

    public string[] Labels { get; set; } = [];

    /// <summary>
    /// The external requirement this case protects, or null. Mutable metadata: no revision or hash reads it.
    /// </summary>
    public string? RequirementRef { get; set; }

    public string FirstSeenCommit { get; set; } = string.Empty;

    public string? LastSeenCommit { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int Version { get; set; } = 1;
}

/// <summary>One row of <c>public.case_identity_source</c>: append-only.</summary>
internal sealed class CaseIdentitySource
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid CaseId { get; set; }

    public string Source { get; set; } = string.Empty;

    public string RawKey { get; set; } = string.Empty;

    public string CommitSha { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One row of <c>public.case_rename_history</c>: append-only.</summary>
internal sealed class CaseRenameHistory
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid CaseId { get; set; }

    public string FromPath { get; set; } = string.Empty;

    public string ToPath { get; set; } = string.Empty;

    public string CommitSha { get; set; } = string.Empty;

    public DateTimeOffset DetectedAt { get; set; }
}
