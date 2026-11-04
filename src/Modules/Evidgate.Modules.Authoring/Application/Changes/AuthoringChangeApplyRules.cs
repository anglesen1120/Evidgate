using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Domain.Changes;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;

namespace Evidgate.Modules.Authoring.Application.Changes;

/// <summary>
/// The refusals and the two wire helpers of the batched apply (13.1.2 §5, §6): the selection's own shape, the batch
/// ETag, and the one 412/409 vocabulary every precondition answer uses.
/// </summary>
internal static class AuthoringChangeApplyRules
{
    /// <summary>
    /// The first refusal of <c>change_ids</c> itself, or null: the member must carry 1..<see cref="AuthoringChangeLimits.SelectionMax"/>
    /// distinct uuids. A repeat is refused rather than de-duplicated, because the caller's ETag was computed over the
    /// selection it read.
    /// </summary>
    /// <param name="changeIds">The body's <c>change_ids</c>.</param>
    internal static Error? SelectionError(IReadOnlyList<Guid>? changeIds)
    {
        if (changeIds is not { Count: > 0 })
        {
            return AuthoringErrors.Invalid("/change_ids", "required", "change_ids must carry at least one change");
        }

        if (changeIds.Count > AuthoringChangeLimits.SelectionMax)
        {
            return AuthoringErrors.Invalid(
                "/change_ids",
                "too_many_changes",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"at most {AuthoringChangeLimits.SelectionMax} changes can be applied at once"));
        }

        var seen = new HashSet<Guid>();
        for (var index = 0; index < changeIds.Count; index++)
        {
            if (!seen.Add(changeIds[index]))
            {
                return AuthoringErrors.Invalid(
                    string.Create(CultureInfo.InvariantCulture, $"/change_ids/{index}"),
                    "payload_invalid",
                    "a change id appears twice in the selection");
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>If-Match</c> tag as a bare value: the header may arrive quoted and weakly quoted, and an absent or blank
    /// tag is null, which the route answers as the missing precondition it is.
    /// </summary>
    /// <param name="raw">The header value as the endpoint read it.</param>
    internal static string? NormalizeTag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..].TrimStart();
        }

        value = value.Trim('"');

        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// The batch ETag of a locked affected set: the order hash of every affected item with its <b>current</b> version,
    /// deduplicated by id, through the same helper the reorder precondition uses, so one scheme serves both.
    /// </summary>
    /// <param name="items">The locked affected items.</param>
    internal static string BatchEtag(IEnumerable<AuthoringAffectedItemRow> items) =>
        AuthoringHashes.OrderHash(items.Select(item => (item.Id, item.Version)));

    /// <summary>422 when the header is absent: the body names the header, never a 428.</summary>
    internal static Error MissingIfMatch() => AuthoringErrors.Invalid(
        "/If-Match",
        "required",
        "If-Match is required: send the batch ETag the selection was read with");

    /// <summary>412 when the tag the caller sent is not the tag the server recomputes.</summary>
    internal static Error BatchStale() => new(
        ApiErrorCode.PreconditionFailed,
        "the pending changes moved since the selection was read; reload and apply again",
        412,
        Retryable: false,
        Details: [new ErrorDetail("/If-Match", "batch_stale")]);

    /// <summary>412 for an affected item whose version moved after the change was queued.</summary>
    /// <param name="itemId">The item whose version moved.</param>
    internal static Error TargetChanged(Guid itemId) => new(
        ApiErrorCode.PreconditionFailed,
        "an affected checklist item changed since the change was queued; reload and apply again",
        412,
        Retryable: false,
        Details: [new ErrorDetail(ItemPath(itemId), "target_changed")]);

    /// <summary>412 for an affected item that is gone or soft-deleted since the change was queued.</summary>
    /// <param name="itemId">The item that disappeared.</param>
    internal static Error TargetDeleted(Guid itemId) => new(
        ApiErrorCode.PreconditionFailed,
        "an affected checklist item no longer exists; reload and apply again",
        412,
        Retryable: false,
        Details: [new ErrorDetail(ItemPath(itemId), "target_deleted")]);

    /// <summary>409 for a selected change that already left <c>pending</c>.</summary>
    /// <param name="index">The change's position in the caller's <c>change_ids</c>.</param>
    internal static Error ChangeNotPending(int index) => Error.Conflict(
        ApiErrorCode.InvalidTransition,
        "a selected change is no longer pending",
        [new ErrorDetail(string.Create(CultureInfo.InvariantCulture, $"/change_ids/{index}"), "change_not_pending")]);

    /// <summary>
    /// 409 for a flip that moved fewer rows than it was handed. The rows were locked before it ran, so nothing outside
    /// this transaction can move them: the answer names no index because no index is knowable, and fails closed.
    /// </summary>
    internal static Error ChangeNotPending() => Error.Conflict(
        ApiErrorCode.InvalidTransition,
        "a selected change is no longer pending",
        [new ErrorDetail("/change_ids", "change_not_pending")]);

    /// <summary>
    /// 422 for a selected change this route cannot apply. Merge and split execute their own effect list, which is a
    /// separate unit of work; refusing the whole batch keeps "every selected change is applied" true.
    /// </summary>
    /// <param name="index">The change's position in the caller's <c>change_ids</c>.</param>
    internal static Error ChangeNotApplicable(int index) => AuthoringErrors.Invalid(
        string.Create(CultureInfo.InvariantCulture, $"/change_ids/{index}"),
        "payload_invalid",
        "a selected change cannot be applied by this route");

