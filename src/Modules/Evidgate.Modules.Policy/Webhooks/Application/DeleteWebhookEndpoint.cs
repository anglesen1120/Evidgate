using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Application;
using Mediator;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Evidgate.Modules.Policy.Webhooks.Application;

/// <summary>Delete an endpoint (<c>DELETE /v1/projects/{p}/webhook-endpoints/{e}</c>): a soft delete that keeps the delivery history.</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="EndpointId">The endpoint from the route.</param>
internal sealed record DeleteWebhookEndpointCommand(Guid ProjectId, Guid EndpointId) : ICommand<Result>;

/// <summary>
/// Handler of <see cref="DeleteWebhookEndpointCommand"/>. The row turns <c>deleted</c> and is never removed: the fan-out
/// skips it, the delivery worker records <c>endpoint_disabled</c> for anything still in flight, and the delivery rows
/// stay readable. A deleted or foreign endpoint is 404, so a repeat writes no second audit row.
/// </summary>
[Permission(Permissions.Webhook.Write)]
[AuditedAction("webhook_endpoint.delete")]
internal sealed class DeleteWebhookEndpointCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IAuditTrail audit)
    : ICommandHandler<DeleteWebhookEndpointCommand, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(DeleteWebhookEndpointCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(DeleteWebhookEndpointCommand));

        if (PolicyScope.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure(mismatch);
        }

        await db.ExecuteAsync(WebhookEndpointSql.LockTimeout, null, cancellationToken).ConfigureAwait(false);

        int changed;

        try
        {
            changed = await db.ExecuteAsync(
                WebhookEndpointSql.SoftDelete,
                new { org_id = scopes.Require().OrgId, project_id = command.ProjectId, endpoint_id = command.EndpointId },
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return Result.Failure(WebhookEndpointErrors.Busy);
        }

        if (changed == 0)
        {
            return Result.Failure(WebhookEndpointErrors.EndpointNotFound);
        }

        audit.Describe(new AuditObject("webhook_endpoint", command.EndpointId.ToString("D"), ProjectId: command.ProjectId));

        return Result.Success(StatusCodes.Status204NoContent);
    }
}
