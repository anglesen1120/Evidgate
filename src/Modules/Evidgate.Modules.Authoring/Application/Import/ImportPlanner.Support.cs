using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;

namespace Evidgate.Modules.Authoring.Application.Import;

internal sealed partial class ImportPlanner
{
    /// <summary>
    /// True when a case row would write content under a checklist item that is <em>stored</em> in its terminal state. The
    /// writer refuses that placement — <see cref="AuthoringVersionWriter.CheckCaseParentAsync"/>: "a retired checklist
    /// item takes no new content; restore it first" — and an import must not be the way around it. A row that writes
    /// nothing (an unchanged row of a round trip) is not a placement and stays accepted, and an item this same file
    /// creates as retired is not a stored one: refusing that would break the round trip of a live-but-retired item and
    /// its cases, which is exactly what the exporter emits (AC14/AC15).
    /// </summary>
    /// <param name="row">The planned row.</param>
    /// <param name="resolvedItems">The item each file key resolved to, with its state and whether it is stored.</param>
    /// <param name="outcome">The outcome the row is heading for.</param>
    private static bool WritesUnderRetiredItem(
        ImportPlannedRow row,
        Dictionary<string, ResolvedItem> resolvedItems,
        ImportOutcome outcome) =>
        outcome != ImportOutcome.Unchanged
        && row.Row.Kind == ImportRowKind.Case
        && row.Row.ItemKey is { } itemKey
        && resolvedItems.TryGetValue(itemKey, out var item)
        && item.Stored
        && string.Equals(item.State, ChecklistItemStateMachine.Retired, StringComparison.Ordinal);

    /// <summary>Refuses a case row whose item is retired, on the column that names the item.</summary>
    /// <param name="issues">The refusals.</param>
    /// <param name="row">The refused row.</param>
    private static void RefuseRetiredItem(List<ImportIssue> issues, ImportPlannedRow row) =>
        Issue(issues, row.Row.Source.Index, "/item_key", "value_invalid");

    /// <summary>
    /// The state a matched row writes (13.1.1 §6, AC13): the stored one when the file's <c>state</c> cell was empty, the
    /// file's own when the entity's state machine allows exactly that move, and <c>null</c> — with the issue counted —
    /// when it does not. The import carries no reason, so a move into <c>retired</c> (which needs one) is refused here
    /// too: an import may not be the path that retires a row without a reason.
    /// </summary>
    /// <param name="row">The matched row.</param>
    /// <param name="stored">The state the entity is stored in.</param>
    /// <param name="decide">The entity's own <c>Decide</c>, with the reason bound to null.</param>
    /// <param name="issues">The refusals.</param>
    internal static string? MatchedState(
        ImportValidatedRow row,
        string stored,
        Func<string, string, AuthoringTransition> decide,
        List<ImportIssue> issues)
    {
        if (!row.StateGiven)
        {
            // An export that leaves the state column empty describes the stored state, never the creation default: this
            // is what keeps an import from resetting an approved case to draft (Perf/security F2, findings A/F4).
            return stored;
        }

        var transition = decide(stored, row.State);

        switch (transition.Outcome)
        {
            case AuthoringTransitionOutcome.Allowed:
            case AuthoringTransitionOutcome.Unchanged:
                return transition.ToState;

            default:
                // A move outside §6's table, a move out of `retired`, and a move into `retired` without a reason are one
                // refusal on the state column in the import's vocabulary.
                Issue(issues, row.Source.Index, "/state", "value_invalid");
                return null;
        }
    }

    /// <summary>True when a matched item or case is stored in its terminal state, which no update path may edit.</summary>
    /// <param name="row">The planned row.</param>
    private static bool IsRetired(ImportPlannedRow row) =>
        row.Row.Kind != ImportRowKind.Node
        && row.StoredState is { } state
        && string.Equals(
            state,
            row.Row.Kind == ImportRowKind.Case
                ? ManualTestCaseStateMachine.Retired
                : ChecklistItemStateMachine.Retired,
            StringComparison.Ordinal);

    /// <summary>The stored snapshot of a soft-deleted case (its state is §6's restore state, draft): the marker that makes
    /// the row a restore, and what the apply's lock read proves is still deleted when it runs.</summary>
    /// <param name="stored">The soft-deleted case.</param>
    private static ManualTestCaseSnapshot Restored(ImportSql.CaseRow stored) => new(
        stored.ChecklistItemId,
        stored.Code,
        stored.Title,
        stored.Description,
        stored.Precondition,
        stored.Steps,
        stored.Priority,
        ManualTestCaseStateMachine.RestoreState);

    /// <summary>The stored form of a case code: the number is formatted with its minimum digits, so a file's <c>SHOP-0007</c>
    /// matches the stored <c>SHOP-007</c>.</summary>
    /// <param name="code">The code as the file wrote it.</param>
    private static string CanonicalCode(string code) =>
        AuthoringCode.TryParse(code, out var parsed)
            ? parsed.Value
            : throw new InvalidOperationException("the validator accepted a code outside the grammar");

