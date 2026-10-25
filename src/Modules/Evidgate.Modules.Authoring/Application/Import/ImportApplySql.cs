using Evidgate.Modules.Authoring.Contracts;

namespace Evidgate.Modules.Authoring.Application.Import;

/// <summary>
/// The set-based statements of an import's apply phase (13.1.1 AC13, plan P2: "one batched insert per entity kind, never
/// a per-row loop"). Every write is one statement for <b>all</b> rows of a kind, so an import of 50 rows and an import of
/// 5 000 rows cost the same number of round trips.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the import does not call <c>AppendAsync</c> per change.</b> The writer's per-change path is right for the
/// routes — one entity, one command, its own probes — and it is deliberately <em>not</em> batched: each change is four or
/// five statements (probe, sibling check, row write, version read). A 5 000-row file through it would be ~20 000 round
/// trips inside one transaction. The import therefore writes the entity rows itself, in one statement per kind, and
/// still appends its version rows through the writer's own <see cref="Infrastructure.Persistence.AuthoringSql.AppendVersions"/>
/// statement — so the log it produces is byte-identical in shape to the writer's (one row per changed entity, one shared
/// <c>batch_id</c>, <c>source = import</c>).
/// </para>
/// <para>
/// <b>Locks.</b> Three statements, one per kind that has a non-create row, lock exactly the rows plan 2 resolved, in
/// ascending id (the writer's rank-then-id order, one rank per statement), and return the <c>version</c> the caller must
/// still find: a version that moved since the read means another writer changed a row this plan matched, and the apply
/// refuses with the retryable <c>503</c> instead of writing a version number that is already taken. A fourth statement
/// takes the sibling-group advisory locks of every group the file adds a node or item to, in one call and in key order —
/// the same lock (<c>AuthoringLockGuard.SiblingGroupKey</c>) the create, move and reorder paths take, so an import cannot
/// meet a parked position of a concurrent renumber.
/// </para>
/// <para>
/// <b>Positions and states.</b> A create inserts its resolved position; a restore clears <c>deleted_at</c> and writes §6's
/// restore state; an update writes the whole snapshot. The <c>version</c> column is never written: the
/// <c>set_updated_at_and_version</c> trigger owns it, and the version rows the import appends are numbered from the lock
/// read (<c>version + 1</c>, and <c>version + 2</c> for the rare restore-then-update row).
/// </para>
/// </remarks>
internal static class ImportApplySql
{
    /// <summary>
    /// Takes the advisory locks of every sibling group this file adds to, in one statement and in key order (a sorted
    /// set, so two imports of overlapping group sets can never take them in opposite orders).
    /// </summary>
    internal const string LockSiblingGroups = """
        select pg_advisory_xact_lock(hashtextextended(g.key, 0))
          from (select unnest(@group_keys::text[]) as key) g
         order by g.key
        """;

    /// <summary>The row locks of the entities this file updates or restores, one statement per kind (rank order).</summary>
    /// <param name="kind">The entity kind.</param>
    internal static string LockEntities(AuthoringEntityKind kind) => kind switch
    {
        AuthoringEntityKind.FeatureNode => """
            select n.id, n.version
              from public.feature_node n
             where n.org_id = @org_id and n.project_id = @project_id and n.id = any(@ids)
             order by n.id
               for update
            """,
        AuthoringEntityKind.ChecklistItem => """
            select i.id, i.version, i.deleted_at
              from public.checklist_item i
             where i.org_id = @org_id and i.project_id = @project_id and i.id = any(@ids)
             order by i.id
               for update
            """,
        AuthoringEntityKind.ManualTestCase => """
            select m.id, m.version, m.deleted_at
              from public.manual_test_case m
             where m.org_id = @org_id and m.project_id = @project_id and m.id = any(@ids)
             order by m.id
               for update
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown authoring entity kind"),
    };

    /// <summary>The creates of one kind, one statement for all of them (the entity rows of the import).</summary>
    /// <param name="kind">The entity kind.</param>
    internal static string InsertEntities(AuthoringEntityKind kind) => kind switch
    {
        AuthoringEntityKind.FeatureNode => """
            insert into public.feature_node (id, org_id, project_id, parent_id, kind, title, position)
            select r.id, @org_id, @project_id, r.parent_id, r.kind, r.title, r.position
              from jsonb_to_recordset(@rows::jsonb)
                   as r(id uuid, parent_id uuid, kind text, title text, position integer)
            """,
        AuthoringEntityKind.ChecklistItem => """
            insert into public.checklist_item
                   (id, org_id, project_id, feature_node_id, title, description, priority, state, source, position)
            select r.id, @org_id, @project_id, r.feature_node_id, r.title, r.description, r.priority, r.state, @source,
                   r.position
              from jsonb_to_recordset(@rows::jsonb)
                   as r(id uuid, feature_node_id uuid, title text, description text, priority text, state text,
                        position integer)
            """,
        AuthoringEntityKind.ManualTestCase => """
            insert into public.manual_test_case
                   (id, org_id, project_id, checklist_item_id, code, title, description, precondition, steps, priority,
                    state, source)
            select r.id, @org_id, @project_id, r.checklist_item_id, r.code, r.title, r.description, r.precondition,
                   r.steps::jsonb, r.priority, r.state, @source
              from jsonb_to_recordset(@rows::jsonb)
                   as r(id uuid, checklist_item_id uuid, code text, title text, description text, precondition text,
                        steps text, priority text, state text)
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown authoring entity kind"),
    };

