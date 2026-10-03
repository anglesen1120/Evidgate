using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Catalog.Application;

/// <summary>Errors of the suite routes (03.2.1, 00 §9.2).</summary>
internal static class SuiteErrors
{
    internal static Error SuiteNotFound => Error.NotFound("no suite with that id is visible to this tenant");

    internal static Error RevisionNotFound => Error.NotFound("no suite revision with that id is visible to this tenant");

    internal static Error SlugTaken => Error.Conflict(ApiErrorCode.SuiteSlugTaken, "that slug is already used by a suite of this project");

    internal static Error Archived => Error.Conflict(ApiErrorCode.InvalidTransition, "an archived suite takes no new revision");

    internal static Error Invalid(string message, params ErrorDetail[] details) => Error.Invalid(message, details);

    internal static Error NotSecretFree(IReadOnlyList<ErrorDetail> details) => new(
        ApiErrorCode.VariantNotSecretFree,
        "a parameter, URL or requirement carries a secret-shaped key or value; store only names of secrets, never values",
        StatusCodes.Status422UnprocessableEntity,
        Retryable: false,
        Details: details);

    internal static Error CrossProject(IReadOnlyList<ErrorDetail> details) => new(
        ApiErrorCode.CrossProjectMember,
        "a member names a case revision that does not exist in this project",
        StatusCodes.Status422UnprocessableEntity,
        Retryable: false,
        Details: details);

    internal static Error Cycle(IReadOnlyList<ErrorDetail> path) => new(
        ApiErrorCode.DependencyCycle,
        "the dependency edges form a cycle; details list the members along it",
        StatusCodes.Status422UnprocessableEntity,
        Retryable: false,
        Details: path);
}
