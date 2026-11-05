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

    /// <summary>404: no manifest with that id is visible to this tenant (05.2.1 §5) — the same code for unknown and for another tenant's.</summary>
    internal static Error ManifestNotFound => Error.NotFound("no manifest with that id is visible to this tenant");

    /// <summary>
    /// 422 <c>release_unresolvable</c> (05.2.1 AC10, T15): a request naming a <c>release</c> is syntactically valid but
    /// the <c>release</c>/<c>release_run</c> tables land with 05.6.1 at M2, so at M1 the field never resolves.
    /// </summary>
    internal static Error ReleaseUnresolvable => Error.Unprocessable(
        ApiErrorCode.ReleaseUnresolvable,
        "the release cannot be resolved at this milestone");

    /// <summary>
    /// 422 <c>replay_grant_invalid</c> (05.2.1 AC8, T5): a <c>purpose: "replay"</c> request names no grant the platform
    /// can honour. The <c>replay_grant</c> store lands with 08.4.3, so until then every replay request — with or
    /// without a well-formed grant id — is refused with this code (the path is inert).
    /// </summary>
    internal static Error ReplayGrantInvalid => Error.Unprocessable(
        ApiErrorCode.ReplayGrantInvalid,
        "the replay grant cannot be resolved at this milestone");

    /// <summary>422 <c>replay_release_not_allowed</c> (05.2.1 AC9, T5): a replay run re-executes stored inputs, so it may not also name a <c>release</c>.</summary>
    internal static Error ReplayReleaseNotAllowed => Error.Unprocessable(
        ApiErrorCode.ReplayReleaseNotAllowed,
        "a replay run may not also name a release");

    /// <summary>422 <c>execution_key_conflict</c> (05.2.1 AC6): two planned executions of the manifest share an execution key.</summary>
    internal static Error ExecutionKeyConflict(IReadOnlyList<ErrorDetail> details) => Error.Unprocessable(
        ApiErrorCode.ExecutionKeyConflict,
        "two planned executions of the manifest share an execution key",
        details);

    /// <summary>404 <c>not_found</c>: no release with that id is visible to this tenant — the same code and body an unknown id gets, so another tenant's and a sibling project's release are indistinguishable from one that does not exist.</summary>
    internal static Error ReleaseNotFound => Error.NotFound("no release with that id is visible to this tenant");

    internal static Error ProjectScopeMismatch => Error.Denied(ApiErrorCode.ProjectScopeMismatch, "the credential is scoped to a different project");

    internal static Error Busy => new(ApiErrorCode.Unavailable, "the environment is being changed by another request; retry shortly", StatusCodes.Status503ServiceUnavailable);

    internal static Error ManifestInvalid(string message, IReadOnlyList<ErrorDetail> details) =>
        Error.Unprocessable(ApiErrorCode.ManifestInvalid, message, details);

    internal static Error EnvironmentRevisionInvalid(string message, IReadOnlyList<ErrorDetail> details) =>
        Error.Unprocessable(ApiErrorCode.EnvironmentRevisionInvalid, message, details);

    /// <summary>422 <c>release_invalid</c>: the body is JSON of the declared shape but breaks a release rule.</summary>
    internal static Error ReleaseInvalid(string message, IReadOnlyList<ErrorDetail> details) =>
        Error.Unprocessable(ApiErrorCode.ReleaseInvalid, message, details);

    /// <summary>409 <c>release_name_conflict</c>: the project already declares a release with that name.</summary>
    internal static Error ReleaseNameConflict => Error.Conflict(
        ApiErrorCode.ReleaseNameConflict,
        "the project already declares a release with that name");

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
