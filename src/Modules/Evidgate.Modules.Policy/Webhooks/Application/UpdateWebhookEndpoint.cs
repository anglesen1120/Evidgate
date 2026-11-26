using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Application;
using Evidgate.Modules.Policy.Webhooks.Domain;
using Mediator;
using Npgsql;

namespace Evidgate.Modules.Policy.Webhooks.Application;

/// <summary>Change an endpoint (<c>PATCH /v1/projects/{p}/webhook-endpoints/{e}</c>); the caller's <c>If-Match</c> is the version it read.</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="EndpointId">The endpoint from the route.</param>
/// <param name="IfMatch">The raw <c>If-Match</c> header.</param>
/// <param name="Url">A new https target, or null to keep it.</param>
/// <param name="EventTypes">A new event type list, or null to keep it.</param>
/// <param name="State"><c>active</c> or <c>disabled</c>, or null to keep it.</param>
internal sealed record UpdateWebhookEndpointCommand(
    Guid ProjectId,
    Guid EndpointId,
    string? IfMatch,
    string? Url,
    string[]? EventTypes,
    string? State)
    : ICommand<Result<WebhookEndpointView>>;

/// <summary>
/// Handler of <see cref="UpdateWebhookEndpointCommand"/>. The version is checked twice: once before the (network)
/// URL guard, so a stale caller is refused without a DNS lookup, and once under the row lock, which is what makes the
/// check and the write one step. A new URL passes the same guard a registration does.
/// </summary>
[Permission(Permissions.Webhook.Write)]
[AuditedAction("webhook_endpoint.update")]
internal sealed class UpdateWebhookEndpointCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    WebhookUrlGuard guard,
    IAuditTrail audit)
    : ICommandHandler<UpdateWebhookEndpointCommand, Result<WebhookEndpointView>>
{
    /// <inheritdoc />
    public async ValueTask<Result<WebhookEndpointView>> Handle(
        UpdateWebhookEndpointCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(UpdateWebhookEndpointCommand));

        if (PolicyScope.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<WebhookEndpointView>(mismatch);
        }

        var rejected = WebhookEndpointRules.IfMatch(command.IfMatch, out var expected) ?? CheckBody(command);

        if (rejected is not null)
        {
            return Result.Failure<WebhookEndpointView>(rejected);
        }

        var orgId = scopes.Require().OrgId;
        var parameters = new { org_id = orgId, project_id = command.ProjectId, endpoint_id = command.EndpointId };
        var current = await db.QuerySingleOrDefaultAsync<WebhookEndpointView>(WebhookEndpointSql.Get, parameters, cancellationToken)
            .ConfigureAwait(false);

        if (Precondition(current, expected) is { } stale)
        {
            return Result.Failure<WebhookEndpointView>(stale);
        }

        string? url = null;

        if (command.Url is not null)
        {
            var check = await guard.CheckAsync(command.Url, cancellationToken).ConfigureAwait(false);
            url = check.Target is { } target ? WebhookEndpointRules.StoredUrl(target) : null;

            if (url is null)
            {
                return Result.Failure<WebhookEndpointView>(WebhookEndpointErrors.UrlBlocked(check.Reason));
            }
        }

        try
        {
            return await WriteAsync(command, parameters, expected, url, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return Result.Failure<WebhookEndpointView>(WebhookEndpointErrors.Busy);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Result.Failure<WebhookEndpointView>(WebhookEndpointErrors.UrlTaken());
        }
    }

    private static Error? CheckBody(UpdateWebhookEndpointCommand command)
    {
        if (command.Url is null && command.EventTypes is null && command.State is null)
        {
            return Error.Invalid("a change must carry at least one of url, event_types or state", [new ErrorDetail("/", "empty")]);
        }

        if (command.State is not null
            && command.State != WebhookStates.EndpointActive
            && command.State != WebhookStates.EndpointDisabled)
        {
            return Error.Invalid("state must be active or disabled", [new ErrorDetail("/state", "unsupported")]);
        }

        return WebhookEndpointRules.CheckEventTypes(command.EventTypes);
    }

    private static Error? Precondition(WebhookEndpointView? current, int expected)
    {
        if (current is null)
        {
            return WebhookEndpointErrors.EndpointNotFound;
        }

        return current.Version == expected
            ? null
            : Error.PreconditionFailed("the webhook endpoint changed since it was read; fetch it again");
    }

    private async Task<Result<WebhookEndpointView>> WriteAsync(
        UpdateWebhookEndpointCommand command,
        object parameters,
        int expected,
        string? url,
        CancellationToken cancellationToken)
    {
        await db.ExecuteAsync(WebhookEndpointSql.LockTimeout, null, cancellationToken).ConfigureAwait(false);

        var locked = await db.QuerySingleOrDefaultAsync<WebhookEndpointView>(
            WebhookEndpointSql.GetForUpdate,
            parameters,
            cancellationToken).ConfigureAwait(false);

        if (Precondition(locked, expected) is { } stale)
        {
            return Result.Failure<WebhookEndpointView>(stale);
        }

        var updated = await db.QuerySingleOrDefaultAsync<WebhookEndpointView>(
            WebhookEndpointSql.Update,
            new
            {
                org_id = scopes.Require().OrgId,
                project_id = command.ProjectId,
                endpoint_id = command.EndpointId,
                url = url ?? locked!.Url,
                event_types = (command.EventTypes ?? locked!.EventTypes).ToList(),
                state = command.State ?? locked!.State,
            },
            cancellationToken).ConfigureAwait(false);

        audit.Describe(new AuditObject("webhook_endpoint", command.EndpointId.ToString("D"), ProjectId: command.ProjectId));

        return Result.Success(updated!);
    }
}
