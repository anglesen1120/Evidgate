using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Application;
using Mediator;

namespace Evidgate.Modules.Policy.Webhooks.Application;

/// <summary>One endpoint (<c>GET /v1/projects/{p}/webhook-endpoints/{e}</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="EndpointId">The endpoint from the route.</param>
internal sealed record GetWebhookEndpointQuery(Guid ProjectId, Guid EndpointId) : IQuery<Result<WebhookEndpointView>>;

/// <summary>Handler of <see cref="GetWebhookEndpointQuery"/>: RLS plus the project and state predicates decide visibility; the secret is never selected.</summary>
[Permission(Permissions.Webhook.Read)]
internal sealed class GetWebhookEndpointQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<GetWebhookEndpointQuery, Result<WebhookEndpointView>>
{
    /// <inheritdoc />
    public async ValueTask<Result<WebhookEndpointView>> Handle(GetWebhookEndpointQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(GetWebhookEndpointQuery));

        if (PolicyScope.Project(query.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<WebhookEndpointView>(mismatch);
        }

        var view = await db.QuerySingleOrDefaultAsync<WebhookEndpointView>(
            WebhookEndpointSql.Get,
            new { org_id = scopes.Require().OrgId, project_id = query.ProjectId, endpoint_id = query.EndpointId },
            cancellationToken).ConfigureAwait(false);

        return view is null
            ? Result.Failure<WebhookEndpointView>(WebhookEndpointErrors.EndpointNotFound)
            : Result.Success(view);
    }
}

/// <summary>Cursor page of a project's live endpoints (<c>GET /v1/projects/{p}/webhook-endpoints</c>).</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="Cursor">Opaque cursor, or null.</param>
/// <param name="Limit">Page size, 1–200.</param>
internal sealed record ListWebhookEndpointsQuery(Guid ProjectId, string? Cursor, int Limit) : IQuery<Result<WebhookEndpointPage>>;

/// <summary>Handler of <see cref="ListWebhookEndpointsQuery"/>: project check, then one keyset statement; one extra row decides the next page.</summary>
[Permission(Permissions.Webhook.Read)]
internal sealed class ListWebhookEndpointsQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<ListWebhookEndpointsQuery, Result<WebhookEndpointPage>>
{
    /// <inheritdoc />
    public async ValueTask<Result<WebhookEndpointPage>> Handle(ListWebhookEndpointsQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(ListWebhookEndpointsQuery));

        if (PolicyScope.Project(query.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<WebhookEndpointPage>(mismatch);
        }

        var live = await db.ScalarAsync<int?>(WebhookEndpointSql.ProjectExists, new { project_id = query.ProjectId }, cancellationToken)
            .ConfigureAwait(false);

        if (live is null)
        {
            return Result.Failure<WebhookEndpointPage>(WebhookEndpointErrors.ProjectNotFound);
        }

        var orgId = scopes.Require().OrgId;
        IReadOnlyList<WebhookEndpointView> rows;

        if (query.Cursor is null)
        {
            rows = await db.QueryAsync<WebhookEndpointView>(
                WebhookEndpointSql.FirstPage,
                new { org_id = orgId, project_id = query.ProjectId, limit = query.Limit + 1 },
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (!KeysetCursor.TryDecode(query.Cursor, out var createdAt, out var id))
            {
                return Result.Failure<WebhookEndpointPage>(Error.Invalid(
                    "the cursor is not a value this endpoint issued",
                    [new ErrorDetail("cursor", "malformed")]));
            }

            rows = await db.QueryAsync<WebhookEndpointView>(
                WebhookEndpointSql.NextPage,
                new { org_id = orgId, project_id = query.ProjectId, cursor_created_at = createdAt, cursor_id = id, limit = query.Limit + 1 },
                cancellationToken).ConfigureAwait(false);
        }

        var hasMore = rows.Count > query.Limit;
        IReadOnlyList<WebhookEndpointView> page = hasMore ? [.. rows.Take(query.Limit)] : rows;

        return Result.Success(new WebhookEndpointPage(page, hasMore ? KeysetCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null));
    }
}
