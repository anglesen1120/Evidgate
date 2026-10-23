namespace Evidgate.Modules.Authoring.Infrastructure.Persistence;

/// <summary>
/// The read statements of the checklist item routes (13.1.1 §5 routes 7-10): one list page, one item, and the two
/// locks an edit or a delete takes before it writes (AC19's atomic <c>If-Match</c>).
/// </summary>
/// <remarks>
/// Every statement names the tenant columns explicitly even though RLS also scopes them (00 §5.4.7: an unset tenant GUC
/// must yield zero rows, never all rows); the explicit predicates keep the plan on the declared indexes
/// (<c>checklist_item_list_idx</c>, 0039:279-281) and make the intent readable. The column aliases are quoted so a
/// positional record maps by constructor order, exactly as the catalog's <c>CaseReader</c> maps <c>CaseDto</c>.
/// </remarks>
internal static class AuthoringChecklistReadSql
{
    /// <summary>
    /// One keyset page, ordered <c>(position, id)</c> (13.1.1 §5 route 7, AC2's order). The cursor predicate is a row
    /// comparison over the same two columns, so the index serves it; <c>@after_position</c> null means the first page.
    /// One extra row is fetched by the caller to decide whether a next page exists.
    /// </summary>
    internal const string List = """
        select i.id as "ItemId",
               i.feature_node_id as "NodeId",
               i.title as "Title",
               i.description as "Description",
               i.priority as "Priority",
               i.state as "State",
               i.source as "Source",
               i.position as "Position",
               i.version as "Version"
        from checklist_item i
        where i.org_id = @org_id
          and i.project_id = @project_id
          and i.deleted_at is null
          and (@node_id::uuid is null or i.feature_node_id = @node_id::uuid)
          and (@state::text is null or i.state = @state::text)
          and (@priority::text is null or i.priority = @priority::text)
          and (@after_position::integer is null
               or (i.position, i.id) > (@after_position::integer, @after_id::uuid))
        order by i.position, i.id
        limit @take
        """;

    /// <summary>One live item of the project; a soft-deleted or other-project row is simply absent (AC10).</summary>
    internal const string One = """
        select i.id as "ItemId",
               i.feature_node_id as "NodeId",
               i.title as "Title",
               i.description as "Description",
               i.priority as "Priority",
               i.state as "State",
               i.source as "Source",
               i.position as "Position",
               i.version as "Version"
        from checklist_item i
        where i.org_id = @org_id
          and i.project_id = @project_id
          and i.id = @item_id
          and i.deleted_at is null
        """;

    /// <summary>
    /// The parent node's share lock, taken **before** the item lock: the version writer locks a node and then its
    /// children in that order (<c>AuthoringVersionWriter.LockBatchAsync</c>, AC2/AC7), so a command that inverted the
    /// order could deadlock against a batch. <c>select 1 … for share</c> locks the row without reading its columns.
    /// </summary>
    internal const string LockParentShare = """
        select 1
        from feature_node n
        where n.org_id = @org_id
          and n.project_id = @project_id
          and n.id = @node_id
        for share
        """;

    /// <summary>
    /// The item's own row lock, taken after <see cref="LockParentShare"/>: the AC19 version check that follows it is
    /// then atomic with the write, so two editors holding the same <c>If-Match</c> can never both succeed.
    /// </summary>
    internal const string LockItem = """
        select i.id as "ItemId",
               i.feature_node_id as "NodeId",
               i.title as "Title",
               i.description as "Description",
               i.priority as "Priority",
               i.state as "State",
               i.source as "Source",
               i.position as "Position",
               i.version as "Version"
        from checklist_item i
        where i.org_id = @org_id
          and i.project_id = @project_id
          and i.id = @item_id
          and i.deleted_at is null
        for update
        """;

    /// <summary>
    /// The position a create without one lands on: after the node's last live item. Items have no sibling uniqueness
    /// constraint (unlike <c>feature_node</c>, 0039:237-239), so a race here can only produce two equal positions, which
    /// the <c>(position, id)</c> order still renders deterministically (AC2).
    /// </summary>
    internal const string NextPosition = """
        select coalesce(max(i.position) + 1, 0)
        from checklist_item i
        where i.org_id = @org_id
          and i.project_id = @project_id
          and i.feature_node_id = @node_id
          and i.deleted_at is null
        """;
}
