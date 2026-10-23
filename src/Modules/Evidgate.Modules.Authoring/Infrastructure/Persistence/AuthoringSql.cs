using Evidgate.Modules.Authoring.Contracts;

namespace Evidgate.Modules.Authoring.Infrastructure.Persistence;

/// <summary>
/// Every statement of <c>AuthoringVersionWriter</c> and of the code allocator, one constant each, so the SQL of the
/// authoring write path is reviewable in one file instead of scattered through the writer (03.2.1's
/// <c>SuiteReadSql</c>/<c>TestRevisionSql</c> pattern). Parameters are always bound, never interpolated (00 §5.7 S1).
/// </summary>
internal static class AuthoringSql
{
    /// <summary>
    /// The row lock of a mutation: the entity row <c>for update</c> — the lock that serialises two writers of one
    /// entity, so <c>version_no</c> never collides and the unique key of <c>authoring_version</c> never surfaces as a
    /// 23505.
    /// </summary>
    /// <remarks>
    /// The last version's number and hash are read by <see cref="ReadLastVersion"/> in a separate statement
    /// <em>after</em> this lock, never through a lateral join here: a lateral is evaluated in the statement's own
    /// snapshot, so a writer that waited on another writer's lock would re-check the row under READ COMMITTED against
    /// the new row version while the lateral still answered the pre-wait <c>last_version_no</c>, and the out-of-step
    /// guard would refuse a perfectly serialised write.
    /// </remarks>
    /// <param name="kind">The entity kind.</param>
    /// <param name="shared">
    /// True for a parent-only probe: <c>for share</c> still blocks a concurrent soft delete of the parent, which is the
    /// whole purpose of the probe, without serialising every unrelated child of that parent behind one update lock.
    /// </param>
    internal static string LockEntity(AuthoringEntityKind kind, bool shared = false) =>
        EntitySelect(kind, LockClause(kind, shared ? "share" : "update"));

    /// <summary>
    /// The same row read without any lock: the peek a restore needs to learn its parent id before it takes the
    /// parent lock, in the writer-wide lock order (parent before child).
    /// </summary>
    /// <param name="kind">The entity kind.</param>
    internal static string PeekEntity(AuthoringEntityKind kind) => EntitySelect(kind, null);

    /// <summary>
    /// The last version row of one entity (the no-op rule of AC5 and the out-of-step guard). Always a statement of its
    /// own, after the entity lock, so it sees everything the lock holder committed.
    /// </summary>
    internal const string ReadLastVersion = """
        select version_no, content_hash
          from public.authoring_version
         where org_id = @org_id
           and project_id = @project_id
           and entity_kind = @entity_kind
           and entity_id = @id
         order by version_no desc
         limit 1
        """;

    /// <summary>The lock clause of <see cref="LockEntity"/>: the alias of the locked table is the row's own.</summary>
    /// <param name="kind">The entity kind.</param>
    /// <param name="strength"><c>update</c> or <c>share</c>.</param>
    private static string LockClause(AuthoringEntityKind kind, string strength) => kind switch
    {
        AuthoringEntityKind.FeatureNode => "for " + strength + " of n",
        AuthoringEntityKind.ChecklistItem => "for " + strength + " of i",
        AuthoringEntityKind.ManualTestCase => "for " + strength + " of m",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown authoring entity kind"),
    };

    /// <summary>One entity row's read, with an optional lock clause appended after the <c>where</c>.</summary>
    /// <param name="kind">The entity kind.</param>
    /// <param name="lockClause">The lock clause, or null for an unlocked read.</param>
    private static string EntitySelect(AuthoringEntityKind kind, string? lockClause)
    {
        var suffix = lockClause is null ? string.Empty : "\n   " + lockClause;

        return kind switch
        {
            AuthoringEntityKind.FeatureNode => $"""
            select n.id, n.parent_id, n.kind, n.title, n.position, n.deleted_at, n.version
              from public.feature_node n
             where n.org_id = @org_id and n.project_id = @project_id and n.id = @id
            {suffix}
            """,
            AuthoringEntityKind.ChecklistItem => $"""
            select i.id, i.feature_node_id, i.title, i.description, i.priority, i.state, i.source, i.position,
                   i.deleted_at, i.version
              from public.checklist_item i
             where i.org_id = @org_id and i.project_id = @project_id and i.id = @id
            {suffix}
            """,
            AuthoringEntityKind.ManualTestCase => $"""
            select m.id, m.checklist_item_id, m.code, m.title, m.description, m.precondition, m.steps::text as steps,
                   m.priority, m.state, m.source, m.deleted_at, m.version
              from public.manual_test_case m
             where m.org_id = @org_id and m.project_id = @project_id and m.id = @id
            {suffix}
            """,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown authoring entity kind"),
        };
    }

