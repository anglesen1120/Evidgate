using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Mediator;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>The locked read of one node, shared by <see cref="UpdateNodeCommandHandler"/> and <see cref="DeleteNodeCommandHandler"/>.</summary>
internal static class NodeRead
{
    /// <summary>The stored row <c>for update</c>, or null when the project has no such node.</summary>
    /// <param name="db">The tenant connection of the command's transaction.</param>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="nodeId">The node.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static Task<AuthoringNodeReadSql.LockedNodeRow?> LockedAsync(
        ITenantDb db,
        Guid orgId,
        Guid projectId,
        Guid nodeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.QuerySingleOrDefaultAsync<AuthoringNodeReadSql.LockedNodeRow>(
            AuthoringNodeReadSql.LockNode,
            new { org_id = orgId, project_id = projectId, id = nodeId },
            cancellationToken);
    }
}
