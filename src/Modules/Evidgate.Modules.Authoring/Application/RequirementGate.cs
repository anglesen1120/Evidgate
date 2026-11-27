using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Commands;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;

namespace Evidgate.Modules.Authoring.Application;

/// <summary>
/// What every requirement handler checks before it touches a requirement: the credential's project scope first (RLS
/// scopes rows by organization only, so a credential narrowed to one project must be refused here), then that the
/// project is a live project of the tenant.
/// </summary>
internal static class RequirementGate
{
    /// <summary>The refusal for the project of the route, or null when the caller may go on.</summary>
    /// <param name="db">The tenant-bound database of the open scope.</param>
    /// <param name="scopes">The open tenant scope.</param>
    /// <param name="principals">The caller.</param>
    /// <param name="tenant">The request's own tenant context.</param>
    /// <param name="projectId">The project from the route.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static async ValueTask<Error?> RefusalAsync(
        ITenantDb db,
        ITenantScopeAccessor scopes,
        IRequestPrincipalAccessor principals,
        IRequestTenantContextAccessor tenant,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        if (AuthoringRules.Project(projectId, principals, tenant) is { } scopeError)
        {
            return scopeError;
        }

        return await AuthoringProjectProbe.MissingAsync(db, scopes.Require().OrgId, projectId, cancellationToken).ConfigureAwait(false);
    }
}
