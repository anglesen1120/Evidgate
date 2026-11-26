using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Application.Manifests;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

namespace Evidgate.Modules.Orchestrator.Application.Queries;

/// <summary>
/// The two checks every project-scoped run read that also names verdicts makes before reading any run: the project
/// boundary and the second permission a verdict needs. One copy, so the run list and the decision counts cannot
/// drift apart.
/// </summary>
internal static class RunReadAccess
{
    /// <summary>
    /// Null when the project exists, is live and is inside every scope the request carries. An unknown id, another
    /// tenant's project (hidden by RLS), a soft-deleted project and a scope mismatch all answer the one
    /// <see cref="OrchestratorErrors.ProjectNotFound"/>, so the answer is no existence oracle.
    /// </summary>
    internal static async ValueTask<Error?> CheckProjectAsync(
        Guid projectId,
        ITenantDb db,
        IRequestPrincipalAccessor principals,
        IRequestTenantContextAccessor tenant,
        CancellationToken cancellationToken)
    {
        if (OrchestratorScope.Project(projectId, principals, tenant) is not null)
        {
            return OrchestratorErrors.ProjectNotFound;
        }

        var live = await db.ScalarAsync<int?>(
                ListRunsSql.ProjectExists,
                new { project_id = projectId },
                cancellationToken)
            .ConfigureAwait(false);

        return live is null ? OrchestratorErrors.ProjectNotFound : null;
    }

    /// <summary>
    /// True when the caller holds <c>decision:read</c>. A verdict is that permission's data (00 §9.3) while the
    /// route's own permission is <c>run:read</c>, so the pipeline cannot ask: the handler asks once, after the
    /// project boundary, so a foreign project keeps its 404.
    /// </summary>
    internal static async ValueTask<bool> CanReadDecisionsAsync(
        IRequestPrincipalAccessor principals,
        IRequestTenantContextAccessor tenant,
        IPermissionEvaluator permissions,
        CancellationToken cancellationToken)
    {
        var context = tenant.Require();

        return principals.Current is { } principal
            && (await permissions
                .EvaluateAsync(Permissions.Decision.Read, principal, context, cancellationToken)
                .ConfigureAwait(false))
                .IsGranted;
    }
}