    /// <summary>The updates of one kind, one statement for all of them.</summary>
    /// <param name="kind">The entity kind.</param>
    /// <summary>
    /// The update statement of an entity kind. A feature node has none: its title, kind and parent are exactly its
    /// identity, and a file that names another position is route 4's reorder — so a matched node is always
    /// <c>unchanged</c> and a node row is only ever inserted (13.1.1 §4, AC15).
    /// </summary>
    /// <param name="kind">The entity kind.</param>
    internal static string UpdateEntities(AuthoringEntityKind kind) => kind switch
    {
        AuthoringEntityKind.ChecklistItem => """
            update public.checklist_item i
               set title = r.title, description = r.description, priority = r.priority, state = r.state,
                   position = r.position
              from jsonb_to_recordset(@rows::jsonb)
                   as r(id uuid, title text, description text, priority text, state text, position integer)
             where i.org_id = @org_id and i.project_id = @project_id and i.deleted_at is null and i.id = r.id
            """,
        AuthoringEntityKind.ManualTestCase => """
            update public.manual_test_case m
               set checklist_item_id = r.checklist_item_id, title = r.title, description = r.description,
                   precondition = r.precondition, steps = r.steps::jsonb, priority = r.priority, state = r.state
              from jsonb_to_recordset(@rows::jsonb)
                   as r(id uuid, checklist_item_id uuid, title text, description text, precondition text, steps text,
                        priority text, state text)
             where m.org_id = @org_id and m.project_id = @project_id and m.deleted_at is null and m.id = r.id
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown authoring entity kind"),
    };

    /// <summary>
    /// The restores of the soft-deleted cases a file brings back, one statement for all of them: <c>deleted_at</c>
    /// cleared <b>and</b> the file's content written (AC8), so the row that comes back is exactly the row the file
    /// describes and the next identical import matches it as <c>unchanged</c> (AC15). One row of
    /// <c>authoring_version</c> with <c>change_kind = restore</c> records it: the entity's <c>version</c> is
    /// trigger-owned and bumps once, so a restore and a separate update of one row could not both be numbered.
    /// </summary>
    internal const string RestoreCases = """
        update public.manual_test_case m
           set deleted_at = null, checklist_item_id = r.checklist_item_id, title = r.title,
               description = r.description, precondition = r.precondition, steps = r.steps::jsonb,
               priority = r.priority, state = r.state
          from jsonb_to_recordset(@rows::jsonb)
               as r(id uuid, checklist_item_id uuid, title text, description text, precondition text, steps text,
                    priority text, state text)
         where m.org_id = @org_id and m.project_id = @project_id and m.deleted_at is not null and m.id = r.id
        """;

    /// <summary>One locked row of the entities the apply mutates.</summary>
    internal sealed class LockedRow
    {
        /// <summary>The row's id.</summary>
        public Guid Id { get; set; }

        /// <summary>The row's <c>version</c> as the lock read it.</summary>
        public int Version { get; set; }

        /// <summary>The soft-delete stamp (cases and items only; null for a node read).</summary>
        public DateTimeOffset? DeletedAt { get; set; }
    }
}