    /// <summary>The insert of a created entity; returns the row's <c>version</c> (1, by the column default).</summary>
    /// <param name="kind">The entity kind.</param>
    internal static string InsertEntity(AuthoringEntityKind kind) => kind switch
    {
        AuthoringEntityKind.FeatureNode => """
            insert into public.feature_node (id, org_id, project_id, parent_id, kind, title, position)
            values (@id, @org_id, @project_id, @parent_id, @kind, @title, @position)
            returning version
            """,
        AuthoringEntityKind.ChecklistItem => """
            insert into public.checklist_item
                   (id, org_id, project_id, feature_node_id, title, description, priority, state, source, position)
            values (@id, @org_id, @project_id, @feature_node_id, @title, @description, @priority, @state, @source, @position)
            returning version
            """,
        AuthoringEntityKind.ManualTestCase => """
            insert into public.manual_test_case
                   (id, org_id, project_id, checklist_item_id, code, title, description, precondition, steps, priority, state, source)
            values (@id, @org_id, @project_id, @checklist_item_id, @code, @title, @description, @precondition,
                    @steps::jsonb, @priority, @state, @source)
            returning version
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown authoring entity kind"),
    };

    /// <summary>The update of a live entity from its new snapshot; the trigger bumps <c>version</c> and returns it.</summary>
    /// <param name="kind">The entity kind.</param>
    internal static string UpdateEntity(AuthoringEntityKind kind) => kind switch
    {
        AuthoringEntityKind.FeatureNode => """
            update public.feature_node
               set parent_id = @parent_id, kind = @kind, title = @title, position = @position
             where org_id = @org_id and project_id = @project_id and id = @id and deleted_at is null
            returning version
            """,
        AuthoringEntityKind.ChecklistItem => """
            update public.checklist_item
               set feature_node_id = @feature_node_id, title = @title, description = @description,
                   priority = @priority, state = @state, position = @position
             where org_id = @org_id and project_id = @project_id and id = @id and deleted_at is null
            returning version
            """,
        AuthoringEntityKind.ManualTestCase => """
            update public.manual_test_case
               set checklist_item_id = @checklist_item_id, code = @code, title = @title, description = @description,
                   precondition = @precondition, steps = @steps::jsonb, priority = @priority, state = @state
             where org_id = @org_id and project_id = @project_id and id = @id and deleted_at is null
            returning version
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown authoring entity kind"),
    };

    /// <summary>The soft delete: <c>deleted_at</c> set, the row kept, the trigger bumping <c>version</c> (AC7).</summary>
    /// <param name="kind">The entity kind.</param>
    internal static string SoftDeleteEntity(AuthoringEntityKind kind) => kind switch
    {
        AuthoringEntityKind.FeatureNode => SoftDeleteFeatureNode,
        AuthoringEntityKind.ChecklistItem => SoftDeleteChecklistItem,
        AuthoringEntityKind.ManualTestCase => SoftDeleteManualTestCase,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown authoring entity kind"),
    };

    private const string SoftDeleteFeatureNode = """
        update public.feature_node set deleted_at = clock_timestamp()
         where org_id = @org_id and project_id = @project_id and id = @id and deleted_at is null
        returning version
        """;

    private const string SoftDeleteChecklistItem = """
        update public.checklist_item set deleted_at = clock_timestamp()
         where org_id = @org_id and project_id = @project_id and id = @id and deleted_at is null
        returning version
        """;

    private const string SoftDeleteManualTestCase = """
        update public.manual_test_case set deleted_at = clock_timestamp()
         where org_id = @org_id and project_id = @project_id and id = @id and deleted_at is null
        returning version
        """;

