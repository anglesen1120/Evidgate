using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Domain;

namespace Evidgate.Modules.Authoring.Application;

/// <summary>
/// The 00 §9.2 failures of the authoring write path (13.1.1 §5): two codes are this story's own
/// (<c>authoring_parent_invalid</c> 422, <c>authoring_not_empty</c> 409) and everything else reuses the shared ones, so
/// the writer and the T2 handlers answer with one vocabulary.
/// </summary>
internal static class AuthoringErrors
{
    /// <summary>422 <c>authoring_parent_invalid</c> with one of the AC1/AC3/AC8 reasons.</summary>
    /// <param name="reason">One of <see cref="PlacementReason"/>.</param>
    /// <param name="path">The offending member, e.g. <c>/content/parent_id</c>.</param>
    internal static Error ParentInvalid(string reason, string path) => new(
        ApiErrorCode.AuthoringParentInvalid,
        "the entity cannot sit there in the feature tree",
        422,
        Retryable: false,
        Details: [new ErrorDetail(path, reason)]);

    /// <summary>409 <c>authoring_not_empty</c> with one of the AC7 reasons.</summary>
    /// <param name="reason"><c>node_has_children</c>, <c>node_has_items</c> or <c>item_has_cases</c>.</param>
    /// <param name="path">The offending member, e.g. <c>/id</c>.</param>
    internal static Error NotEmpty(string reason, string path) => new(
        ApiErrorCode.AuthoringNotEmpty,
        "this entry still holds content inside",
        409,
        Retryable: false,
        Details: [new ErrorDetail(path, reason)]);

    /// <summary>409 <c>invalid_transition</c>: a state move, or an edit of a terminal entity, §6 refuses.</summary>
    /// <param name="message">A secret-free summary.</param>
    internal static Error InvalidTransition(string message) => Error.Conflict(ApiErrorCode.InvalidTransition, message);

    /// <summary>422 <c>request_invalid</c> with one reason: a shape problem of the caller's own request.</summary>
    /// <param name="path">The offending member.</param>
    /// <param name="reason">The stable machine reason.</param>
    /// <param name="message">A secret-free summary.</param>
    internal static Error Invalid(string path, string reason, string message) =>
        Error.Invalid(message, [new ErrorDetail(path, reason)]);

    /// <summary>422 <c>request_invalid</c> over several members (the AC7 retire reason, an invalid snapshot).</summary>
    /// <param name="message">A secret-free summary.</param>
    /// <param name="details">One entry per offending member.</param>
    internal static Error InvalidMany(string message, IReadOnlyList<ErrorDetail> details) => Error.Invalid(message, details);

    /// <summary>404 <c>not_found</c>: no such entity, version or target in this project.</summary>
    /// <param name="message">A secret-free summary.</param>
    internal static Error NotFound(string message) => Error.NotFound(message);

    /// <summary>409 <c>quota_exceeded</c>: the project has allocated its 10⁹th manual case code (§4).</summary>
    internal static Error QuotaExceeded() => Error.Conflict(
        ApiErrorCode.QuotaExceeded,
        "this project has used every manual case code it can allocate");

    /// <summary>
    /// 503 <c>unavailable</c>: another writer held what this request needed past the 5 s <c>lock_timeout</c> (§4) — the
    /// manual case code counter, a sibling group's advisory lock, a row this command had to lock, or a deadlock between
    /// any of them — so the answer names the contention, not one particular resource.
    /// </summary>
    internal static Error Unavailable() => new(
        ApiErrorCode.Unavailable,
        "the resource is being changed by another request; retry shortly",
        503,
        Retryable: true);

    /// <summary>
    /// True when a request-facing writer failure is AC2's <c>position_taken</c> on the route's own <c>/position</c>
    /// member: the one refusal that can be a lost race rather than a fault of the request, when the handler chose the
    /// position instead of the caller (review NEW-2).
    /// </summary>
    /// <param name="error">A failure already rebased by <c>RequestFacing</c>.</param>
    internal static bool IsPositionTaken(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return string.Equals(error.Code, ApiErrorCode.RequestInvalid, StringComparison.Ordinal)
            && error.Details is { Count: > 0 } details
            && details.Any(detail => string.Equals(detail.Path, "/position", StringComparison.Ordinal)
                && string.Equals(detail.Reason, "position_taken", StringComparison.Ordinal));
    }

    /// <summary>404 for an entity that is gone or belongs to another project (AC10's indistinguishability).</summary>
    /// <param name="kind">Which entity was addressed.</param>
    internal static Error EntityNotFound(Contracts.AuthoringEntityKind kind) => NotFound(
        kind switch
        {
            Contracts.AuthoringEntityKind.FeatureNode => "no feature node with that id is visible in this project",
            Contracts.AuthoringEntityKind.ChecklistItem => "no checklist item with that id is visible in this project",
            Contracts.AuthoringEntityKind.ManualTestCase => "no manual test case with that id is visible in this project",
            _ => "no such entity is visible in this project",
        });

    /// <summary>The failure of a <see cref="Result{T}"/> the caller must propagate unchanged.</summary>
    /// <typeparam name="T">The failed value's type.</typeparam>
    /// <param name="error">The failure.</param>
    internal static Result<T> Fail<T>(Error error) => Result.Failure<T>(error);

    /// <summary>
    /// The writer's refusal of a <b>single-change</b> batch as the route's own refusal: the writer names each change's
    /// members under its batch envelope (<c>/changes/0/content/position</c>) because it accepts several changes, while the
    /// routes of 13.1.1 §5 carry exactly one member each (<c>/position</c> — §5 route 2, AC1, AC2). Every detail whose
    /// path starts with the prefix therefore drops it, and <paramref name="memberNames"/> renames a stored column to the
    /// request member it came from (the checklist item's <c>feature_node_id</c> column is route 8's <c>node_id</c>).
    /// </summary>
    /// <remarks>
    /// Only the <c>/changes/{i}/content/</c> prefix is rebased: a path that names the change itself (for example the
    /// AC7 <c>409</c> of a delete) is the writer's own envelope and is left as it is.
    /// </remarks>
    /// <param name="error">The writer's failure.</param>
    /// <param name="memberNames">Stored-member → request-member renames, or null.</param>
    /// <returns>The failure with its content-member paths rebased.</returns>
    internal static Error RequestFacing(Error error, IReadOnlyDictionary<string, string>? memberNames = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (error.Details is not { Count: > 0 } details)
        {
            return error;
        }

        List<ErrorDetail>? rebased = null;

        for (var index = 0; index < details.Count; index++)
        {
            var detail = details[index];

            if (!detail.Path.StartsWith(ContentPrefix, StringComparison.Ordinal))
            {
                rebased?.Add(detail);
                continue;
            }

            var member = detail.Path[ContentPrefix.Length..];
            if (memberNames is not null && memberNames.TryGetValue(member, out var requestMember))
            {
                member = requestMember;
            }

            // The first rebased detail is the signal: from there on every row is rebuilt, so a refusal that mixes a
            // rebased member with an envelope path keeps both in their original order.
            rebased ??= [.. details.Take(index)];
            rebased.Add(new ErrorDetail("/" + member, detail.Reason, detail.Index));
        }

        return rebased is null ? error : error with { Details = rebased };
    }

    /// <summary>The writer's own prefix for a member of the change at <c>index</c> of a batch.</summary>
    private const string ContentPrefix = "/changes/0/content/";
}
