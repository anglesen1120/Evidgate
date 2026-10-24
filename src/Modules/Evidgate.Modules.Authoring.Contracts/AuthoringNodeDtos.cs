namespace Evidgate.Modules.Authoring.Contracts;

/// <summary>
/// One feature node as the tree routes answer it (13.1.1 §5 route 1): the identity of the node, its place in the
/// tree and the two counts the UI renders next to it. Published as the <c>AuthoringNode</c> schema of
/// <c>contracts/openapi.yaml</c>.
/// </summary>
/// <param name="NodeId">The <c>feature_node.id</c>.</param>
/// <param name="ParentId">The parent node, or null for a root <c>module</c>.</param>
/// <param name="Kind">One of <c>module</c>, <c>feature</c>, <c>category</c>, <c>sub_category</c>.</param>
/// <param name="Title">The node's display title (1..200 characters, NFC).</param>
/// <param name="Position">The sibling position: unique among the live siblings of one parent (AC2).</param>
/// <param name="Version">The entity's <c>version</c>, the value an <c>If-Match</c> carries (AC19).</param>
/// <param name="ChildCount">How many <b>live</b> child nodes the node has (always returned, §5 route 1).</param>
/// <param name="ItemCount">How many <b>live</b> checklist items hang off the node (always returned).</param>
public sealed record NodeDto(
    Guid NodeId,
    Guid? ParentId,
    string Kind,
    string Title,
    int Position,
    int Version,
    int ChildCount,
    int ItemCount);

/// <summary>
/// One ancestor of a node, root first (13.1.1 §5 route 1's <c>path</c>): the breadcrumb of a node, without the node
/// itself and without the counts a breadcrumb never renders. Published as the <c>AuthoringNodePathEntry</c> schema.
/// </summary>
/// <param name="NodeId">The ancestor's id.</param>
/// <param name="Kind">The ancestor's kind.</param>
/// <param name="Title">The ancestor's title.</param>
public sealed record NodePathEntry(Guid NodeId, string Kind, string Title);

/// <summary>
/// The answer of <c>GET /v1/projects/{p}/authoring/tree</c> (13.1.1 §5 route 1): one sibling page, plus — when the
/// caller addressed one node — that node and its ancestor path. Published as the <c>AuthoringTree</c> schema.
/// </summary>
/// <param name="Items">
/// The page of live nodes ordered <c>(position, id)</c>: the project's roots when the request named neither
/// <c>node_id</c> nor <c>parent_id</c>, the live children of <c>parent_id</c>, or the live children of <c>node_id</c>.
/// </param>
/// <param name="NextCursor">The opaque keyset cursor of the next page, or null on the last page.</param>
/// <param name="Node">
/// The node the request addressed with <c>node_id</c> — including a leaf, which then has no <c>items</c> — or null
/// when the request listed a sibling group.
/// </param>
/// <param name="Path">
/// The ancestors of <c>node_id</c>, root first, excluding the node itself; with <c>parent_id</c> and
/// <c>include=path</c>, the chain from the root down to and including <c>parent_id</c>. Null when no path was asked
/// for and when the request listed the project's roots.
/// </param>
public sealed record AuthoringTreeResponse(
    IReadOnlyList<NodeDto> Items,
    string? NextCursor,
    NodeDto? Node,
    IReadOnlyList<NodePathEntry>? Path);

/// <summary>
/// The body of <c>POST /v1/projects/{p}/authoring/nodes</c> (13.1.1 §5 route 2, AC1/AC2). Published as the
/// <c>CreateNodeRequest</c> schema; the route reads the raw body so a malformed member answers a per-member 422.
/// </summary>
/// <param name="ParentId">The parent node; omitted or null places a root <c>module</c>.</param>
/// <param name="Kind">One of <c>module</c>, <c>feature</c>, <c>category</c>, <c>sub_category</c> (the ladder of AC1).</param>
/// <param name="Title">The display title, 1..200 characters after NFC normalisation.</param>
/// <param name="Position">
/// The sibling position; omitted appends at <c>max(live sibling position) + 1</c>, and a position a live sibling
/// already holds is refused 422 <c>position_taken</c> (AC2).
/// </param>
public sealed record CreateNodeRequest(Guid? ParentId, string? Kind, string? Title, int? Position);

/// <summary>
/// The answer of <c>POST /v1/projects/{p}/authoring/nodes</c> (13.1.1 §5 route 2): the id the server generated and
/// the version the new row was written at. Published as the <c>CreateNodeResponse</c> schema.
/// </summary>
/// <param name="NodeId">The new <c>feature_node.id</c> (a UUIDv7).</param>
/// <param name="Version">The row's <c>version</c>, 1 for a create.</param>
public sealed record CreateNodeResponse(Guid NodeId, int Version);

/// <summary>
/// The body of <c>PATCH /v1/projects/{p}/authoring/nodes/{n}</c> (13.1.1 §5 route 3): every member is optional and an
/// omitted one is left unchanged. <c>kind</c> is deliberately absent — a node's kind is fixed once it exists.
/// Published as the <c>UpdateNodeRequest</c> schema.
/// </summary>
/// <param name="Title">The new title, 1..200 characters after NFC normalisation.</param>
/// <param name="ParentId">
/// The new parent, re-applying AC1's ladder and the renumber of AC2 (route 3): a <c>feature</c> may move module to
/// module, and the writer refuses only a parent kind the ladder does not allow, a missing or soft-deleted parent and a
/// node that would become its own ancestor (<c>422 authoring_parent_invalid</c>). With no explicit
/// <paramref name="Position"/> the node lands at the end of its new group. Null is not "move to the root": an omitted
/// member is the unchanged one.
/// </param>
/// <param name="Position">The new sibling position; a position a live sibling holds is refused <c>position_taken</c>.</param>
public sealed record UpdateNodeRequest(string? Title, Guid? ParentId, int? Position);

/// <summary>
/// The body of <c>POST /v1/projects/{p}/authoring/nodes/{n}/reorder</c> (13.1.1 §5 route 4, AC2): the full list of
/// live siblings of <c>{n}</c>'s parent, each exactly once, in their new order. Published as the
/// <c>ReorderNodesRequest</c> schema.
/// </summary>
/// <param name="OrderedIds">
/// Every live sibling of <paramref name="OrderedIds"/>' group exactly once, <c>{n}</c> included; the list index
/// becomes the node's new <c>position</c> (0-based).
/// </param>
public sealed record ReorderNodesRequest(IReadOnlyList<Guid>? OrderedIds);

/// <summary>
/// The answer of <c>POST /v1/projects/{p}/authoring/nodes/{n}/reorder</c> (13.1.1 §5 route 4): whether the call
/// moved anything. <c>false</c> means the list already described the stored order, so no version row was written
/// (AC5's no-op rule applied to the reorder). Published as the <c>ReorderNodesResponse</c> schema.
/// </summary>
/// <param name="Changed">True when at least one sibling moved, so one <c>update</c> version was appended per moved node.</param>
public sealed record ReorderNodesResponse(bool Changed);
