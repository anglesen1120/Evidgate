using Evidgate.Modules.Authoring.Domain;

namespace Evidgate.Modules.Authoring.Application.Import;

/// <summary>
/// The four-to-five statements of <see cref="AuthoringExporter"/> (13.1.1 §5 route 25, AC13/AC14): the project-liveness
/// probe, the subtree root probe, and one statement per kind of exported row, so the statement count is constant whatever
/// the size of the tree (P3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the read SQL lives beside the exporter, not in <c>Infrastructure/Persistence</c>.</b> The module's other read
/// models are shared by several handlers (<c>AuthoringNodeReadSql</c>, <c>AuthoringChecklistReadSql</c>,
/// <c>AuthoringManualCaseReadSql</c>); these statements have exactly one caller, the exporter, so they ship in the same
/// folder as their single consumer and can be read with it — the same reason <c>AuthoringSql</c> keeps the writer's
/// statements together for the write path. The project probe is the one exception: it reuses
/// <see cref="Evidgate.Modules.Authoring.Infrastructure.Persistence.AuthoringNodeReadSql.ProjectExists"/> rather than
/// restating the predicate.
/// </para>
/// <para>
/// <b>Every statement is bound</b> (00 §5.7 S1) and filters on <c>org_id</c> <em>and</em> the project of the URL as well
/// as relying on row level security, so another project's rows of the same tenant are invisible instead of being exported
/// (00 §5.4.7: an unset tenant GUC must yield zero rows, never all rows). Live rows only: every statement carries
/// <c>deleted_at is null</c>, so a tombstone never lands in a file (AC7/AC8).
/// </para>
/// <para>
/// <b>Order.</b> Each statement returns one total order over the whole set — <c>(position, id)</c> for nodes and items,
/// <c>(code, id)</c> for cases — and the exporter groups that stream by parent, which leaves every sibling list in exactly
/// the order the statement sorted it by. The tree order itself (pre-order, parents before children) is assembled in
/// <see cref="AuthoringExporter.ExportAsync"/>, never in SQL: a recursive walk that also sorts is a statement whose plan
/// depends on the data.
/// </para>
/// <para>
/// <b>Bounded reads.</b> Every statement carries <c>limit @take</c> with <c>@take = ImportLimits.MaxDataRows + 1</c>, so a
/// scope over the export cap is <em>detected</em> by the one row past it and never materialised in full (AC16's row cap
/// applied to the read side).
/// </para>
/// <para>
/// <b>The subtree</b> of <c>node_id</c> is a recursive CTE bounded by the AC1 ladder (<c>s.depth &lt; @max_depth</c>, four
/// levels), so the recursion terminates even on a tree a manual <c>INSERT</c> could have nested deeper than the API
/// allows: a fifth level is simply not part of the export. Its root keeps the id the caller addressed and the exporter
/// emits it with a null <c>parent_key</c>, because the parent is not in the file.
/// </para>
/// </remarks>
internal static class ExportSql
{
    /// <summary>
    /// The recursive subtree of one node as a common table expression over <c>feature_node_tree_idx</c>: the addressed
    /// live node at depth 1, then each live child of a row already in scope while the ladder allows another level.
    /// </summary>
    private const string Subtree = """
        with recursive scope as (
            select n.id, n.parent_id, n.kind, n.title, n.position, 1 as depth
              from public.feature_node n
             where n.org_id = @org_id
               and n.project_id = @project_id
               and n.id = @root_id
               and n.deleted_at is null
            union all
            select c.id, c.parent_id, c.kind, c.title, c.position, s.depth + 1
              from public.feature_node c
              join scope s
                on c.parent_id = s.id
             where c.org_id = @org_id
               and c.project_id = @project_id
               and c.deleted_at is null
               and s.depth < @max_depth)
        """;

    /// <summary>Every live node of the project, in the tree's sibling order.</summary>
    internal const string Nodes = """
        select n.id, n.parent_id, n.kind, n.title, n.position
          from public.feature_node n
         where n.org_id = @org_id
           and n.project_id = @project_id
           and n.deleted_at is null
         order by n.position,
                  n.id
         limit @take
        """;

    /// <summary>The live nodes of one node's subtree, the root included.</summary>
    internal const string NodesOfSubtree = $"""
        {Subtree}
        select s.id, s.parent_id, s.kind, s.title, s.position
          from scope s
         order by s.position,
                  s.id
         limit @take
        """;

    /// <summary>Every live checklist item of the project, in its sibling order.</summary>
    internal const string Items = """
        select i.id, i.feature_node_id, i.title, i.description, i.priority, i.state, i.position
          from public.checklist_item i
         where i.org_id = @org_id
           and i.project_id = @project_id
           and i.deleted_at is null
         order by i.position,
                  i.id
         limit @take
        """;

