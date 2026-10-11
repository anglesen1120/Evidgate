using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The 422 refusals of the artifact mint command (00 §9.2, 06.1.1 §5). Each carries its own 00 §9.2 code through
/// <see cref="Error.Unprocessable"/> — the shape a validator cannot express (the
/// <c>Error.Unprocessable</c> contract: "the domain codes a validator cannot express through
/// <c>Invalid</c>") — so the command returns them as <c>Result</c> errors (C8) and 06.1.2's
/// <c>POST /v1/artifacts/upload-url</c> surfaces them to HTTP clients. Messages never echo the client-supplied
/// value back, so a hostile key or checksum cannot travel into a log or an envelope.
/// </summary>
internal static class EvidenceErrors
{
    /// <summary>422: the command carried a client-supplied object key; keys are server-chosen only (AC1, FR-06-1).</summary>
    internal static Error ObjectKeyNotAllowed => Error.Unprocessable(
        ApiErrorCode.ObjectKeyNotAllowed,
        "the object key is chosen by the server; a client-supplied object key is not accepted",
        [new ErrorDetail("client_object_key", "not_allowed")]);

    /// <summary>422: the artifact <c>kind</c> is outside the 00 §7 closed list (06.1.1 §5).</summary>
    internal static Error KindUnknown => Error.Unprocessable(
        ApiErrorCode.KindUnknown,
        "the artifact kind is not one of the 00 §7 kinds",
        [new ErrorDetail("kind", "unknown")]);

    /// <summary>422: the declared checksum is not <c>sha256:&lt;64 lowercase hex&gt;</c> (06.1.1 §5).</summary>
    internal static Error ChecksumInvalid => Error.Unprocessable(
        ApiErrorCode.ChecksumInvalid,
        "the checksum must be sha256:<64 lowercase hex>",
        [new ErrorDetail("checksum", "invalid_format")]);

    /// <summary>422: the declared artifact size is not positive (06.1.1 §5).</summary>
    internal static Error SizeOutOfRange => Error.Unprocessable(
        ApiErrorCode.SizeOutOfRange,
        "the declared size must be greater than zero",
        [new ErrorDetail("declared_bytes", "out_of_range")]);

    /// <summary>403: the request carries no project scope, so no artifact can be minted for it (00 §5.4).</summary>
    internal static Error ProjectRequired => Error.Forbidden(
        "the request is not scoped to a project, so no artifact can be minted");

    /// <summary>
    /// 404: the run does not exist in the caller's project (06.1.2 AC6). RLS makes another tenant's run invisible,
    /// so an unknown id and a foreign id are the same answer; the handler also compares the run's project with the
    /// request scope, so a run of another project of the same org is refused too.
    /// </summary>
    internal static Error RunNotFound => Error.NotFound("the run does not exist in this project");

    /// <summary>
    /// 403: the caller has no token identity (<c>jti</c>) to bind a producer slot to (06.1.2 §5, T9/T85). A slot is
    /// bound to the credential that first wrote it, so a caller without one can never bind — it is refused before
    /// the registry is called rather than binding an empty identity.
    /// </summary>
    internal static Error NoTokenIdentity => Error.Forbidden(
        "the caller has no token identity, so no producer slot can be bound");

    /// <summary>
    /// 422: the named <c>execution_key</c> is not a planned execution of the run (06.1.2 AC2, TL T10/T17).
    /// <c>planned_execution</c> has no <c>attempt_no</c> column, so only the key's membership is checked here;
    /// the attempt number is validated for shape (<c>&gt;= 1</c>) by the command validator.
    /// </summary>
    internal static Error UnknownExecutionKey => Error.Unprocessable(
        ApiErrorCode.UnknownExecutionKey,
        "the execution key is not a planned execution of the run",
        [new ErrorDetail("execution_key", "unknown")]);

    /// <summary>409: the project is being deleted, so a new upload is refused (06.1.2 AC6, 06.4.4 AC2).</summary>
    internal static Error ProjectDeleting => Error.Conflict(
        ApiErrorCode.ProjectDeleting,
        "the project is being deleted, so no new upload is accepted");

    /// <summary>422: the declared media type is not in the kind policy's allowlist (06.1.2 AC1, 06.1.2-T3).</summary>
    internal static Error MediaTypeNotAllowed => Error.Unprocessable(
        ApiErrorCode.MediaTypeNotAllowed,
        "the media type is not accepted for this artifact kind",
        [new ErrorDetail("media_type", "not_allowed")]);

    /// <summary>
    /// 422: capture of the kind is switched off for the project, or the project has no policy row for it
    /// (06.1.2 AC1, 06.2.2 AC2). A missing row fails closed: the policy is the only authority on what a project
    /// accepts, and 06.1.2-T3.1's static defaults may later relax this to a permissive fallback.
    /// </summary>
    internal static Error KindDisabled => Error.Unprocessable(
        ApiErrorCode.KindDisabled,
        "capture of this artifact kind is disabled for the project",
        [new ErrorDetail("kind", "disabled")]);

    /// <summary>413: the declared size is above the kind policy's <c>max_bytes</c> (06.1.2 AC1, 06.1.2-T3).</summary>
    internal static Error PayloadTooLarge => new(
        ApiErrorCode.PayloadTooLarge,
        "the declared size is above the limit for this artifact kind",
        StatusCodes.Status413PayloadTooLarge,
        Retryable: false,
        Details: [new ErrorDetail("bytes", "too_large")]);
}
