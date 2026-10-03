using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Orchestrator.Application.Manifests;

/// <summary>Errors of the manifest and environment-revision routes (00 §9.2, 05.1.1 §5, 05.1.2 §5).</summary>
internal static class OrchestratorErrors
{
    internal static Error ProjectNotFound => Error.NotFound("no project with that id is visible to this tenant");

    internal static Error EnvironmentRevisionNotFound => Error.NotFound("no environment revision with that id is visible to this tenant");

    internal static Error ProjectScopeMismatch => Error.Denied(ApiErrorCode.ProjectScopeMismatch, "the credential is scoped to a different project");

    internal static Error Busy => new(ApiErrorCode.Unavailable, "the environment is being changed by another request; retry shortly", StatusCodes.Status503ServiceUnavailable);

    internal static Error ManifestInvalid(string message, IReadOnlyList<ErrorDetail> details) =>
        Error.Unprocessable(ApiErrorCode.ManifestInvalid, message, details);

    internal static Error EnvironmentRevisionInvalid(string message, IReadOnlyList<ErrorDetail> details) =>
        Error.Unprocessable(ApiErrorCode.EnvironmentRevisionInvalid, message, details);

    internal static Error RevisionConflict(int next) => new(
        ApiErrorCode.RevisionConflict,
        "the environment's next revision number is not the one the request expected",
        StatusCodes.Status409Conflict,
        Retryable: false,
        Details: [new ErrorDetail("/revision_no", next.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
}

/// <summary>The project half of the C2 scope rule, as in the catalog: a project-scoped credential touches its project only.</summary>
internal static class OrchestratorScope
{
    /// <summary>Null when <paramref name="projectId"/> is inside every scope the request carries, else 403 <c>project_scope_mismatch</c>.</summary>
    internal static Error? Project(Guid projectId, IRequestPrincipalAccessor principals, IRequestTenantContextAccessor tenant)
    {
        if (principals.Current?.ProjectScope is { } tokenScope && tokenScope != projectId)
        {
            return OrchestratorErrors.ProjectScopeMismatch;
        }

        return tenant.Current?.ProjectId is { } requestProject && requestProject != projectId ? OrchestratorErrors.ProjectScopeMismatch : null;
    }
}
