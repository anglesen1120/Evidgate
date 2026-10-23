using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Catalog.Application;

/// <summary>Errors of the catalog's HTTP surface (00 §9.2).</summary>
internal static class CatalogErrors
{
    internal static Error CaseNotFound => Error.NotFound("no test case with that id is visible to this tenant");

    internal static Error ProjectNotFound => Error.NotFound("no project with that id is visible to this tenant");

    internal static Error ProjectScopeMismatch => Error.Denied(
        ApiErrorCode.ProjectScopeMismatch,
        "the credential is scoped to a different project");

    internal static Error Stale(string what, int currentVersion) => new(
        ApiErrorCode.PreconditionFailed,
        $"the {what} changed since it was read (current version {currentVersion})",
        StatusCodes.Status412PreconditionFailed,
        Retryable: false,
        Details:
        [
            new ErrorDetail("If-Match", "stale"),
            new ErrorDetail("version", currentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ]);

    internal static Error ImmutableField(string field) => new(
        ApiErrorCode.CaseFieldImmutable,
        $"'{field}' is part of the case's identity and never changes",
        StatusCodes.Status422UnprocessableEntity,
        Retryable: false,
        Details: [new ErrorDetail(field, "immutable")]);
}

/// <summary>
/// The project half of the C2 scope rule for routes that name a project or reach one through a case: a credential
/// narrowed to one project may touch only that project, and the request's own project (header) must agree.
/// </summary>
internal static class CatalogScope
{
    /// <summary>Null when <paramref name="projectId"/> is inside every scope the request carries, else 403 <c>project_scope_mismatch</c>.</summary>
    internal static Error? Project(
        Guid projectId,
        IRequestPrincipalAccessor principals,
        IRequestTenantContextAccessor tenant)
    {
        if (principals.Current?.ProjectScope is { } tokenScope && tokenScope != projectId)
        {
            return CatalogErrors.ProjectScopeMismatch;
        }

        if (tenant.Current?.ProjectId is { } requestProject && requestProject != projectId)
        {
            return CatalogErrors.ProjectScopeMismatch;
        }

        return null;
    }
}