    /// <summary>The live items of the nodes in the subtree.</summary>
    internal const string ItemsOfSubtree = $"""
        {Subtree}
        select i.id, i.feature_node_id, i.title, i.description, i.priority, i.state, i.position
          from public.checklist_item i
          join scope s
            on s.id = i.feature_node_id
         where i.org_id = @org_id
           and i.project_id = @project_id
           and i.deleted_at is null
         order by i.position,
                  i.id
         limit @take
        """;

    /// <summary>
    /// Every live manual test case of the project in the list route's own <c>(code, id)</c> order, with the stored
    /// <c>steps</c> document cast to text (the exporter re-serialises it compactly; the cast spares a binary read).
    /// </summary>
    internal const string Cases = """
        select m.id, m.checklist_item_id, m.code, m.title, m.description, m.precondition,
               m.steps::text as steps, m.priority, m.state
          from public.manual_test_case m
         where m.org_id = @org_id
           and m.project_id = @project_id
           and m.deleted_at is null
         order by m.code,
                  m.id
         limit @take
        """;

    /// <summary>The live cases of the items in the subtree.</summary>
    internal const string CasesOfSubtree = $"""
        {Subtree}
        select m.id, m.checklist_item_id, m.code, m.title, m.description, m.precondition,
               m.steps::text as steps, m.priority, m.state
          from public.manual_test_case m
          join public.checklist_item i
            on i.org_id = m.org_id
           and i.project_id = m.project_id
           and i.id = m.checklist_item_id
          join scope s
            on s.id = i.feature_node_id
         where m.org_id = @org_id
           and m.project_id = @project_id
           and m.deleted_at is null
         order by m.code,
                  m.id
         limit @take
        """;

    /// <summary>One live feature node, as the exporter walks the tree by it.</summary>
    internal sealed class NodeRow
    {
        /// <summary>The node id (the export's own identity for the row; never written to the file).</summary>
        public Guid Id { get; set; }

        /// <summary>The parent node, or null for a root.</summary>
        public Guid? ParentId { get; set; }

        /// <summary>One of the four AC1 kinds.</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>The stored title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>The sibling position.</summary>
        public int Position { get; set; }
    }

    /// <summary>One live checklist item.</summary>
    internal sealed class ItemRow
    {
        /// <summary>The item id.</summary>
        public Guid Id { get; set; }

        /// <summary>The node the item hangs under.</summary>
        public Guid FeatureNodeId { get; set; }

        /// <summary>The stored title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>The stored description, or null.</summary>
        public string? Description { get; set; }

        /// <summary>The stored priority.</summary>
        public string Priority { get; set; } = string.Empty;

        /// <summary>The stored state.</summary>
        public string State { get; set; } = string.Empty;

        /// <summary>The sibling position among the node's items.</summary>
        public int Position { get; set; }
    }

    /// <summary>One live manual test case.</summary>
    internal sealed class CaseRow
    {
        /// <summary>The case id.</summary>
        public Guid Id { get; set; }

        /// <summary>The item the case sits under.</summary>
        public Guid ChecklistItemId { get; set; }

        /// <summary>The immutable <c>&lt;PREFIX&gt;-&lt;n&gt;</c> code.</summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>The stored title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>The stored description, or null.</summary>
        public string? Description { get; set; }

        /// <summary>The stored precondition, or null.</summary>
        public string? Precondition { get; set; }

        /// <summary>The stored <c>authoring-steps.v1</c> document as JSON text.</summary>
        public string Steps { get; set; } = string.Empty;

        /// <summary>The stored priority.</summary>
        public string Priority { get; set; } = string.Empty;

        /// <summary>The stored state.</summary>
        public string State { get; set; } = string.Empty;
    }

    /// <summary>
    /// The bound parameters every statement of the exporter shares. A member a particular statement does not reference is
    /// simply not bound by Dapper, so one object serves the project form and the subtree form alike (the two forms are
    /// still branched, never merged behind <c>is not distinct from</c>, so each keeps its own index-friendly predicate).
    /// </summary>
    /// <param name="orgId">The tenant of the scope.</param>
    /// <param name="projectId">The project of the URL.</param>
    /// <param name="rootId">The subtree root, or null for a whole-project export.</param>
    /// <param name="take">The row cap plus one: the read stops one row past the cap.</param>
    internal static object Parameters(Guid orgId, Guid projectId, Guid? rootId, int take) => new
    {
        org_id = orgId,
        project_id = projectId,
        root_id = rootId,
        max_depth = FeatureNodeKinds.MaximumDepth + 1,
        take,
    };
}
