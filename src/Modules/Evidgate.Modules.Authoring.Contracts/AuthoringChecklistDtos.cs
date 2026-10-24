namespace Evidgate.Modules.Authoring.Contracts;

/// <summary>
/// One checklist item as the authoring routes of 13.1.1 §5 echo it: the stored row of
/// <c>checklist_item</c>, including the <c>source</c> and <c>version</c> the client needs to send
/// back as <c>If-Match</c>.
/// </summary>
/// <param name="ItemId">The item's id.</param>
/// <param name="NodeId">The category or sub-category node the item hangs under (AC3).</param>
/// <param name="Title">The title, 1..500 characters.</param>
/// <param name="Description">The optional description, at most 4 000 characters.</param>
/// <param name="Priority">One of <c>critical</c>, <c>high</c>, <c>medium</c>, <c>low</c> (AC3).</param>
/// <param name="State">One of the §6 states <c>draft</c>, <c>active</c>, <c>retired</c>.</param>
/// <param name="Source">Where the item came from: <c>manual</c>, <c>ai</c> or <c>import</c>.</param>
/// <param name="Position">The item's 0-based position inside its node.</param>
/// <param name="Version">The row's version, which an edit or a delete sends as <c>If-Match</c> (AC19).</param>
public sealed record ChecklistItemDto(
    Guid ItemId,
    Guid NodeId,
    string Title,
    string? Description,
    string Priority,
    string State,
    string Source,
    int Position,
    int Version);

/// <summary>A keyset page of checklist items, ordered <c>(position, id)</c> (13.1.1 §5 route 7, AC2's order).</summary>
/// <param name="Items">The page's rows.</param>
/// <param name="NextCursor">The cursor of the next page, or null when this is the last one.</param>
public sealed record ChecklistItemListResponse(IReadOnlyList<ChecklistItemDto> Items, string? NextCursor);

/// <summary>
/// Body of <c>POST …/authoring/checklist-items</c> (13.1.1 §5 route 8). The item is created <c>active</c> and
/// <c>source = manual</c> (AC3), so neither is a member.
/// </summary>
/// <param name="NodeId">The live <c>category</c> or <c>sub_category</c> node of the same project.</param>
/// <param name="Title">The title, 1..500 characters.</param>
/// <param name="Description">The optional description, at most 4 000 characters.</param>
/// <param name="Priority">One of <c>critical</c>, <c>high</c>, <c>medium</c>, <c>low</c>.</param>
/// <param name="Position">The optional position; absent appends after the node's last live item.</param>
public sealed record CreateChecklistItemRequest(Guid NodeId, string Title, string? Description, string Priority, int? Position);

/// <summary>The <c>201</c> answer of route 8.</summary>
/// <param name="ItemId">The created item's id.</param>
/// <param name="Version">Its version, <c>1</c> on create, which the next edit sends as <c>If-Match</c>.</param>
/// <param name="State">Its state, <c>active</c> for a manual create (AC3).</param>
public sealed record CreateChecklistItemResponse(Guid ItemId, int Version, string State);

/// <summary>
/// Body of <c>PATCH …/authoring/checklist-items/{i}</c> (13.1.1 §5 route 9): every member is optional and an
/// absent one is left unchanged. <c>node_id</c> is deliberately **not** a member — an item does not move between
/// nodes on this route, so naming it is refused (422 <c>/node_id</c>).
/// </summary>
/// <param name="Title">The new title, 1..500 characters.</param>
/// <param name="Description">The new description; an explicit <c>null</c> clears it.</param>
/// <param name="Priority">The new priority.</param>
/// <param name="Position">The new position (0 or more).</param>
/// <param name="State">The new state, which must be a §6 move from the current one (AC20).</param>
/// <param name="Reason">The 1..500 character reason a move into <c>retired</c> requires (AC20).</param>
public sealed record UpdateChecklistItemRequest(
    string? Title,
    string? Description,
    string? Priority,
    int? Position,
    string? State,
    string? Reason);

/// <summary>
/// Body of <c>DELETE …/authoring/checklist-items/{i}</c> (13.1.1 §5 route 10): the whole body is optional, and the
/// only member is the reason recorded on the soft-delete version.
/// </summary>
/// <param name="Reason">The optional 1..500 character reason.</param>
public sealed record DeleteChecklistItemRequest(string? Reason);
