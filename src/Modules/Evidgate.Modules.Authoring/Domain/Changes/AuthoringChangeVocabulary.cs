namespace Evidgate.Modules.Authoring.Domain.Changes;

/// <summary>
/// The closed vocabulary of a queued authoring change: the four change types, the one target kind and the
/// structural subset that may not overlap another pending structural change.
/// </summary>
internal static class AuthoringChangeTypes
{
    /// <summary>A reviewer's note on the target item; it changes no content and writes no version.</summary>
    internal const string Comment = "comment";

    /// <summary>A soft delete of the target item.</summary>
    internal const string Delete = "delete";

    /// <summary>A merge of the payload's sources into the named survivor.</summary>
    internal const string Merge = "merge";

    /// <summary>A split of the target item into the payload's new items.</summary>
    internal const string Split = "split";

    /// <summary>The closed set the table's CHECK constraint mirrors.</summary>
    internal static IReadOnlyList<string> Values { get; } = [Comment, Delete, Merge, Split];

    /// <summary>True for one of the four values.</summary>
    /// <param name="changeType">The candidate value.</param>
    internal static bool IsValid(string? changeType) =>
        changeType is not null && Values.Contains(changeType, StringComparer.Ordinal);

    /// <summary>
    /// True for the three types that restructure the tree. Only these take part in the pending-overlap rule:
    /// comments may repeat and a delete never shares an item with another pending structural change.
    /// </summary>
    /// <param name="changeType">A validated change type.</param>
    internal static bool IsStructural(string changeType) => changeType is Delete or Merge or Split;
}

/// <summary>The one target kind an authoring change can name today.</summary>
internal static class AuthoringChangeTargetKind
{
    /// <summary>The target is a <c>checklist_item</c> row of the same project.</summary>
    internal const string ChecklistItem = "checklist_item";

    /// <summary>True for the one supported kind.</summary>
    /// <param name="kind">The candidate value.</param>
    internal static bool IsValid(string? kind) =>
        string.Equals(kind, ChecklistItem, StringComparison.Ordinal);
}

/// <summary>
/// The bounds the queue route and its table CHECKs share: the payload caps the plan names, the pending limit and
/// the page/selection sizes.
/// </summary>
internal static class AuthoringChangeLimits
{
    /// <summary>A comment body has at least one character.</summary>
    internal const int CommentBodyMin = 1;

    /// <summary>A comment body has at most 2 000 characters, the table CHECK's own bound.</summary>
    internal const int CommentBodyMax = 2000;

    /// <summary>A merge names at least two items, including the survivor.</summary>
    internal const int MergeSourcesMin = 2;

    /// <summary>A merge names at most 50 items.</summary>
    internal const int MergeSourcesMax = 50;

    /// <summary>A split creates at least two new items.</summary>
    internal const int SplitItemsMin = 2;

    /// <summary>A split creates at most 20 new items.</summary>
    internal const int SplitItemsMax = 20;

    /// <summary>
    /// A split's <c>case_assignments</c> names at most 200 cases: an item's live case list is unbounded, and one array
    /// entry per case has to stay inside the payload's jsonb size cap rather than rely on the column CHECK to refuse it.
    /// </summary>
    internal const int CaseAssignmentsMax = 200;

    /// <summary>The payload object cap the table CHECK also enforces (bytes of its JSON text).</summary>
    internal const int PayloadMaxBytes = 32768;

    /// <summary>A project may hold at most this many pending changes.</summary>
    internal const int PendingMax = 500;

    /// <summary>One apply names at most this many changes.</summary>
    internal const int SelectionMax = 200;

    /// <summary>The list route's default page size.</summary>
    internal const int PageDefault = 50;

    /// <summary>The list route's largest page.</summary>
    internal const int PageMax = 200;

    /// <summary>A change's reason is at most 500 characters, the table CHECK's own bound.</summary>
    internal const int ReasonMax = 500;

    /// <summary>A checklist item title is at most 500 characters, as on the item routes.</summary>
    internal const int ItemTitleMax = 500;

    /// <summary>A checklist item description is at most 4 000 characters.</summary>
    internal const int ItemDescriptionMax = 4000;
}