    /// <summary>The entity kind of a row kind.</summary>
    /// <param name="kind">The row kind.</param>
    private static AuthoringEntityKind KindOf(ImportRowKind kind) => kind switch
    {
        ImportRowKind.Node => AuthoringEntityKind.FeatureNode,
        ImportRowKind.Item => AuthoringEntityKind.ChecklistItem,
        ImportRowKind.Case => AuthoringEntityKind.ManualTestCase,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown import row kind"),
    };

    /// <summary>The position state of one sibling group, live members and the rows this file adds.</summary>
    /// <param name="groups">The groups seen so far, keyed by parent id.</param>
    /// <param name="parent">The parent, or null for the project's root group.</param>
    /// <remarks>
    /// The key is a plain <see cref="Guid"/> because a nullable key is a nullable-struct key to the dictionary contract
    /// (CS8714, and a warning here is an error): the root group is <see cref="Guid.Empty"/>, which no entity id is
    /// (<c>Guid.CreateVersion7</c> never yields it) and which no row can name as a parent.
    /// </remarks>
    private static ImportGroup Group(Dictionary<Guid, ImportGroup> groups, Guid? parent)
    {
        var key = parent ?? Guid.Empty;

        if (!groups.TryGetValue(key, out var group))
        {
            group = new ImportGroup();
            groups[key] = group;
        }

        return group;
    }

    /// <summary>The groups this file adds a row to, as the apply re-reads them under the advisory lock (finding F).</summary>
    /// <param name="kind">The entity kind of the group.</param>
    /// <param name="groups">The groups of one phase, keyed by parent id.</param>
    /// <remarks>
    /// A group the file only reads (it adds nothing to it) carries no assumption the apply could invalidate, so it is not
    /// an expectation: the apply locks and re-reads exactly what a file writes into.
    /// </remarks>
    private static IEnumerable<ImportGroupExpectation> Expectations(
        ImportRowKind kind,
        Dictionary<Guid, ImportGroup> groups) =>
        groups
            .Where(entry => entry.Value.AddedCount != 0)
            .OrderBy(entry => entry.Key)
            .Select(entry => new ImportGroupExpectation(kind, entry.Key, entry.Value.LiveCount, entry.Value.LiveMaximum));

    /// <summary>Counts one issue, bounded like the validation phase.</summary>
    /// <param name="issues">The refusals.</param>
    /// <param name="index">The row's file position.</param>
    /// <param name="path">The fixed column pointer.</param>
    /// <param name="reason">One of the AC13 reasons.</param>
    private static void Issue(List<ImportIssue> issues, int index, string path, string reason)
    {
        if (issues.Count < ImportValidator.MaximumIssues)
        {
            issues.Add(new ImportIssue(index, path, reason));
        }
    }

    /// <summary>The decided rows and the issues of the hash pass.</summary>
    /// <param name="Rows">One row per validated row, in file order.</param>
    /// <param name="Issues">Always empty today; kept so the caller has one shape to answer with.</param>
    private sealed record PlanOutcome(IReadOnlyList<ImportPlannedRow> Rows, IReadOnlyList<ImportIssue> Issues);

    /// <summary>
    /// One sibling group's positions: the live members read from the database and the rows this file adds, so an append
    /// lands at <c>max + 1</c> and an explicit position that is already held is refused instead of colliding in the
    /// unique index of AC2.
    /// </summary>
    private sealed class ImportGroup
    {
        private readonly HashSet<int> taken = [];

        /// <summary>The number of live members of the group.</summary>
        public int LiveCount { get; private set; }

        /// <summary>The number of rows this file adds to the group.</summary>
        public int AddedCount { get; private set; }

        /// <summary>
        /// The highest position among the group's <b>live</b> members, or −1 for an empty group: what the apply must still
        /// find when it re-reads the group under the advisory lock (finding F). It is deliberately separate from
        /// <see cref="Maximum"/>: the assignment cursor moves as this file's own rows take positions, and comparing that
        /// against the live state would make every importing append look like a change by another request.
        /// </summary>
        public int LiveMaximum { get; private set; } = -1;

        /// <summary>The highest position the group holds, live or added by this file: the append cursor.</summary>
        public int Maximum { get; private set; } = -1;

        /// <summary>Records one live member.</summary>
        /// <param name="position">Its stored position.</param>
        public void Live(int position)
        {
            LiveCount++;
            taken.Add(position);
            Maximum = Math.Max(Maximum, position);
            LiveMaximum = Math.Max(LiveMaximum, position);
        }

        /// <summary>Assigns the position of a row this file adds: the named one when it is free, else the append position.</summary>
        /// <param name="position">The position the row named, or null.</param>
        /// <param name="assigned">The assigned position.</param>
        public bool Next(int? position, out int assigned)
        {
            if (position is { } named)
            {
                if (!taken.Add(named))
                {
                    assigned = named;
                    return false;
                }

                Maximum = Math.Max(Maximum, named);
                assigned = named;
                AddedCount++;
                return true;
            }

            assigned = Maximum + 1;
            taken.Add(assigned);
            Maximum = assigned;
            AddedCount++;
            return true;
        }
    }
}