    /// <summary>
    /// The write of a restore (AC8): <c>deleted_at</c> cleared and the re-applied snapshot written back, with the
    /// resolved position; the trigger bumps <c>version</c>.
    /// </summary>
    /// <param name="kind">The entity kind.</param>
    internal static string RestoreEntity(AuthoringEntityKind kind) => kind switch
    {
        AuthoringEntityKind.FeatureNode => RestoreFeatureNode,
        AuthoringEntityKind.ChecklistItem => RestoreChecklistItem,
        AuthoringEntityKind.ManualTestCase => RestoreManualTestCase,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown authoring entity kind"),
    };

    private const string RestoreFeatureNode = """
        update public.feature_node
           set deleted_at = null, parent_id = @parent_id, kind = @kind, title = @title, position = @position
         where org_id = @org_id and project_id = @project_id and id = @id
        returning version
        """;

    private const string RestoreChecklistItem = """
        update public.checklist_item
           set deleted_at = null, feature_node_id = @feature_node_id, title = @title, description = @description,
               priority = @priority, state = @state, position = @position
         where org_id = @org_id and project_id = @project_id and id = @id
        returning version
        """;

    private const string RestoreManualTestCase = """
        update public.manual_test_case
           set deleted_at = null, checklist_item_id = @checklist_item_id, code = @code, title = @title,
               description = @description, precondition = @precondition, steps = @steps::jsonb,
               priority = @priority, state = @state
         where org_id = @org_id and project_id = @project_id and id = @id
        returning version
        """;

    /// <summary>The AC7 non-empty guard of a feature node: live children and live checklist items.</summary>
    internal const string NodeContents = """
        select exists (select 1 from public.feature_node c
                        where c.org_id = @org_id and c.project_id = @project_id
                          and c.parent_id = @id and c.deleted_at is null) as has_children,
               exists (select 1 from public.checklist_item i
                        where i.org_id = @org_id and i.project_id = @project_id
                          and i.feature_node_id = @id and i.deleted_at is null) as has_items
        """;

    /// <summary>The AC7 non-empty guard of a checklist item: live manual cases.</summary>
    internal const string ItemContents = """
        select exists (select 1 from public.manual_test_case m
                        where m.org_id = @org_id and m.project_id = @project_id
                          and m.checklist_item_id = @id and m.deleted_at is null) as has_cases
        """;

    /// <summary>
    /// The sibling rule of AC2 applied to a create or a restore (AC8): is the row's <c>position</c> held by a live
    /// sibling, and what is the highest position among the live siblings (<c>@id</c> is the row itself, which never
    /// counts as its own sibling; a create passes its new id, which no row holds yet).
    /// </summary>
    /// <param name="root">
    /// True for the <c>parent_id is null</c> branch, false for the <c>parent_id = @parent_id</c> one. The predicate is
    /// branched instead of written <c>parent_id is not distinct from @parent_id</c> (OI-13-112): the latter is not a
    /// searchable form, so PostgreSQL scans the whole <c>(org_id, project_id)</c> prefix of <c>feature_node</c> for every
    /// call, while both branches are served by <c>feature_node_tree_idx</c> (and, for the <c>exists</c> half, by
    /// <c>feature_node_sibling_position_uq</c>). Both indexes stay: the read order covers <c>id</c>, the unique index
    /// covers the position.
    /// </param>
    internal static string NodeSiblingPosition(bool root) => root
        ? """
            select exists (select 1 from public.feature_node t
                            where t.org_id = @org_id and t.project_id = @project_id
                              and t.parent_id is null
                              and t.position = @position
                              and t.deleted_at is null
                              and t.id <> @id) as taken,
                   coalesce(max(s.position), -1) as max_position
              from public.feature_node s
             where s.org_id = @org_id and s.project_id = @project_id
               and s.parent_id is null
               and s.deleted_at is null
               and s.id <> @id
            """
        : """
            select exists (select 1 from public.feature_node t
                            where t.org_id = @org_id and t.project_id = @project_id
                              and t.parent_id = @parent_id
                              and t.position = @position
                              and t.deleted_at is null
                              and t.id <> @id) as taken,
                   coalesce(max(s.position), -1) as max_position
              from public.feature_node s
             where s.org_id = @org_id and s.project_id = @project_id
               and s.parent_id = @parent_id
               and s.deleted_at is null
               and s.id <> @id
            """;

