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

    /// <summary>503 <c>unavailable</c>: the counter lock was held past the 5 s <c>lock_timeout</c> (§4).</summary>
    internal static Error Unavailable() => new(
        ApiErrorCode.Unavailable,
        "the code counter is being changed by another request; retry shortly",
        503,
        Retryable: true);

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
}
