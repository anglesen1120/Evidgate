using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Catalog.Application;

/// <summary>Errors of the fixture routes.</summary>
internal static class FixtureErrors
{
    internal static Error NotFound => Error.NotFound("no fixture with that id is visible to this tenant");

    internal static Error KeyTaken => Error.Conflict(ApiErrorCode.FixtureKeyTaken, "that fixture key is already used by a fixture of this project");

    /// <summary>
    /// The per-fixture lock is held by another request. Nothing was changed, and the same request succeeds once the
    /// holder finishes, so the envelope says the call may be retried unchanged.
    /// </summary>
    internal static Error Contended => new(
        ApiErrorCode.Unavailable,
        "the fixture is being changed by another request; retry shortly",
        StatusCodes.Status503ServiceUnavailable,
        Retryable: true);

    internal static Error Invalid(string message, params ErrorDetail[] details) => Error.Invalid(message, details);

    /// <summary>
    /// A suite revision names a fixture this project does not hold at that revision: one code for an unknown fixture, an
    /// unknown revision and another project's fixture, so a caller cannot probe which of the three it hit.
    /// </summary>
    internal static Error CrossProject(IReadOnlyList<ErrorDetail> details) => new(
        ApiErrorCode.CrossProjectMember,
        "a fixture requirement names a fixture or a revision that does not exist in this project",
        StatusCodes.Status422UnprocessableEntity,
        Retryable: false,
        Details: details);
}
