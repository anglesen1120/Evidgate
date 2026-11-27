using System.Globalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application;
using Mediator;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Authoring.Trackers.Application;

/// <summary>
/// Disconnects a connection: the row moves to <c>disconnected</c> and both token ciphers are cleared in the same
/// transaction. The provider is not called; revoking the grant there is the customer's action.
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="ConnectionId">The connection from the route.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>.</param>
internal sealed record DisconnectTrackerConnectionCommand(Guid ProjectId, Guid ConnectionId, string IdempotencyKey)
    : ICommand<Result>, IIdempotentCommand;

/// <summary>Handler of <see cref="DisconnectTrackerConnectionCommand"/>.</summary>
/// <param name="principals">The caller, whose token may be narrowed to one project.</param>
/// <param name="tenant">The request's tenant context, for the project scope check.</param>
/// <param name="store">The connection store.</param>
/// <param name="audit">The audit trail.</param>
[AuditedAction("tracker.connection.disconnect")]
[Permission(Permissions.Project.Write)]
internal sealed class DisconnectTrackerConnectionCommandHandler(
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    ITrackerConnectionStore store,
    IAuditTrail audit) : ICommandHandler<DisconnectTrackerConnectionCommand, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(DisconnectTrackerConnectionCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Authoring, nameof(DisconnectTrackerConnectionCommand));

        ArgumentNullException.ThrowIfNull(command);

        if (AuthoringRules.Project(command.ProjectId, principals, tenant) is { } scopeError)
        {
            return Result.Failure(scopeError);
        }

        // RLS hides another tenant's row and the project filter hides another project's, so both answer this same 404.
        if (!await store.DisconnectAsync(command.ProjectId, command.ConnectionId, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure(TrackerErrors.ConnectionNotFound);
        }

        audit.Describe(new AuditObject(
            "tracker_connection",
            command.ConnectionId.ToString("D", CultureInfo.InvariantCulture),
            Metadata: new Dictionary<string, string> { ["outcome"] = "disconnected" },
            ProjectId: command.ProjectId));

        return Result.Success(StatusCodes.Status204NoContent);
    }
}
