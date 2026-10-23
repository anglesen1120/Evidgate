namespace Evidgate.Modules.Authoring.Domain;

/// <summary>
/// The kind ladder of the feature tree (13.1.1 AC1, §4): <c>module</c> at the root, then <c>feature</c>,
/// <c>category</c> and <c>sub_category</c>, each kind under exactly one parent kind and nothing under a
/// <c>sub_category</c>. Cycle freedom is structural — the chain strictly decreases in depth — and the defensive
/// <see cref="PlacementReason.Cycle"/> branch is kept for the day the ladder changes (§4 [DECISION]).
/// </summary>
internal static class FeatureNodeKinds
{
    /// <summary>The root kind; the only one whose <c>parent_id</c> may be null.</summary>
    public const string Module = "module";

    /// <summary>Under a module.</summary>
    public const string Feature = "feature";

    /// <summary>Under a feature; a checklist item may attach to it.</summary>
    public const string Category = "category";

    /// <summary>The deepest kind; a checklist item may attach to it.</summary>
    public const string SubCategory = "sub_category";

    /// <summary>The closed kind set, root first.</summary>
    public static IReadOnlyList<string> Values { get; } = [Module, Feature, Category, SubCategory];

    /// <summary>The two kinds a checklist item may attach to (AC3).</summary>
    public static IReadOnlyList<string> ItemParentKinds { get; } = [Category, SubCategory];

    /// <summary>True when <paramref name="kind"/> is one of the four kinds.</summary>
    /// <param name="kind">The candidate.</param>
    public static bool IsValid(string? kind) => kind is not null && Values.Contains(kind, StringComparer.Ordinal);

    /// <summary>The depth of a kind: <c>module</c> 0 … <c>sub_category</c> 3.</summary>
    /// <param name="kind">A valid kind.</param>
    public static int Depth(string kind) => kind switch
    {
        Module => 0,
        Feature => 1,
        Category => 2,
        SubCategory => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown feature node kind"),
    };

    /// <summary>The deepest kind; a child of it would be the fifth level AC1 refuses.</summary>
    public static int MaximumDepth => Depth(SubCategory);

    /// <summary>The one kind a child of <paramref name="kind"/> may be, or null for the deepest kind.</summary>
    /// <param name="kind">The parent kind.</param>
    public static string? ChildKind(string kind) => kind switch
    {
        Module => Feature,
        Feature => Category,
        Category => SubCategory,
        _ => null,
    };
}

/// <summary>The stable <c>details[].reason</c> values of <c>authoring_parent_invalid</c> (AC1, AC8).</summary>
internal static class PlacementReason
{
    /// <summary>No node with that id is visible in this project (or it belongs to another project, AC10).</summary>
    public const string ParentNotFound = "parent_not_found";

    /// <summary>The parent exists but its kind is not the one the ladder requires (or a non-module names no parent).</summary>
    public const string ParentKindInvalid = "parent_kind_invalid";

    /// <summary>The parent exists but is soft-deleted; restore the parent first (AC8).</summary>
    public const string ParentDeleted = "parent_deleted";

    /// <summary>The placement would be the fifth level, or a child of a <c>sub_category</c>.</summary>
    public const string DepthExceeded = "depth_exceeded";

    /// <summary>A re-parent would make the node its own ancestor (the structural ladder keeps this unreachable).</summary>
    public const string Cycle = "cycle";
}

/// <summary>What the first node of a feature tree has to know about its parent.</summary>
/// <param name="Id">The parent's id.</param>
/// <param name="Kind">The parent's kind.</param>
/// <param name="Deleted">True when the parent is soft-deleted.</param>
/// <param name="SameProject">True when the parent belongs to the project the child is being written to.</param>
internal sealed record FeatureNodeParent(Guid Id, string Kind, bool Deleted, bool SameProject);

/// <summary>The verdict of a placement check: no reason means the placement is legal.</summary>
/// <param name="Reason">One of <see cref="PlacementReason"/>, or null.</param>
internal sealed record FeatureNodePlacement(string? Reason)
{
    /// <summary>True when nothing refuses the placement.</summary>
    public bool IsAccepted => Reason is null;

    /// <summary>The accepted placement.</summary>
    public static FeatureNodePlacement Accepted { get; } = new((string?)null);

    /// <summary>A refused placement with its reason.</summary>
    /// <param name="reason">One of <see cref="PlacementReason"/>.</param>
    public static FeatureNodePlacement Refused(string reason) => new(reason);
}

