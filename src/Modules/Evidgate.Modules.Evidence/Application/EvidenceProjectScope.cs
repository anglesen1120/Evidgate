using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// The module's project-scope rule: a credential bound to another project, or a request already narrowed to another
/// project, is outside the scope and is refused 403 <c>project_scope_mismatch</c> before any row is read. A read that
/// addresses its target by id alone maps the mismatch to the route's own 404 instead, so it is never an oracle; the
/// routes that name the project (retention, the kind-policy override) surface this refusal.
/// <para>
/// On the artifact read routes only the request-narrowed form is reachable today: <c>artifact:read</c> is outside the
/// service ceiling, so no credential carries a project into them. Both members stay checked so a later project-bound
/// credential cannot widen the routes.
/// </para>
/// </summary>
internal static class EvidenceProjectScope
{
    /// <summary>Null when the addressed project is inside every scope the request carries.</summary>
    /// <param name="projectId">The project the addressed row, or the route, names.</param>
    /// <param name="credentialScope">The project the credential is bound to, if any.</param>
    /// <param name="requestProject">The project the request's tenant context is narrowed to, if any.</param>
    internal static Error? Project(Guid projectId, Guid? credentialScope, Guid? requestProject)
    {
        if (credentialScope is { } tokenScope && tokenScope != projectId)
        {
            return Error.Denied(ApiErrorCode.ProjectScopeMismatch, "the project is outside the credential's scope");
        }

        return requestProject is { } narrowed && narrowed != projectId
            ? Error.Denied(ApiErrorCode.ProjectScopeMismatch, "the project is outside the request's scope")
            : null;
    }
}
