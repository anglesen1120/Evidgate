using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Catalog.Application;

/// <summary>Errors of the fixture routes (03.3.1, 00 §9.2).</summary>
internal static class FixtureErrors
{
    internal static Error NotFound => Error.NotFound("no fixture with that id is visible to this tenant");

    internal static Error KeyTaken => Error.Conflict(ApiErrorCode.FixtureKeyTaken, "that fixture key is already used by a fixture of this project");

    internal static Error Invalid(string message, params ErrorDetail[] details) => Error.Invalid(message, details);

    /// <summary>
    /// A suite revision names a fixture this project does not hold at that revision. It is the same code as an unknown
    /// case revision, because from the author's side both are one mistake — a reference to content this project does not
    /// have — and one answer keeps a caller from probing which of the two it hit.
    /// </summary>
    internal static Error CrossProject(IReadOnlyList<ErrorDetail> details) => new(
        ApiErrorCode.CrossProjectMember,
        "a fixture requirement names a fixture or a revision that does not exist in this project",
        StatusCodes.Status422UnprocessableEntity,
        Retryable: false,
        Details: details);
}
