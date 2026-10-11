using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;

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
}
