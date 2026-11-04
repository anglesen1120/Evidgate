using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Domain.Changes;

namespace Evidgate.Modules.Authoring.Application.Changes;

/// <summary>
/// The queue route's own failures: the two 409s that are not about the change's state machine but about a
/// neighbour (a pending structural change already covers an item) and about the project's pending budget.
/// Everything else reuses <see cref="AuthoringErrors"/> so one module keeps one vocabulary.
/// </summary>
internal static class AuthoringChangeErrors
{
    /// <summary>
    /// 409 <c>invalid_transition</c> with <c>change_already_pending</c>: any item of the new change's affected set
    /// already sits in a pending <c>delete</c>/<c>merge</c>/<c>split</c>. The handler's unlocked pre-check and the
    /// guard trigger's <c>23514</c> answer this same body.
    /// </summary>
    internal static Error ChangeAlreadyPending() => new(
        ApiErrorCode.InvalidTransition,
        "another structural change is already pending for one of these items",
        409,
        Retryable: false,
        Details: [new ErrorDetail("/target_id", "change_already_pending")]);

    /// <summary>
    /// 409 <c>invalid_transition</c> with <c>change_not_pending</c>: the change already left <c>pending</c>, so a
    /// discard or an apply cannot move it again.
    /// </summary>
    internal static Error ChangeNotPending() => new(
        ApiErrorCode.InvalidTransition,
        "this change is no longer pending",
        409,
        Retryable: false,
        Details: [new ErrorDetail("/state", "change_not_pending")]);

    /// <summary>
    /// 429 <c>quota_exceeded</c> when the project already holds the pending limit. The host's Result filter adds the
    /// <c>Retry-After</c> every 429 carries; the detail names the budget rather than echoing a count that a
    /// concurrent apply can change before the client reads it.
    /// </summary>
    internal static Error PendingLimitReached() => new(
        ApiErrorCode.QuotaExceeded,
        string.Create(
            CultureInfo.InvariantCulture,
            $"the project already holds {AuthoringChangeLimits.PendingMax} pending changes; apply or discard some first"),
        429,
        Retryable: true,
        Details: [new ErrorDetail("/pending", "reached")]);

    /// <summary>422 <c>payload_invalid</c> for a member of the payload the change type does not allow.</summary>
    /// <param name="path">The JSON pointer of the offending member.</param>
    internal static Error PayloadInvalid(string path) =>
        AuthoringErrors.Invalid(path, "payload_invalid", "the change payload is invalid");

    /// <summary>422 <c>target_deleted</c>: the target (or a payload item) is not a live item of this project.</summary>
    /// <param name="path">The member that named the item.</param>
    internal static Error TargetDeleted(string path) =>
        AuthoringErrors.Invalid(path, "target_deleted", "the item was deleted");

    /// <summary>422 <c>ids_with_filters</c>: a selection read names no other filter.</summary>
    internal static Error IdsWithFilters() =>
        AuthoringErrors.Invalid("/ids", "ids_with_filters", "ids cannot be combined with other filters");
}
