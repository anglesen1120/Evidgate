namespace Evidgate.Modules.Authoring.Infrastructure.Persistence;

/// <summary>
/// The Dapper read model of the feature-tree routes (13.1.1 §5 routes 1-5): the tree page, its counts, the ancestor
/// path and the two sibling readers the write routes need before they reach the writer. Every statement is bound and
/// every list order is <c>(position, id)</c> over one of the two declared indexes of migration 0039 —
/// <c>feature_node_tree_idx</c> for a sibling page (<c>org, project, parent_id, position, id</c>, live rows only) and
/// the partial unique <c>feature_node_sibling_position_uq</c> for a single position probe.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate file.</b> <see cref="AuthoringSql"/> is the SQL of the write path (the writer and the code
/// allocator), so a reviewer can read the mutations in one place; this file is its read counterpart, the
/// <c>CatalogReadSql</c>/<c>SuiteReadSql</c> split (03.1.1/03.2.1 of this repository).
/// </para>
/// <para>
/// <b>Root versus child probes are branched, never written with <c>is not distinct from</c></b> (OI-13-112): the
/// predicate must match the index, and <c>parent_id is not distinct from @parent_id</c> is not a searchable form, so
/// PostgreSQL would scan the whole <c>(org, project)</c> prefix of the table for every call. The root branch states
/// <c>parent_id is null</c>, which the same index serves.
/// </para>
/// <para>
/// <b>Statement budget</b> (P3): a root page is three statements (project liveness, the page, the order tag); a child
/// page four (the parent's liveness included); a <c>node_id</c> read five (node, ancestors, its children, counts, order
/// tag). The counts are one statement for the whole page — never one per node — and the ancestors are one recursive
/// statement over the at-most-four-level ladder.
/// </para>
/// </remarks>
internal static class AuthoringNodeReadSql
{
    /// <summary>The project's liveness: another tenant's or another project's id answers 404 (AC10).</summary>
    internal const string ProjectExists = """
        select 1
          from public.project p
         where p.org_id = @org_id
           and p.id = @project_id
           and p.status <> 'deleted'
        """;

    /// <summary>One live node by id, for the <c>node_id</c> read and the parent probe of a child page.</summary>
    internal const string LiveNode = $"""
        select {NodeColumns}
          from public.feature_node n
         where n.org_id = @org_id
           and n.project_id = @project_id
           and n.id = @id
           and n.deleted_at is null
        """;

    /// <summary>
    /// The <c>for update</c> read of a node before a <c>PATCH</c> or <c>DELETE</c>: the stored snapshot the command
    /// merges the caller's members into, the <c>version</c> the <c>If-Match</c> is compared with, and the tombstone stamp
    /// that turns an already deleted node into the same 404 as an absent one. The lock is what makes AC19 hold under
    /// concurrency: the row is held until the command's transaction ends, so a second writer waits here and then compares
    /// its own <c>If-Match</c> against the value the first one committed (412), instead of reaching the writer with a
    /// stale snapshot that would pass its out-of-step guard.
    /// </summary>
    /// <remarks>
    /// The predicate deliberately omits <c>deleted_at is null</c>: a tombstoned row must be <b>found</b> so the command can
    /// answer 404 for it (and so a restore route can see it) rather than reporting "no such node" from an empty read.
    /// </remarks>
    internal const string LockNode = """
        select n.id, n.parent_id, n.kind, n.title, n.position, n.deleted_at, n.version
          from public.feature_node n
         where n.org_id = @org_id
           and n.project_id = @project_id
           and n.id = @id
           for update
        """;

    /// <summary>First page of the project's live roots (<c>parent_id is null</c>), ordered <c>(position, id)</c> (AC2).</summary>
    internal const string RootsFirstPage = $"""
        select {NodeColumns}
          from public.feature_node n
         where n.org_id = @org_id
           and n.project_id = @project_id
           and n.parent_id is null
           and n.deleted_at is null
         order by n.position,
                  n.id
         limit @limit
        """;

    /// <summary>The next root page: the same page behind the row-comparison cursor (00 §9.1).</summary>
    internal const string RootsNextPage = $"""
        select {NodeColumns}
          from public.feature_node n
         where n.org_id = @org_id
           and n.project_id = @project_id
           and n.parent_id is null
           and n.deleted_at is null
           and (n.position, n.id) > (@cursor_position, @cursor_id::uuid)
         order by n.position,
                  n.id
         limit @limit
        """;

    /// <summary>First page of one parent's live children, ordered <c>(position, id)</c> (AC2).</summary>
    internal const string ChildrenFirstPage = $"""
        select {NodeColumns}
          from public.feature_node n
         where n.org_id = @org_id
           and n.project_id = @project_id
           and n.parent_id = @parent_id
           and n.deleted_at is null
         order by n.position,
                  n.id
         limit @limit
        """;

    /// <summary>The next page of one parent's live children.</summary>
    internal const string ChildrenNextPage = $"""
        select {NodeColumns}
          from public.feature_node n
         where n.org_id = @org_id
           and n.project_id = @project_id
           and n.parent_id = @parent_id
           and n.deleted_at is null
           and (n.position, n.id) > (@cursor_position, @cursor_id::uuid)
         order by n.position,
                  n.id
         limit @limit
        """;

