namespace Evidgate.Modules.Authoring.Infrastructure.Persistence;

/// <summary>
/// The statements and Dapper rows of the authoring change queue: the queue route's reads and its one insert, the
/// pending-overlap pre-check, and the two read routes over the same table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenant first, RLS second.</b> Every statement carries <c>org_id</c> and the project of the URL, so a change of
/// another project of this organization, of another organization or a random id are one 404; the policies scope the
/// rows again. The project probe is <see cref="AuthoringVersionReadSql.ProjectExists"/> — the same predicate the
/// other authoring routes use, so "this project is live" cannot drift between them.
/// </para>
/// <para>
/// <b>Bounded statements.</b> The queue performs one item read over the whole affected set, one pending count, one
/// overlap probe and one insert; the list route one keyset statement whatever the page size. The selection read
/// (<c>?ids=</c>) is one statement over at most 200 ids, and its affected-version read is one statement over their
/// union.
/// </para>
/// <para>
/// <b>Rows are mutable classes.</b> Dapper binds them through <c>DefaultTypeMap.MatchNamesWithUnderscores</c>; a
/// positional record cannot carry the nullable lifecycle columns the detail statement projects.
/// </para>
/// </remarks>
internal static class AuthoringChangeSql
{
    /// <summary>
    /// The live items of the affected set with the versions the queue stamps into <c>affected_versions</c>. A
    /// soft-deleted item is absent, which the handler answers as <c>target_deleted</c>; the same project and tenant
    /// are in the predicate, so a foreign item cannot enter the set.
    /// </summary>
    internal const string AffectedItems = """
        select i.id, i.version, i.deleted_at
          from public.checklist_item i
         where i.org_id = @org_id
           and i.project_id = @project_id
           and i.id = any(@item_ids)
        """;

    /// <summary>Live manual cases of one item: the queue-time half of 13.1.1's non-empty guard for a delete.</summary>
    internal const string LiveCaseCount = """
        select count(*)::int
          from public.manual_test_case m
         where m.org_id = @org_id
           and m.project_id = @project_id
           and m.checklist_item_id = @item_id
           and m.deleted_at is null
        """;

    /// <summary>
    /// The live cases of one item that are among the given ids: a split's <c>case_assignments</c> may only name cases of
    /// the item being split, and this read answers which of the named ones are exactly that (the rest is the refusal).
    /// </summary>
    internal const string LiveCaseIds = """
        select m.id
          from public.manual_test_case m
         where m.org_id = @org_id
           and m.project_id = @project_id
           and m.checklist_item_id = @item_id
           and m.deleted_at is null
           and m.id = any(@case_ids)
        """;

    /// <summary>
    /// The project's queue lock, keyed exactly as the guard trigger keys it. Taken before any pending count so the
    /// cap holds under READ COMMITTED: a count alone lets two requests both read 499.
    /// </summary>
    internal const string LockProject = """
        select pg_advisory_xact_lock(
            hashtextextended('authoring_change:' || @org_id::text || ':' || @project_id::text, 0))
        """;

    /// <summary>
    /// The project's pending total, served by the partial index on <c>state = 'pending'</c>. Correct only after
    /// <see cref="LockProject"/>: the count is serialised per project, not merely a hint.
    /// </summary>
    internal const string PendingCount = """
        select count(*)::int
          from public.authoring_change c
         where c.org_id = @org_id
           and c.project_id = @project_id
           and c.state = 'pending'
        """;

    /// <summary>
    /// The unlocked pre-check of the affected-set guard: any pending structural change whose stamped item set shares
    /// one item with the new change. It gives the caller a clean 409 before the insert; the guard trigger answers the
    /// same refusal for a racing insert, so this statement is never the guarantee. <c>@keys</c> is a <c>text[]</c>
    /// because jsonb's <c>?|</c> takes one, so it binds through the module's array parameter, never as a plain array.
    /// </summary>
    internal const string PendingStructuralOverlap = """
        select 1
          from public.authoring_change c
         where c.org_id = @org_id
           and c.project_id = @project_id
           and c.state = 'pending'
           and c.change_type in ('delete', 'merge', 'split')
           and c.affected_versions ?| @keys
         limit 1
        """;

    /// <summary>
    /// The queue insert. <c>affected_versions</c> is the server's map, never a request member; the guard trigger runs
    /// before this row is visible and raises <c>23514</c> for an overlap the pre-check could not see.
    /// </summary>
    internal const string Insert = """
        insert into public.authoring_change (
            id, org_id, project_id, target_kind, target_id, change_type, payload, base_version,
            affected_versions, state, source, ai_run_id, author_membership_id, reason)
        values (
            @id, @org_id, @project_id, @target_kind, @target_id, @change_type, @payload::jsonb, @base_version,
            @affected_versions::jsonb, 'pending', 'manual', null, @author_membership_id, @reason)
        returning created_at
        """;

    /// <summary>The projection every read route answers: the payload text is parsed by the handler, never echoed raw.</summary>
    private const string Columns = """
        c.id as change_id, c.target_kind, c.target_id, c.change_type, c.payload::text as payload, c.state, c.source,
        c.base_version, c.affected_versions::text as affected_versions, c.author_membership_id, c.reason,
        c.created_at, c.applied_at, c.applied_batch_id, c.discarded_at, c.version
        """;

