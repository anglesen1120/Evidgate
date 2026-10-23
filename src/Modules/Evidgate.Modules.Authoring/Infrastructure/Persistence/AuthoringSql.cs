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
    /// The sibling rule of AC2 applied to a restore (AC8): is the restored row's old <c>position</c> held by a live
    /// sibling, and what is the highest position among the live siblings (<c>@id</c> is the row itself, which never
    /// counts as its own sibling).
    /// </summary>
    internal const string NodeSiblingPosition = """
        select exists (select 1 from public.feature_node t
                        where t.org_id = @org_id and t.project_id = @project_id
                          and t.parent_id is not distinct from @parent_id
                          and t.position = @position
                          and t.deleted_at is null
                          and t.id <> @id) as taken,
               coalesce(max(s.position), -1) as max_position
          from public.feature_node s
         where s.org_id = @org_id and s.project_id = @project_id
           and s.parent_id is not distinct from @parent_id
           and s.deleted_at is null
           and s.id <> @id
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