    /// <summary>
    /// The ancestor chain of one node, root first and without the node itself (route 1's <c>path</c>): one recursive
    /// statement over the depth ladder, which is at most four levels and structurally acyclic (AC1), so the recursion
    /// terminates on the root's <c>null</c> parent. A soft-deleted ancestor is skipped, never rendered as a gone node.
    /// </summary>
    internal const string Ancestors = """
        with recursive chain as (select n.id, n.parent_id, n.kind, n.title, 0 as depth
                                   from public.feature_node n
                                  where n.org_id = @org_id
                                    and n.project_id = @project_id
                                    and n.id = @id
                                    and n.deleted_at is null
                                 union all
                                 select p.id, p.parent_id, p.kind, p.title, c.depth + 1
                                   from public.feature_node p
                                   join chain c on c.parent_id = p.id
                                  where p.org_id = @org_id
                                    and p.project_id = @project_id
                                    and p.deleted_at is null)
        select c.id, c.kind, c.title
          from chain c
         where c.depth > 0
         order by c.depth desc
        """;

    /// <summary>
    /// The two counts of a whole page in one statement (never one query per node): <c>child_count</c> is the live child
    /// nodes and <c>item_count</c> the live checklist items of each requested id. A node with no children still gets a
    /// row, so the projection never guesses a zero.
    /// </summary>
    internal const string Counts = """
        select wanted.id                                                                        as node_id,
               (select count(*)
                  from public.feature_node c
                 where c.org_id = @org_id
                   and c.project_id = @project_id
                   and c.parent_id = wanted.id
                   and c.deleted_at is null)                                                    as child_count,
               (select count(*)
                  from public.checklist_item i
                 where i.org_id = @org_id
                   and i.project_id = @project_id
                   and i.feature_node_id = wanted.id
                   and i.deleted_at is null)                                                    as item_count
          from unnest(@ids::uuid[]) as wanted(id)
        """;

    /// <summary>
    /// The live siblings of one parent group as <c>(id, version)</c> pairs in stored order: the input of AC2's
    /// <c>"order:&lt;sha256&gt;"</c> ETag, and nothing else. The pair set is what the hash covers, so the read returns
    /// the ids and versions only — no title, no path.
    /// </summary>
    internal const string SiblingVersionsRoot = """
        select n.id, n.version
          from public.feature_node n
         where n.org_id = @org_id
           and n.project_id = @project_id
           and n.parent_id is null
           and n.deleted_at is null
         order by n.position,
                  n.id
        """;

    /// <summary>The same read under a named parent.</summary>
    internal const string SiblingVersionsChild = """
        select n.id, n.version
          from public.feature_node n
         where n.org_id = @org_id
           and n.project_id = @project_id
           and n.parent_id = @parent_id
           and n.deleted_at is null
         order by n.position,
                  n.id
        """;

    private const string NodeColumns = """
               n.id, n.parent_id, n.kind, n.title, n.position, n.version
        """;

    /// <summary>One row of a sibling page.</summary>
    internal sealed class NodePageRow
    {
        /// <summary>The node id.</summary>
        public Guid Id { get; set; }

        /// <summary>The parent, or null for a root.</summary>
        public Guid? ParentId { get; set; }

        /// <summary>One of the four kinds.</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>The title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>The sibling position.</summary>
        public int Position { get; set; }

        /// <summary>The entity version (the <c>If-Match</c> value of a later write, AC19).</summary>
        public int Version { get; set; }
    }

    /// <summary>One row of <see cref="Counts"/>.</summary>
    internal sealed class NodeCountRow
    {
        /// <summary>Which node the counts belong to.</summary>
        public Guid NodeId { get; set; }

        /// <summary>Live child nodes.</summary>
        public int ChildCount { get; set; }

        /// <summary>Live checklist items.</summary>
        public int ItemCount { get; set; }
    }

    /// <summary>One <c>(id, version)</c> pair of the sibling order.</summary>
    internal sealed class SiblingVersionRow
    {
        /// <summary>The sibling's id.</summary>
        public Guid Id { get; set; }

        /// <summary>The sibling's version.</summary>
        public int Version { get; set; }
    }

    /// <summary>One ancestor of the requested node.</summary>
    internal sealed class NodePathRow
    {
        /// <summary>The ancestor's id.</summary>
        public Guid Id { get; set; }

        /// <summary>The ancestor's kind.</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>The ancestor's title.</summary>
        public string Title { get; set; } = string.Empty;
    }

    /// <summary>
    /// The locked row a <c>PATCH</c> or <c>DELETE</c> reads before it calls the writer: the columns of
    /// <see cref="AuthoringSql.LockEntity"/> for a feature node, so the command can see the stored snapshot, the
    /// <c>version</c> the caller's <c>If-Match</c> is checked against (AC19) and whether the row is already a
    /// tombstone (AC7's "gone entities are 404").
    /// </summary>
    internal sealed class LockedNodeRow
    {
        /// <summary>The node id.</summary>
        public Guid Id { get; set; }

        /// <summary>The parent, or null for a root.</summary>
        public Guid? ParentId { get; set; }

        /// <summary>One of the four kinds.</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>The stored title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>The stored sibling position.</summary>
        public int Position { get; set; }

        /// <summary>The soft-delete stamp, or null while the row is live.</summary>
        public DateTime? DeletedAt { get; set; }

        /// <summary>The entity version the trigger maintains.</summary>
        public int Version { get; set; }
    }
}