    /// <summary>The detail read: one change of this project, or nothing at all for a foreign id.</summary>
    internal const string Get = $"""
        select {Columns}
          from public.authoring_change c
         where c.org_id = @org_id
           and c.project_id = @project_id
           and c.id = @change_id
        """;

    /// <summary>
    /// The list page without a <c>target_id</c> filter. The keyset seek is always present (the first page passes a
    /// sentinel pair) so it stays one searchable row comparison on <c>authoring_change_list_idx</c>; the optional
    /// <c>state</c> and <c>change_type</c> predicates are residual, which is what the plan's P4 note accepts for a
    /// filter that does not lead the index.
    /// </summary>
    internal const string Page = $"""
        select {Columns}
          from public.authoring_change c
         where c.org_id = @org_id
           and c.project_id = @project_id
           and (c.created_at, c.id) > (@cursor_created_at::timestamptz, @cursor_id::uuid)
           and (@state::text is null or c.state = @state)
           and (@change_type::text is null or c.change_type = @change_type)
         order by c.created_at,
                  c.id
         limit @take
        """;

    /// <summary>
    /// The list page with a <c>target_id</c>: the equality leads <c>authoring_change_target_idx</c>, and the remaining
    /// filters stay residual. One statement per shape keeps the SQL text a constant (00 §5.7 S1).
    /// </summary>
    internal const string PageByTarget = $"""
        select {Columns}
          from public.authoring_change c
         where c.org_id = @org_id
           and c.project_id = @project_id
           and c.target_id = @target_id
           and (c.created_at, c.id) > (@cursor_created_at::timestamptz, @cursor_id::uuid)
           and (@state::text is null or c.state = @state)
           and (@change_type::text is null or c.change_type = @change_type)
         order by c.created_at,
                  c.id
         limit @take
        """;

    /// <summary>The <c>?ids=</c> selection: no other filter is accepted with it, so the read is one <c>any</c>.</summary>
    internal const string Selection = $"""
        select {Columns}
          from public.authoring_change c
         where c.org_id = @org_id
           and c.project_id = @project_id
           and c.id = any(@ids)
         order by c.created_at,
                  c.id
        """;

    /// <summary>
    /// The current versions of the selection's affected items, over the union the handler built from
    /// <c>affected_versions</c>. Soft-deleted rows are included on purpose: their version moved when they were
    /// deleted, and that is exactly what makes a queued change stale.
    /// </summary>
    internal const string ItemVersions = """
        select i.id, i.version
          from public.checklist_item i
         where i.org_id = @org_id
           and i.project_id = @project_id
           and i.id = any(@item_ids)
        """;

    /// <summary>One affected item and the version the queue stamped for it.</summary>
    internal sealed class ItemVersion
    {
        /// <summary>The item's id.</summary>
        public Guid Id { get; init; }

        /// <summary>Its version now.</summary>
        public int Version { get; init; }

        /// <summary>When it was soft-deleted, or null while it is live: a gone item is 422, an absent one is 404.</summary>
        public DateTimeOffset? DeletedAt { get; init; }
    }

    /// <summary>The insert's answer: the store's <c>created_at</c>, so the response echoes the stored instant.</summary>
    internal sealed class InsertedChange
    {
        /// <summary>When the row was created.</summary>
        public DateTimeOffset CreatedAt { get; init; }
    }

    /// <summary>One change row as the wire DTOs need it; <c>payload</c> and <c>affected_versions</c> arrive as text.</summary>
    internal sealed class ChangeRow
    {
        /// <summary>The change id.</summary>
        public Guid ChangeId { get; init; }

        /// <summary>The target kind, always <c>checklist_item</c> today.</summary>
        public string TargetKind { get; init; } = string.Empty;

        /// <summary>The target item, or the merge's survivor.</summary>
        public Guid TargetId { get; init; }

        /// <summary>One of <c>comment</c>, <c>delete</c>, <c>merge</c>, <c>split</c>.</summary>
        public string ChangeType { get; init; } = string.Empty;

        /// <summary>The stored payload as JSON text.</summary>
        public string Payload { get; init; } = string.Empty;

        /// <summary>One of <c>pending</c>, <c>applied</c>, <c>discarded</c>.</summary>
        public string State { get; init; } = string.Empty;

        /// <summary>Who proposed the change: <c>manual</c>, <c>ai</c> or <c>import</c>.</summary>
        public string Source { get; init; } = string.Empty;

        /// <summary>The target's version when the change was queued.</summary>
        public int BaseVersion { get; init; }

        /// <summary>The stamped item-to-version map as JSON text.</summary>
        public string AffectedVersions { get; init; } = string.Empty;

        /// <summary>The membership that queued the change.</summary>
        public Guid AuthorMembershipId { get; init; }

        /// <summary>The optional reason the caller sent.</summary>
        public string? Reason { get; init; }

        /// <summary>When the row was created, the list's first sort key.</summary>
        public DateTimeOffset CreatedAt { get; init; }

        /// <summary>When the change was applied, null while pending or discarded.</summary>
        public DateTimeOffset? AppliedAt { get; init; }

        /// <summary>The batch the change was applied in, null while pending or discarded.</summary>
        public Guid? AppliedBatchId { get; init; }

        /// <summary>When the change was discarded, null otherwise.</summary>
        public DateTimeOffset? DiscardedAt { get; init; }

        /// <summary>The row's own lifecycle version, the detail route's ETag.</summary>
        public int Version { get; init; }
    }
}
