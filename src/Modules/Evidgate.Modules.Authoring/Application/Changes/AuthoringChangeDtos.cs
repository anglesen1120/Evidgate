using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Evidgate.Modules.Authoring.Application.Changes;

/// <summary>The 201 body of the queue route: the change's identity, its state and the version it was based on.</summary>
/// <param name="ChangeId">The new change's id.</param>
/// <param name="State">Always <c>pending</c> on this answer.</param>
/// <param name="BaseVersion">The target item's version when the change was queued.</param>
/// <param name="CreatedAt">The store's instant.</param>
internal sealed record QueuedAuthoringChangeResponse(
    Guid ChangeId,
    string State,
    int BaseVersion,
    DateTimeOffset CreatedAt,
    [property: JsonIgnore] int Version = 1)
{
    /// <summary>The new row's own version as the response <c>ETag</c> value, served in the header only.</summary>
    internal string Etag => Version.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// One change as both read routes serve it. <see cref="Payload"/> is the stored document, parsed but never
/// re-shaped; <see cref="Stale"/> is present only on a selection read, where the server knows the version each
/// affected item had when the change was queued.
/// </summary>
/// <param name="ChangeId">The change's id.</param>
/// <param name="TargetKind">Always <c>checklist_item</c> today.</param>
/// <param name="TargetId">The target item, or the merge's survivor.</param>
/// <param name="ChangeType">One of <c>comment</c>, <c>delete</c>, <c>merge</c>, <c>split</c>.</param>
/// <param name="Payload">The queued document.</param>
/// <param name="State">One of <c>pending</c>, <c>applied</c>, <c>discarded</c>.</param>
/// <param name="Source">Who proposed it: <c>manual</c>, <c>ai</c> or <c>import</c>.</param>
/// <param name="BaseVersion">The target's version when queued.</param>
/// <param name="AuthorMembershipId">The membership that queued it.</param>
/// <param name="Reason">The caller's optional reason.</param>
/// <param name="CreatedAt">When it was queued.</param>
/// <param name="AppliedAt">When it was applied, or null.</param>
/// <param name="AppliedBatchId">The batch it was applied in, or null.</param>
/// <param name="DiscardedAt">When it was discarded, or null.</param>
/// <param name="Stale">
/// True when any affected item's version moved after queueing; null when the read was not a selection, because
/// only the selection read resolves the affected set to current versions.
/// </param>
internal sealed record AuthoringChangeListItem(
    Guid ChangeId,
    string TargetKind,
    Guid TargetId,
    string ChangeType,
    JsonElement Payload,
    string State,
    string Source,
    int BaseVersion,
    Guid AuthorMembershipId,
    string? Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AppliedAt,
    Guid? AppliedBatchId,
    DateTimeOffset? DiscardedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Stale);

/// <summary>A keyset page of changes, with the batch ETag a selection read carries.</summary>
/// <param name="Items">The page's rows, ordered <c>(created_at, id)</c> ascending.</param>
/// <param name="NextCursor">The cursor of the next page, or null on the last one.</param>
/// <param name="Etag">The batch ETag over the selection's affected items, or null for a filtered page.</param>
internal sealed record AuthoringChangePageResponse(
    IReadOnlyList<AuthoringChangeListItem> Items,
    string? NextCursor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Etag);

/// <summary>The detail route's body: the same change, plus its own lifecycle version for the <c>ETag</c>.</summary>
/// <param name="ChangeId">The change's id.</param>
/// <param name="TargetKind">Always <c>checklist_item</c> today.</param>
/// <param name="TargetId">The target item, or the merge's survivor.</param>
/// <param name="ChangeType">One of the four change types.</param>
/// <param name="Payload">The queued document.</param>
/// <param name="State">Its state.</param>
/// <param name="Source">Who proposed it.</param>
/// <param name="BaseVersion">The target's version when queued.</param>
/// <param name="AuthorMembershipId">The membership that queued it.</param>
/// <param name="Reason">The caller's optional reason.</param>
/// <param name="CreatedAt">When it was queued.</param>
/// <param name="AppliedAt">When it was applied, or null.</param>
/// <param name="AppliedBatchId">The batch it was applied in, or null.</param>
/// <param name="DiscardedAt">When it was discarded, or null.</param>
/// <param name="Version">The row's lifecycle version.</param>
internal sealed record AuthoringChangeDetailResponse(
    Guid ChangeId,
    string TargetKind,
    Guid TargetId,
    string ChangeType,
    JsonElement Payload,
    string State,
    string Source,
    int BaseVersion,
    Guid AuthorMembershipId,
    string? Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AppliedAt,
    Guid? AppliedBatchId,
    DateTimeOffset? DiscardedAt,
    int Version)
{
    /// <summary>That version as the response <c>ETag</c>, never part of the body.</summary>
    [JsonIgnore]
    internal string Etag => AuthoringChangeJson.Etag(Version);
}