    /// <summary>409 when a soft-delete target acquired a live manual case after its change was queued.</summary>
    /// <param name="itemId">The item that still holds cases.</param>
    internal static Error ItemHasCases(Guid itemId) => AuthoringErrors.NotEmpty("item_has_cases", ItemPath(itemId));

    /// <summary>A detail path naming an entity: the item id itself, as the plan's 412 rows carry it.</summary>
    /// <param name="itemId">The entity id.</param>
    private static string ItemPath(Guid itemId) => itemId.ToString("D");
}

/// <summary>One selected change with the affected set it was stamped with at queue time.</summary>
/// <param name="Row">The locked change row.</param>
/// <param name="Affected">The item id → version pairs the queue route stamped.</param>
internal sealed record PlannedAuthoringChange(AuthoringChangeRow Row, IReadOnlyDictionary<Guid, int> Affected);

/// <summary>
/// What one apply will write, read entirely from the locked rows: the affected items it must lock and check, the entity
/// changes it hands the version writer, the soft-delete targets the non-empty guard re-checks, and the first selected
/// change whose own effect list this route does not execute.
/// </summary>
/// <param name="Changes">The selection, in the order the locked statement answered it.</param>
/// <param name="AffectedItemIds">The union of every change's affected items, ascending.</param>
/// <param name="Writes">One entity change per changed entity; empty for a comment-only batch.</param>
/// <param name="DeleteTargets">The items the batch soft-deletes.</param>
/// <param name="NotApplicableIndex">The first selected change this route cannot apply, or -1.</param>
internal sealed record AuthoringChangePlan(
    IReadOnlyList<PlannedAuthoringChange> Changes,
    IReadOnlyList<Guid> AffectedItemIds,
    IReadOnlyList<EntityChange> Writes,
    IReadOnlyList<Guid> DeleteTargets,
    int NotApplicableIndex);

/// <summary>The effect matrix of 13.1.2 §6 as a pure plan: which entities one selection changes, and nothing else.</summary>
internal static class AuthoringChangePlanning
{
    /// <summary>Builds the plan of a locked selection.</summary>
    /// <param name="rows">The locked change rows, in <c>(target_id, id)</c> order.</param>
    internal static AuthoringChangePlan Plan(IReadOnlyList<AuthoringChangeRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var changes = new List<PlannedAuthoringChange>(rows.Count);
        var affectedIds = new SortedSet<Guid>();
        var writes = new List<EntityChange>();
        var deleteTargets = new SortedSet<Guid>();
        var notApplicable = -1;

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var affected = ReadAffected(row);
            changes.Add(new PlannedAuthoringChange(row, affected));

            foreach (var itemId in affected.Keys)
            {
                affectedIds.Add(itemId);
            }

            switch (row.ChangeType)
            {
                case AuthoringChangeTypes.Comment:
                    // A comment writes no version and edits no item, so it never invalidates an editor's If-Match.
                    break;

                case AuthoringChangeTypes.Delete:
                    writes.Add(EntityChange.SoftDelete(AuthoringEntityKind.ChecklistItem, row.TargetId, row.Reason));
                    deleteTargets.Add(row.TargetId);
                    break;

                default:
                    if (notApplicable < 0)
                    {
                        notApplicable = index;
                    }

                    break;
            }
        }

        return new AuthoringChangePlan([.. changes], [.. affectedIds], writes, [.. deleteTargets], notApplicable);
    }

    /// <summary>
    /// The affected set a change was stamped with. A row whose set is unreadable — not an object, an id that is not a
    /// uuid, a version below 1 — is a stored-value fault, so it refuses the whole batch instead of guessing.
    /// </summary>
    /// <param name="row">The locked change row.</param>
    private static Dictionary<Guid, int> ReadAffected(AuthoringChangeRow row)
    {
        var affected = new Dictionary<Guid, int>();

        using var document = JsonDocument.Parse(row.AffectedVersions);
        if (document.RootElement.ValueKind is not JsonValueKind.Object)
        {
            throw Corrupt(row);
        }

        foreach (var member in document.RootElement.EnumerateObject())
        {
            if (!Guid.TryParse(member.Name, out var itemId)
                || member.Value.ValueKind is not JsonValueKind.Number
                || !member.Value.TryGetInt32(out var version)
                || version < 1)
            {
                throw Corrupt(row);
            }

            affected[itemId] = version;
        }

        if (affected.Count == 0 || (IsDelete(row) && !affected.ContainsKey(row.TargetId)))
        {
            throw Corrupt(row);
        }

        return affected;
    }

    /// <summary>True for the one change type whose own target is part of its affected set.</summary>
    /// <param name="row">The locked change row.</param>
    private static bool IsDelete(AuthoringChangeRow row) =>
        string.Equals(row.ChangeType, AuthoringChangeTypes.Delete, StringComparison.Ordinal);

    /// <summary>
    /// The failure of a stored affected set nothing can repair: the queue route validates the set before it inserts, so
    /// reaching this means the row or the code that wrote it is wrong, and a partial apply would be worse than a 500.
    /// </summary>
    /// <param name="row">The locked change row.</param>
    private static InvalidOperationException Corrupt(AuthoringChangeRow row) => new(
        string.Create(CultureInfo.InvariantCulture, $"authoring change {row.Id:D} has an unreadable affected set"));
}