    /// <summary>
    /// AC2's default position of a create: <c>max(live sibling position) + 1</c>, or 0 for the first sibling. The
    /// caller computes it before the writer sees the snapshot (the snapshot always carries an explicit position), and
    /// the partial unique index stays the authority — two concurrent creates that both read the same maximum answer
    /// <c>422 position_taken</c> for the loser, never a duplicate position.
    /// </summary>
    /// <param name="root">True for the root group, false for a named parent (same branching as <see cref="NodeSiblingPosition"/>).</param>
    internal static string NodeNextPosition(bool root) => root
        ? """
            select coalesce(max(n.position), -1) + 1
              from public.feature_node n
             where n.org_id = @org_id
               and n.project_id = @project_id
               and n.parent_id is null
               and n.deleted_at is null
            """
        : """
            select coalesce(max(n.position), -1) + 1
              from public.feature_node n
             where n.org_id = @org_id
               and n.project_id = @project_id
               and n.parent_id = @parent_id
               and n.deleted_at is null
            """;

    /// <summary>
    /// The live sibling set of one parent group, locked <c>for update</c> in id order (the writer-wide lock order: one
    /// rank, so ascending id) — the whole group, because a reorder renumbers all of it and a create only lands if no
    /// concurrent writer is moving the same group. Ordering by <c>id</c> rather than by the index's
    /// <c>(position, id)</c> keeps two reorders from acquiring the rows in opposite orders if a position changes
    /// between their snapshots.
    /// </summary>
    /// <param name="root">True for the root group, false for a named parent.</param>
    internal static string LockNodeSiblings(bool root) => root
        ? """
            select n.id, n.kind, n.title, n.position, n.version
              from public.feature_node n
             where n.org_id = @org_id
               and n.project_id = @project_id
               and n.parent_id is null
               and n.deleted_at is null
             order by n.id
             for update
            """
        : """
            select n.id, n.kind, n.title, n.position, n.version
              from public.feature_node n
             where n.org_id = @org_id
               and n.project_id = @project_id
               and n.parent_id = @parent_id
               and n.deleted_at is null
             order by n.id
             for update
            """;

    /// <summary>
    /// The first statement of AC2's two-phase renumber (OI-13-110): every <b>moved</b> sibling is parked above the
    /// group's current maximum, <c>position + @offset</c> with <c>@offset = max(position) + 1</c>. Parked values are
    /// therefore greater than every live position of the group (moved or not), so the assignment of the next statement
    /// can never collide with a value still held — and the partial unique index, which cannot be
    /// <c>DEFERRABLE</c>, is satisfied throughout.
    /// <para>
    /// Only the moved ids are touched: a sibling whose position does not change must keep its single version bump, or
    /// the row's <c>version</c> and its last <c>authoring_version.version_no</c> would drift apart.
    /// </para>
    /// </summary>
    /// <param name="root">True for the root group, false for a named parent.</param>
    internal static string ReorderPark(bool root) => root
        ? """
            update public.feature_node
               set position = position + @offset
             where org_id = @org_id
               and project_id = @project_id
               and parent_id is null
               and deleted_at is null
               and id = any(@ids)
            """
        : """
            update public.feature_node
               set position = position + @offset
             where org_id = @org_id
               and project_id = @project_id
               and parent_id = @parent_id
               and deleted_at is null
               and id = any(@ids)
            """;

    /// <summary>
    /// The second statement of AC2's two-phase renumber: each parked row takes its final, 0-based position from the
    /// caller's list in <b>one</b> set-based assignment. The final positions are <c>0..n-1</c> and the parked values are
    /// <c>&gt;= n</c> (they start at <c>max + 1</c> and <c>max &gt;= n-1</c>), so no final value meets a parked one.
    /// </summary>
    internal const string ReorderAssign = """
        update public.feature_node n
           set position = wanted.position
          from jsonb_to_recordset(@rows::jsonb) as wanted(id uuid, position integer)
         where n.org_id = @org_id
           and n.project_id = @project_id
           and n.deleted_at is null
           and n.id = wanted.id
        """;