/// <summary>
/// The one validator of the ladder (AC1, §4 [DECISION]): every write path that places a node — route 2's create,
/// route 3's re-parent, route 19's move target lookup and the import — calls it, and the database keeps
/// <c>parent_id &lt;&gt; id</c> plus the same-project composite foreign key as the second layer.
/// </summary>
internal static class FeatureNodeRules
{
    /// <summary>
    /// Decides whether <paramref name="kind"/> may be placed under <paramref name="parent"/>. The order is the one
    /// AC1 answers with: absence and deletion first (they also cover a parent in another project, which must be
    /// indistinguishable from absence, AC10), then the fifth level, then the kind ladder.
    /// </summary>
    /// <param name="kind">The kind of the node being placed.</param>
    /// <param name="parent">What the caller found for <c>parent_id</c>, or null when it named none.</param>
    public static FeatureNodePlacement CheckPlacement(string kind, FeatureNodeParent? parent)
    {
        if (!FeatureNodeKinds.IsValid(kind))
        {
            throw new ArgumentException($"'{kind}' is not a feature node kind", nameof(kind));
        }

        if (parent is null)
        {
            // A module is the root kind; every other kind needs a parent, and a caller that named none
            // named a kind that cannot stand at the root.
            return string.Equals(kind, FeatureNodeKinds.Module, StringComparison.Ordinal)
                ? FeatureNodePlacement.Accepted
                : FeatureNodePlacement.Refused(PlacementReason.ParentKindInvalid);
        }

        if (!parent.SameProject)
        {
            return FeatureNodePlacement.Refused(PlacementReason.ParentNotFound);
        }

        if (parent.Deleted)
        {
            return FeatureNodePlacement.Refused(PlacementReason.ParentDeleted);
        }

        if (!FeatureNodeKinds.IsValid(parent.Kind))
        {
            throw new ArgumentException($"'{parent.Kind}' is not a feature node kind", nameof(parent));
        }

        if (FeatureNodeKinds.Depth(parent.Kind) >= FeatureNodeKinds.MaximumDepth)
        {
            // The fifth level: nothing may hang under a sub_category, whatever its kind.
            return FeatureNodePlacement.Refused(PlacementReason.DepthExceeded);
        }

        return string.Equals(FeatureNodeKinds.ChildKind(parent.Kind), kind, StringComparison.Ordinal)
            ? FeatureNodePlacement.Accepted
            : FeatureNodePlacement.Refused(PlacementReason.ParentKindInvalid);
    }

    /// <summary>
    /// The self and ancestor guard of a re-parent (AC1): a node is never its own parent, and the ladder makes a
    /// longer ancestor cycle impossible, so <paramref name="ancestorIds"/> is the defensive half of the branch.
    /// </summary>
    /// <param name="nodeId">The node being moved.</param>
    /// <param name="newParentId">The parent it is moved under, or null for the root.</param>
    /// <param name="ancestorIds">The ids of the new parent and its ancestors, as the caller read them.</param>
    public static FeatureNodePlacement CheckReparent(Guid nodeId, Guid? newParentId, IReadOnlyList<Guid> ancestorIds)
    {
        ArgumentNullException.ThrowIfNull(ancestorIds);

        if (newParentId is not { } parent)
        {
            return FeatureNodePlacement.Accepted;
        }

        return parent == nodeId || ancestorIds.Contains(nodeId)
            ? FeatureNodePlacement.Refused(PlacementReason.Cycle)
            : FeatureNodePlacement.Accepted;
    }

    /// <summary>
    /// The sibling-position rule of AC2: a position is a non-negative integer, two live siblings never share one
    /// (the partial unique index enforces it), and a new or restored row lands at <c>max + 1</c>.
    /// </summary>
    /// <param name="position">The candidate position.</param>
    public static bool IsValidPosition(int position) => position >= 0;

    /// <summary>The position after every live sibling's (AC2's <c>max + 1</c>, also the restore rule of AC8).</summary>
    /// <param name="liveSiblingPositions">The live siblings' positions; empty for the first child.</param>
    public static int NextPosition(IEnumerable<int> liveSiblingPositions)
    {
        ArgumentNullException.ThrowIfNull(liveSiblingPositions);

        var maximum = -1;
        foreach (var position in liveSiblingPositions)
        {
            maximum = Math.Max(maximum, position);
        }

        return maximum + 1;
    }
}
