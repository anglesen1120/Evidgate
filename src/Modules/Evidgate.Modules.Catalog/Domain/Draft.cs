namespace Evidgate.Modules.Catalog.Domain;

/// <summary>The closed list of <c>draft.kind</c> values.</summary>
internal static class DraftKind
{
    /// <summary>Cut from a template the project stores.</summary>
    public const string Template = "template";

    /// <summary>Authored by an AI run.</summary>
    public const string Ai = "ai";

    /// <summary>Cut from a regression report.</summary>
    public const string Regression = "regression";

    /// <summary>Every accepted value, in the contract's order.</summary>
    public static readonly IReadOnlyList<string> All = [Template, Ai, Regression];
}

/// <summary>
/// The <c>draft</c> state machine: content is edited only in <see cref="Draft"/>, dry runs follow the sandbox result,
/// and a PR ends at <see cref="Merged"/> or <see cref="Closed"/>. The database repeats the transition list in
/// <c>draft_guard</c>, because the app role can update <c>status</c> directly.
/// </summary>
internal static class DraftStatus
{
    /// <summary>Being edited; the only state whose content may change.</summary>
    public const string Draft = "draft";

    /// <summary>A dry run is queued or running.</summary>
    public const string DryRunning = "dry_running";

    /// <summary>The dry run passed; a PR may be opened.</summary>
    public const string DryPassed = "dry_passed";

    /// <summary>The dry run failed; a re-run or an unverified PR may follow.</summary>
    public const string DryFailed = "dry_failed";

    /// <summary>A PR is open.</summary>
    public const string PrOpen = "pr_open";

    /// <summary>The PR merged and the catalog link was recorded.</summary>
    public const string Merged = "merged";

    /// <summary>The PR closed without merging.</summary>
    public const string Closed = "closed";

    /// <summary>Every accepted value, in the contract's order.</summary>
    public static readonly IReadOnlyList<string> All =
        [Draft, DryRunning, DryPassed, DryFailed, PrOpen, Merged, Closed];
}

/// <summary>
/// The write-model row of <c>public.draft</c> (migration 0117). Identity columns are assigned once and the
/// <c>draft_guard</c> trigger refuses any later change to them, as it refuses a content edit outside
/// <see cref="DraftStatus.Draft"/>, a transition outside the state machine, and an insert that does not start
/// at <see cref="DraftStatus.Draft"/> version 1.
/// </summary>
internal sealed class Draft
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid AuthorMembershipId { get; set; }

    public string Kind { get; set; } = DraftKind.Template;

    public string BaseCommit { get; set; } = string.Empty;

    public string Status { get; set; } = DraftStatus.Draft;

    public int Version { get; set; } = 1;

    /// <summary>The content set as JSON text; typed as text so the row round-trips byte for byte.</summary>
    public string Content { get; set; } = "{}";

    /// <summary>Origin metadata as JSON text, or null.</summary>
    public string? Provenance { get; set; }

    public Guid? AiRunId { get; set; }

    public Guid? DryRunId { get; set; }

    public bool AllowUnverified { get; set; }

    public string? UnverifiedReason { get; set; }

    public string? PrBranch { get; set; }

    public string? PrCommitSha { get; set; }

    public string? PrUrl { get; set; }

    public int? PrNumber { get; set; }

    public Guid? LinkedCaseId { get; set; }

    public Guid? LinkedRevisionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Replaces the content set, refusing a draft that has left <see cref="DraftStatus.Draft"/>. A failed save is a
    /// bug in the caller, not a client error: the handler answers the state conflict before it gets here.
    /// </summary>
    /// <param name="contentJson">The validated content as JSON text.</param>
    public void ApplyContent(string contentJson)
    {
        if (Status != DraftStatus.Draft)
        {
            throw new InvalidOperationException($"a {Status} draft no longer accepts content edits");
        }

        Content = contentJson;
    }
}
