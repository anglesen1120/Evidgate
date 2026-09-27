using System.Diagnostics.CodeAnalysis;
using Evidgate.BuildingBlocks.SharedKernel.Api;

namespace Evidgate.BuildingBlocks.SharedKernel.Results;

/// <summary>
/// The application/domain outcome of a command or query (00 §5.5 C8): failures are
/// values, not exceptions — exceptions stay reserved for bugs and infrastructure
/// faults. The endpoint filter maps a <see cref="Result"/> once to the 00 §9.2
/// envelope, and the C2 pipeline's tenant-bound stages (tenant transaction,
/// authorization, idempotency) run for messages whose response is this contract,
/// which is why the type is also the pipeline's "business message" marker.
/// </summary>
public class Result
{
    private protected Result(bool isSuccess, int status, Error? error, object? successValue)
    {
        IsSuccess = isSuccess;
        Status = status;
        Error = error;
        SuccessValue = successValue;
    }

    /// <summary>True when the operation succeeded; then <see cref="Error"/> is null.</summary>
    public bool IsSuccess { get; }

    /// <summary>The failure, or null on success.</summary>
    public Error? Error { get; }

    /// <summary>
    /// The HTTP status the endpoint filter emits (00 §9.2). A command that creates
    /// or accepts work sets 201/202 here instead of the default 200, so the status
    /// and the body it belongs to always travel together — the idempotency
    /// behaviour stores both and replays them verbatim (00 §9.1).
    /// </summary>
    public int Status { get; }

    /// <summary>
    /// The success body, boxed for the endpoint filter (<see cref="ResultFactory"/>
    /// reads it when the idempotency behaviour stores a response); null for the
    /// bodiless <see cref="Result"/>.
    /// </summary>
    public object? SuccessValue { get; }

    /// <summary>A bodiless success (e.g. a cancel request answered 202), or 200 by default.</summary>
    public static Result Success(int status = 200) => new(true, status, null, null);

    /// <summary>A failure carrying the error the endpoint filter renders.</summary>
    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return new(false, error.Status, error, null);
    }

    /// <summary>Success with <paramref name="value"/> as the body, 201/202 when the command creates work.</summary>
    public static Result<T> Success<T>(T value, int status = 200) => new(value, status);

    /// <summary>A failure of a body-carrying response type.</summary>
    public static Result<T> Failure<T>(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return new(error);
    }
}

/// <summary>
/// A <see cref="Result"/> that carries the success body of a command or query
/// (00 §5.5 C8) — the shape every business handler returns. Built through
/// <see cref="Result.Success{T}"/>/<see cref="Result.Failure{T}(Error)"/>.
/// </summary>
/// <typeparam name="T">Type of the success body.</typeparam>
public sealed class Result<T> : Result
{
    internal Result(T value, int status)
        : base(true, status, null, value) => Value = value;

    internal Result(Error error)
        : base(false, error.Status, error, null) => Value = default!;

    /// <summary>The success body; never read on a failure.</summary>
    public T Value { get; }
}

/// <summary>
/// The 00 §9.2 failure an operation returns: the stable machine <see cref="Code"/>,
/// a secret-free <see cref="Message"/>, the HTTP <see cref="Status"/> the endpoint
/// filter emits, and per-field <see cref="Details"/>. A status the factories below
/// do not cover (404, 412, 413, 429, 503 …) is a plain <see cref="Error"/> value.
/// </summary>
/// <param name="Code">Stable machine code, e.g. <c>request_invalid</c>.</param>
/// <param name="Message">Human-readable, secret-free summary.</param>
/// <param name="Status">HTTP status of the mapped envelope.</param>
/// <param name="Retryable">True only when the same request may be retried unchanged.</param>
/// <param name="Details">Per-field or per-item reasons; never secrets.</param>
// CA1716 names the VB keyword collision; the contract (01.4.2 §9.2, C8) names this
// type `Error`, and it only ever sits inside the ErrorEnvelope the wire already
// carries — the VB caller cost is accepted and isolated.
[SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "The C8 contract names this type Error; the VB keyword collision is accepted for the single type.")]
public sealed record Error(
    string Code,
    string Message,
    int Status,
    bool Retryable = false,
    IReadOnlyList<ErrorDetail>? Details = null)
{
    /// <summary>422: the request parsed but breaks the command's rules (the FluentValidation stage).</summary>
    public static Error Invalid(string message, IReadOnlyList<ErrorDetail>? details = null) =>
        new(ApiErrorCode.RequestInvalid, message, 422, false, details);

    /// <summary>400: the request itself is malformed — an unreadable body, an over-long <c>Idempotency-Key</c>.</summary>
    public static Error BadRequest(string message, IReadOnlyList<ErrorDetail>? details = null) =>
        new(ApiErrorCode.RequestInvalid, message, 400, false, details);

    /// <summary>403: the principal does not hold the handler's permission, or the handler declares none.</summary>
    public static Error Forbidden(string message) => new(ApiErrorCode.Forbidden, message, 403);

    /// <summary>
    /// 403 with an explicit 00 §9.2 code: the C2 authorization stage's other two
    /// denials, <c>project_scope_mismatch</c> (the request addresses a project
    /// outside the principal's token scope, 00 §9.1) and <c>no_membership</c> (the
    /// principal has no active membership in the org it addressed, 02 §4.3).
    /// </summary>
    public static Error Denied(string code, string message) => new(code, message, 403);

    /// <summary>409: a conflict with the caller's own earlier request, e.g. a reused <c>Idempotency-Key</c>.</summary>
    public static Error Conflict(string code, string message) => new(code, message, 409);

    /// <summary>404: the addressed resource does not exist (also for another tenant's id, 00 §9.2).</summary>
    public static Error NotFound(string message) => new(ApiErrorCode.NotFound, message, 404);

    /// <summary>412: the row changed since the caller read it (the <c>version</c> precondition, 00 §5.5 C4).</summary>
    public static Error PreconditionFailed(string message) => new(ApiErrorCode.PreconditionFailed, message, 412);

    /// <summary>
    /// 422 with an explicit 00 §9.2 code — the domain codes a validator cannot express
    /// through <see cref="Invalid"/> (<c>unknown_permission</c>,
    /// <c>permission_not_assignable</c>, …), still parsed-but-refused rather than
    /// malformed.
    /// </summary>
    public static Error Unprocessable(string code, string message, IReadOnlyList<ErrorDetail>? details = null) =>
        new(code, message, 422, false, details);
}
