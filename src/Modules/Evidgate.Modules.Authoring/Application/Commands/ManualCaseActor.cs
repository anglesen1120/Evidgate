using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>The authoring member behind the caller, for the <c>actor_membership_id</c> of a version row.</summary>
internal static class ManualCaseActor
{
    /// <summary>
    /// The caller's membership of the tenant, or null when the caller is not a member (a service principal or an
    /// operator writes a system version, exactly like the writer's <c>null</c> actor).
    /// </summary>
    /// <param name="db">The tenant-scoped Dapper seam.</param>
    /// <param name="principals">The caller.</param>
    /// <param name="orgId">The tenant of the open scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static async Task<Guid?> MembershipIdAsync(
        ITenantDb db,
        IRequestPrincipalAccessor principals,
        Guid orgId,
        CancellationToken cancellationToken)
    {
        if (principals.Current is not { Kind: PrincipalKind.Member, UserId: { } userId })
        {
            return null;
        }

        return await db.ScalarAsync<Guid?>(
            AuthoringManualCaseReadSql.ActorMembership,
            new { org_id = orgId, user_id = userId },
            cancellationToken).ConfigureAwait(false);
    }
}