    /// <summary>
    /// The versions the renumber left behind, read after the second statement: each moved node's <c>version</c> is the
    /// <c>version_no</c> its one <c>update</c> row carries (AC2, AC5), and it is two greater than the version the caller
    /// was shown — the trigger bumps on each of the two position statements. The writer asserts that delta instead of
    /// assuming it, so a future third statement surfaces as <c>409 invalid_transition</c> rather than as a gap in the
    /// history.
    /// </summary>
    internal const string ReorderVersions = """
        select n.id, n.version
          from public.feature_node n
         where n.org_id = @org_id
           and n.project_id = @project_id
           and n.id = any(@ids)
        """;

    /// <summary>The same two answers over the live checklist items of one node.</summary>
    internal const string ItemSiblingPosition = """
        select exists (select 1 from public.checklist_item t
                        where t.org_id = @org_id and t.project_id = @project_id
                          and t.feature_node_id = @feature_node_id
                          and t.position = @position
                          and t.deleted_at is null
                          and t.id <> @id) as taken,
               coalesce(max(s.position), -1) as max_position
          from public.checklist_item s
         where s.org_id = @org_id and s.project_id = @project_id
           and s.feature_node_id = @feature_node_id
           and s.deleted_at is null
           and s.id <> @id
        """;

    /// <summary>One stored version, for the <c>fromVersionId</c> restore and the 404 answers (AC8).</summary>
    internal const string ReadVersion = """
        select v.id, v.entity_kind, v.entity_id, v.version_no, v.content::text as content, v.content_hash
          from public.authoring_version v
         where v.org_id = @org_id and v.project_id = @project_id and v.id = @id
        """;

    /// <summary>
    /// The one insert of <c>authoring_version</c>: every row of the batch in one statement, the rows that already hold
    /// the <c>(entity, version_no)</c> key skipped (the caller holds the entity row lock, so a skip is a bug the writer
    /// reports instead of surfacing as a 23505).
    /// </summary>
    internal const string AppendVersions = """
        insert into public.authoring_version
               (id, org_id, project_id, entity_kind, entity_id, version_no, batch_id, change_kind, content,
                content_hash, source, author_membership_id, reason, ai_run_id, created_at)
        select r.id, @org_id, @project_id, r.entity_kind, r.entity_id, r.version_no, @batch_id, r.change_kind,
               r.content::jsonb, r.content_hash, r.source, @author_membership_id, r.reason, r.ai_run_id, clock_timestamp()
          from jsonb_to_recordset(@rows::jsonb)
               as r(id uuid, entity_kind text, entity_id uuid, version_no integer, change_kind text, content text,
                    content_hash text, source text, reason text, ai_run_id uuid)
        on conflict (org_id, project_id, entity_kind, entity_id, version_no) do nothing
        """;

    /// <summary>The counter row of a project, created on first allocation (<c>insert … on conflict do nothing</c>, §4).</summary>
    internal const string EnsureCodeCounter = """
        insert into public.authoring_code_counter (id, org_id, project_id)
        values (@id, @org_id, @project_id)
        on conflict (org_id, project_id) do nothing
        """;

    /// <summary>
    /// One allocation (§4): the row lock of the counter serialises allocators, and <c>returning next_value - 1</c>
    /// hands out the old value. The predicate is the 10⁹ cap — no row means <c>409 quota_exceeded</c>, never a code the
    /// format check would refuse.
    /// </summary>
    internal const string AllocateCode = """
        update public.authoring_code_counter
           set next_value = next_value + 1
         where org_id = @org_id and project_id = @project_id and next_value <= 1000000000
        returning next_value - 1 as number
        """;

    /// <summary>The project's stored <c>case_prefix</c> (OI-13-101: stored, never recomputed from the slug).</summary>
    internal const string ReadCasePrefix = """
        select p.case_prefix
          from public.project p
         where p.org_id = @org_id and p.id = @project_id
        """;

    /// <summary>The bounded wait of the counter lock (§4: <c>unavailable</c> 503 on a lock timeout, <c>SuiteCommands.cs:161</c>).</summary>
    internal const string SetLockTimeout = "select set_config('lock_timeout', '5000', true)";
}
