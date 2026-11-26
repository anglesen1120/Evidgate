using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Policy.Application;
using Evidgate.Modules.Policy.Webhooks.Infrastructure;
using Mediator;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Evidgate.Modules.Policy.Webhooks.Application;

/// <summary>
/// Register an outbound webhook endpoint (<c>POST /v1/projects/{p}/webhook-endpoints</c>). Deliberately not an
/// <c>IIdempotentCommand</c>: the response carries the one-time signing secret, which an idempotency record would
/// have to store, so a repeat registers a second endpoint (or is refused as a duplicate URL).
/// </summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Url">The https target.</param>
/// <param name="EventTypes">The event types to receive; null means every supported type.</param>
internal sealed record RegisterWebhookEndpointCommand(Guid ProjectId, string? Url, string[]? EventTypes)
    : ICommand<Result<WebhookEndpointCreated>>;

/// <summary>
/// Handler of <see cref="RegisterWebhookEndpointCommand"/>: scope and request checks, the SSRF guard, then the project
/// row lock that serialises the per-project cap, and the insert. The signing secret is generated here, stored only as
/// ciphertext and returned once; the audit row names the endpoint and never the secret or the URL.
/// </summary>
[Permission(Permissions.Webhook.Write)]
[AuditedAction("webhook_endpoint.create")]
internal sealed class RegisterWebhookEndpointCommandHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant,
    IMembership memberships,
    WebhookUrlGuard guard,
    WebhookSecretProtector protector,
    IAuditTrail audit)
    : ICommandHandler<RegisterWebhookEndpointCommand, Result<WebhookEndpointCreated>>
{
    /// <inheritdoc />
    public async ValueTask<Result<WebhookEndpointCreated>> Handle(
        RegisterWebhookEndpointCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(RegisterWebhookEndpointCommand));

        if (PolicyScope.Project(command.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<WebhookEndpointCreated>(mismatch);
        }

        if (WebhookEndpointRules.CheckEventTypes(command.EventTypes) is { } invalid)
        {
            return Result.Failure<WebhookEndpointCreated>(invalid);
        }

        // The guard resolves the host (the one network call of a registration), so it runs before any lock is taken.
        var check = await guard.CheckAsync(command.Url, cancellationToken).ConfigureAwait(false);
        var url = check.Target is { } target ? WebhookEndpointRules.StoredUrl(target) : null;

        if (url is null)
        {
            return Result.Failure<WebhookEndpointCreated>(WebhookEndpointErrors.UrlBlocked(check.Reason));
        }

        var orgId = scopes.Require().OrgId;
        var creator = await ResolveCreatorAsync(orgId, cancellationToken).ConfigureAwait(false);

        if (creator is null)
        {
            return Result.Failure<WebhookEndpointCreated>(WebhookEndpointErrors.NoMembership);
        }

        await db.ExecuteAsync(WebhookEndpointSql.LockTimeout, null, cancellationToken).ConfigureAwait(false);

        try
        {
            return await InsertAsync(command, orgId, creator.Value, url, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return Result.Failure<WebhookEndpointCreated>(WebhookEndpointErrors.Busy);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Result.Failure<WebhookEndpointCreated>(WebhookEndpointErrors.UrlTaken());
        }
    }

    private async Task<Result<WebhookEndpointCreated>> InsertAsync(
        RegisterWebhookEndpointCommand command,
        Guid orgId,
        Guid creator,
        string url,
        CancellationToken cancellationToken)
    {
        var live = await db.ScalarAsync<int?>(
            WebhookEndpointSql.ProjectForUpdate,
            new { project_id = command.ProjectId },
            cancellationToken).ConfigureAwait(false);

        if (live is null)
        {
            return Result.Failure<WebhookEndpointCreated>(WebhookEndpointErrors.ProjectNotFound);
        }

        var count = await db.ScalarAsync<int>(
            WebhookEndpointSql.CountLive,
            new { org_id = orgId, project_id = command.ProjectId },
            cancellationToken).ConfigureAwait(false);

        if (count >= WebhookEndpointLimits.MaxEndpointsPerProject)
        {
            return Result.Failure<WebhookEndpointCreated>(WebhookEndpointErrors.LimitReached());
        }

        var secret = protector.Generate();
        var id = Guid.CreateVersion7();
        var view = await db.QuerySingleOrDefaultAsync<WebhookEndpointView>(
            WebhookEndpointSql.Insert,
            new
            {
                id,
                org_id = orgId,
                project_id = command.ProjectId,
                url,
                secret_ciphertext = secret.Ciphertext,
                secret_hint = secret.Hint,
                event_types = command.EventTypes ?? WebhookEndpointRules.DefaultEventTypes,
                created_by = creator,
            },
            cancellationToken).ConfigureAwait(false);

        audit.Describe(new AuditObject("webhook_endpoint", id.ToString("D"), ProjectId: command.ProjectId));

        return Result.Success(WebhookEndpointCreated.From(view!, secret.Plaintext), StatusCodes.Status201Created);
    }

    /// <summary>The caller's membership in this org, or null when the principal is not an active member.</summary>
    private async Task<Guid?> ResolveCreatorAsync(Guid orgId, CancellationToken cancellationToken)
    {
        if (principals.Current?.UserId is not { } userId)
        {
            return null;
        }

        var membership = await memberships.FindAsync(orgId, userId, cancellationToken).ConfigureAwait(false);

        return membership is { Status: MembershipStatus.Active } active ? active.MembershipId : null;
    }
}
