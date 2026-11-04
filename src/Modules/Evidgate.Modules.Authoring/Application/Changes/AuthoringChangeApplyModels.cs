namespace Evidgate.Modules.Authoring.Application.Changes;

/// <summary>
/// The body of a successful apply (13.1.2 §5): one batch, every change it applied, one version per changed entity, the
/// lineage rows it wrote and the instant the batch was stamped with.
/// </summary>
/// <param name="BatchId">The batch every version row of the call shares.</param>
/// <param name="AppliedChangeIds">The changes this call moved to <c>applied</c>, in the order they were selected.</param>
/// <param name="Versions">One entry per changed entity; empty for a comment-only batch.</param>
/// <param name="Lineage">The merge and split relations the batch wrote; always empty until the merge/split apply lands.</param>
/// <param name="CommittedAt">The batch's stamp, from the injected clock.</param>
internal sealed record ApplyAuthoringChangesResult(
    Guid BatchId,
    IReadOnlyList<Guid> AppliedChangeIds,
    IReadOnlyList<AppliedAuthoringVersion> Versions,
    IReadOnlyList<AppliedAuthoringLineage> Lineage,
    DateTimeOffset CommittedAt);

/// <summary>One version an apply appended: which entity and at which number.</summary>
/// <param name="EntityKind">The <c>authoring_version.entity_kind</c> text.</param>
/// <param name="EntityId">The entity.</param>
/// <param name="VersionNo">The version number the entity holds afterwards.</param>
internal sealed record AppliedAuthoringVersion(string EntityKind, Guid EntityId, int VersionNo);

/// <summary>One lineage row an apply wrote: the merge/split relation between two checklist items.</summary>
/// <param name="FromItemId">The item that disappeared.</param>
/// <param name="ToItemId">The item that absorbed it, or the new item it became.</param>
/// <param name="Relation"><c>merged_into</c> or <c>split_from</c>.</param>
internal sealed record AppliedAuthoringLineage(Guid FromItemId, Guid ToItemId, string Relation);

/// <summary>The body of a successful discard: the change and the instant it was abandoned.</summary>
/// <param name="ChangeId">The discarded change.</param>
/// <param name="State">Always <c>discarded</c>.</param>
/// <param name="DiscardedAt">The stamp the update wrote.</param>
internal sealed record DiscardedAuthoringChange(Guid ChangeId, string State, DateTimeOffset? DiscardedAt);
