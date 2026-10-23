namespace Evidgate.Modules.Authoring.Domain;

/// <summary>
/// The write-model row of <c>public.feature_node</c> (migration 0039, 13.1.1 §4): one node of the tree
/// (module → feature → category → sub-category). The rules that place it live in <see cref="FeatureNodeRules"/>; this
/// class is the row the EF Core 10 write model maps (00 §5.5 C4).
/// </summary>
internal sealed class FeatureNode
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid? ParentId { get; set; }

    public string Kind { get; set; } = FeatureNodeKinds.Module;

    public string Title { get; set; } = string.Empty;

    public int Position { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int Version { get; set; }
}

/// <summary>
/// The write-model row of <c>public.checklist_item</c> (13.1.1 §4): a prioritised requirement under a
/// <c>category</c> or <c>sub_category</c>. <see cref="ChecklistItemStateMachine"/> owns its <c>draft → active →
/// retired</c> moves.
/// </summary>
internal sealed class ChecklistItem
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid FeatureNodeId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Priority { get; set; } = AuthoringPriorities.Medium;

    public string State { get; set; } = ChecklistItemStateMachine.Active;

    public string Source { get; set; } = Contracts.AuthoringWireValues.ManualSource;

    public int Position { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int Version { get; set; }
}

/// <summary>
/// The write-model row of <c>public.manual_test_case</c> (13.1.1 §4): the reviewed-or-not steps of a manual test with
/// its project-prefixed <see cref="Code"/>. <see cref="ManualTestCaseStateMachine"/> owns its <c>draft → approved →
/// retired</c> moves and <see cref="AuthoringCode"/> its code.
/// </summary>
internal sealed class ManualTestCase
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid ChecklistItemId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Precondition { get; set; }

    /// <summary>The steps document (<c>{steps: [{no, action, expected, data?}]}</c>) as jsonb text.</summary>
    public string Steps { get; set; } = "{}";

    public string Priority { get; set; } = AuthoringPriorities.Medium;

    public string State { get; set; } = ManualTestCaseStateMachine.Draft;

    public string Source { get; set; } = Contracts.AuthoringWireValues.ManualSource;

    public DateTimeOffset? DeletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int Version { get; set; }
}

/// <summary>
/// The write-model row of <c>public.authoring_version</c> (13.1.1 §4): append-only, one row per changed entity per
/// command, the rows of one command sharing <see cref="BatchId"/>. Only
/// <c>AuthoringVersionWriter</c> inserts one, and the database refuses <c>UPDATE</c> for every role (AC6).
/// </summary>
internal sealed class AuthoringVersion
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid ProjectId { get; set; }

    public string EntityKind { get; set; } = Contracts.AuthoringWireValues.EntityKindName(Contracts.AuthoringEntityKind.FeatureNode);

    public Guid EntityId { get; set; }

    public int VersionNo { get; set; }

    public Guid BatchId { get; set; }

    public string ChangeKind { get; set; } = Contracts.AuthoringWireValues.CreateChangeKind;

    /// <summary>The full <c>content</c> snapshot as jsonb text (13.1.1 §4 line 207).</summary>
    public string Content { get; set; } = "{}";

    public string ContentHash { get; set; } = string.Empty;

    public string Source { get; set; } = Contracts.AuthoringWireValues.ManualSource;

    public Guid? AuthorMembershipId { get; set; }

    public string? Reason { get; set; }

    public Guid? AiRunId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The write-model row of <c>public.authoring_code_counter</c> (13.1.1 §4, AC4): one row per project, keyed
/// <c>(org_id, project_id)</c>. It carries no timestamps: the value is the whole state.
/// </summary>
internal sealed class AuthoringCodeCounter
{
    /// <summary>
    /// The surrogate id the schema requires (0039: the tenant-key rule of 00 §5.4.6); the key is
    /// <c>(org_id, project_id)</c>, so the id never identifies the counter.
    /// </summary>
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid ProjectId { get; set; }

    public long NextValue { get; set; } = 1;
}
