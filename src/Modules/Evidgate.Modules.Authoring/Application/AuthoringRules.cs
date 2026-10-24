using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Authoring.Application;

/// <summary>
/// The module-level rules every authoring handler applies before it reads or writes anything: the project half of the C2
/// scope rule, and the 404 of a project the tenant cannot see.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a helper of its own.</b> RLS scopes <c>feature_node</c>, <c>checklist_item</c>, <c>manual_test_case</c> and
/// <c>authoring_version</c> by <b>organization only</b> (<c>feature_node_tenant</c>, migration 0039:461-465), and the C2
/// pipeline's <c>AuthorizationBehaviour</c> evaluates the permission alone. A service credential narrowed to one project
/// (FR-02-7) that addresses a <b>sibling</b> project of the same organization is therefore invisible to both, and the
/// authoring routes would answer it exactly like the owner. Every other module closes that with a helper of its own
/// (<c>CatalogRules.Project</c> :44-60, <c>QuarantineSupport</c>): this is the authoring counterpart, and every authoring
/// handler calls it <b>first</b>, before its project-liveness read and before any write.
/// </para>
/// <para>
/// <b>What it does not decide.</b> Whether the caller holds <c>authoring:read</c>/<c>authoring:write</c> is the pipeline's
/// decision (a 403 <c>forbidden</c> before the handler runs), and whether the project exists in the tenant is the
/// handler's own read (<see cref="ProjectNotFound"/> stays in the handler because only it knows the statement). This
/// helper answers one question: does the addressed project belong to every scope the request carries?
/// </para>
/// </remarks>
internal static class AuthoringRules
{
    /// <summary>
    /// Null when <paramref name="projectId"/> is inside every scope the request carries, else the 403
    /// <c>project_scope_mismatch</c> (00 §9.1). Both branches answer the same error on purpose: a credential narrowed to
    /// another project and a request whose own project disagrees are the same refusal to the caller, and distinguishing
    /// them would let a token probe which project a route names.
    /// </summary>
    /// <param name="projectId">The project the route or the loaded row names.</param>
    /// <param name="principals">The caller, whose token may be narrowed to one project.</param>
    /// <param name="tenant">The request's own tenant context, whose project may be set by a header.</param>
    internal static Error? Project(Guid projectId, IRequestPrincipalAccessor principals, IRequestTenantContextAccessor tenant)
    {
        ArgumentNullException.ThrowIfNull(principals);
        ArgumentNullException.ThrowIfNull(tenant);

        if (principals.Current?.ProjectScope is { } tokenScope && tokenScope != projectId)
        {
            return ProjectScopeMismatch;
        }

        return tenant.Current?.ProjectId is { } requestProject && requestProject != projectId
            ? ProjectScopeMismatch
            : null;
    }

    /// <summary>The project's liveness for a handler that must not leak another tenant's ids: 404, never 403 (AC10).</summary>
    internal static Error ProjectNotFound => Error.NotFound("no project with that id is visible to this tenant");

    /// <summary>The 403 of <see cref="Project"/>; the code is the 00 §9.1 one every module answers.</summary>
    internal static Error ProjectScopeMismatch => Error.Denied(
        ApiErrorCode.ProjectScopeMismatch,
        "the credential is scoped to a different project");
}
