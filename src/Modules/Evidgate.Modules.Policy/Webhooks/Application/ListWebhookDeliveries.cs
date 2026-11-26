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
using Evidgate.Modules.Policy.Webhooks.Domain;
using Mediator;

namespace Evidgate.Modules.Policy.Webhooks.Application;

/// <summary>A project's delivery log (<c>GET /v1/projects/{p}/webhook-deliveries</c>), newest first.</summary>
/// <param name="ProjectId">The project from the route.</param>
/// <param name="State">A delivery state to filter on, or null for every state.</param>
/// <param name="Cursor">Opaque cursor, or null.</param>
/// <param name="Limit">Page size, 1–200.</param>
internal sealed record ListWebhookDeliveriesQuery(Guid ProjectId, string? State, string? Cursor, int Limit)
    : IQuery<Result<WebhookDeliveryListResponse>>;

/// <summary>
/// Handler of <see cref="ListWebhookDeliveriesQuery"/>: one keyset statement (<c>(created_at, id)</c> descending), whose
/// optional state equality and cursor bound are part of the statement text so each shape rides its own index.
/// </summary>
[Permission(Permissions.Webhook.Read)]
internal sealed class ListWebhookDeliveriesQueryHandler(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IRequestTenantContextAccessor tenant)
    : IQueryHandler<ListWebhookDeliveriesQuery, Result<WebhookDeliveryListResponse>>
{
    private static readonly string[] States =
    [
        WebhookStates.DeliveryQueued,
        WebhookStates.DeliverySending,
        WebhookStates.DeliveryDelivered,
        WebhookStates.DeliveryRetrying,
        WebhookStates.DeliveryDeadLetter,
    ];

    /// <inheritdoc />
    public async ValueTask<Result<WebhookDeliveryListResponse>> Handle(ListWebhookDeliveriesQuery query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(ListWebhookDeliveriesQuery));

        if (PolicyScope.Project(query.ProjectId, principals, tenant) is { } mismatch)
        {
            return Result.Failure<WebhookDeliveryListResponse>(mismatch);
        }

        if (query.State is not null && !States.Contains(query.State, StringComparer.Ordinal))
        {
            return Result.Failure<WebhookDeliveryListResponse>(Error.Invalid(
                "state is not a delivery state",
                [new ErrorDetail("state", "unsupported")]));
        }

        var createdAt = default(DateTimeOffset);
        var id = Guid.Empty;

        if (query.Cursor is not null && !KeysetCursor.TryDecodeDescending(query.Cursor, out createdAt, out id))
        {
            return Result.Failure<WebhookDeliveryListResponse>(Error.Invalid(
                "the cursor is not a value this endpoint issued",
                [new ErrorDetail("cursor", "malformed")]));
        }

        var live = await db.ScalarAsync<int?>(WebhookEndpointSql.ProjectExists, new { project_id = query.ProjectId }, cancellationToken)
            .ConfigureAwait(false);

        if (live is null)
        {
            return Result.Failure<WebhookDeliveryListResponse>(WebhookEndpointErrors.ProjectNotFound);
        }

        var rows = await db.QueryAsync<WebhookDeliveryView>(
            WebhookEndpointSql.DeliveryPage(query.State is not null, query.Cursor is not null),
            new
            {
                org_id = scopes.Require().OrgId,
                project_id = query.ProjectId,
                state = query.State,
                cursor_created_at = createdAt,
                cursor_id = id,
                limit = query.Limit + 1,
            },
            cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > query.Limit;
        IReadOnlyList<WebhookDeliveryView> page = hasMore ? [.. rows.Take(query.Limit)] : rows;

        return Result.Success(new WebhookDeliveryListResponse(
            page,
            hasMore ? KeysetCursor.EncodeDescending(page[^1].CreatedAt, page[^1].Id) : null));
    }
}
