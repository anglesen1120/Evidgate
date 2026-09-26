namespace Evidgate.BuildingBlocks.SharedKernel.Api;

/// <summary>
/// The 00 §9.2 error envelope: every <c>/v1</c> failure body is
/// <c>{"error": {code, message, request_id, retryable, details}}</c> and never
/// <c>ProblemDetails</c> (00 §5.5 C8). Field names are lower_snake_case on the
/// wire through <see cref="Serialization.EvidgateJsonContext"/>.
/// </summary>
public sealed record ErrorEnvelope(ErrorBody Error);

/// <summary>The body of the 00 §9.2 envelope.</summary>
/// <param name="Code">Stable machine code, e.g. <c>request_invalid</c>, <c>manifest_invalid</c>.</param>
/// <param name="Message">Human-readable, secret-free summary.</param>
/// <param name="RequestId">Echo of <c>X-Request-Id</c> (00 §9.1).</param>
/// <param name="Retryable">True only when the same request may be retried unchanged.</param>
/// <param name="Details">Per-field or per-item reasons; never contains secrets (00 §9.2).</param>
public sealed record ErrorBody(
    string Code,
    string Message,
    string RequestId,
    bool Retryable,
    IReadOnlyList<ErrorDetail> Details);

/// <summary>One entry of <see cref="ErrorBody.Details"/> (00 §9.2 example shape).</summary>
/// <param name="Path">JSON pointer or field name the reason belongs to.</param>
/// <param name="Reason">Machine-readable reason, e.g. <c>required</c>.</param>
public sealed record ErrorDetail(string Path, string Reason);

/// <summary>Stable error codes of the 00 §9.2 envelope, shared by the skeleton slice, the C2 pipeline's Result mapping and the endpoint filter.</summary>
public static class ApiErrorCode
{
    /// <summary>The request itself is malformed or misses a required field.</summary>
    public const string RequestInvalid = "request_invalid";

    /// <summary>The manifest could not be accepted (00 §9.2 example code).</summary>
    public const string ManifestInvalid = "manifest_invalid";

    /// <summary>The addressed resource does not exist (or belongs to another tenant).</summary>
    public const string NotFound = "not_found";

    /// <summary>No authenticated principal could be resolved (00 §9.2: 401 unauthenticated).</summary>
    public const string Unauthenticated = "unauthenticated";

    /// <summary>The principal does not hold the operation's permission (00 §9.2: 403 forbidden).</summary>
    public const string Forbidden = "forbidden";

    /// <summary>An <c>Idempotency-Key</c> was reused for a different request (00 §9.1/§9.2: 409).</summary>
    public const string IdempotencyConflict = "idempotency_conflict";


    /// <summary>The route exists but not for this HTTP method (00 §9.2 does not name 405).</summary>
    public const string MethodNotAllowed = "method_not_allowed";

    /// <summary>The request body exceeds the endpoint's limit (00 §9.2: 413 too large).</summary>
    public const string PayloadTooLarge = "payload_too_large";

    /// <summary>An unexpected failure; carries no internal detail (00 §5.5 C8).</summary>
    public const string InternalError = "internal_error";

    /// <summary>No task row exists with that id (01.5.1 §5 operator replay: 404).</summary>
    public const string TaskNotFound = "task_not_found";

    /// <summary>The row exists but is not `dead_letter` (01.5.1 §5 operator replay: 409).</summary>
    public const string TaskNotDeadLetter = "task_not_dead_letter";
}
