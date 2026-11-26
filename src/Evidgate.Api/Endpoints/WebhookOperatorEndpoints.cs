using System.Security.Claims;
using Evidgate.Api.OperatorIngress;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Webhooks.Application;

namespace Evidgate.Api.Endpoints;

/// <summary>
/// The operator's webhook route: replay of an event's dead-lettered deliveries. Same posture as the other operator
/// routes — <see cref="IOperatorGate"/> admits the caller (fail-closed <c>DenyAll</c> until a real gate is composed),
/// the org is a required <c>org_id</c> parameter because the operator is not a tenant principal, and the route is
/// excluded from the served OpenAPI document. Replay needs no <c>Idempotency-Key</c>: a delivery that is no longer
/// <c>dead_letter</c> simply does not match, so a repeat answers 409.
/// </summary>
internal static class WebhookOperatorEndpoints
{
    /// <summary>Maps <c>POST /internal/operator/webhook-deliveries/{event_id}/replay?org_id=</c> on the operator ingress.</summary>
    /// <param name="app">The host's application.</param>
    internal static void MapWebhookOperatorEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/internal/operator/webhook-deliveries")
            .ExcludeFromDescription()
            .AddEndpointFilter<RequireOperatorFilter>()
            .AddEndpointFilter<ResultEndpointFilter>();

        group.MapPost("/{event_id:guid}/replay", ReplayAsync);
    }

    private static async Task<Result<WebhookReplayResult>> ReplayAsync(
        Guid event_id,
        Guid? org_id,
        ReplayWebhookDeliveriesHandler replay,
        HttpContext context)
    {
        if (org_id is not { } orgId)
        {
            return Result.Failure<WebhookReplayResult>(Error.BadRequest(
                "org_id is required: a replay is scoped to the organization that owns the deliveries",
                [new ErrorDetail("org_id", "required")]));
        }

        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return Result.Failure<WebhookReplayResult>(new Error(
                ApiErrorCode.Unauthenticated,
                "the operator principal is not authenticated",
                StatusCodes.Status401Unauthorized));
        }

        var subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out var actor))
        {
            return Result.Failure<WebhookReplayResult>(Error.Forbidden(
                "the operator principal carries no user id, so the replay cannot be attributed"));
        }

        return await replay.ExecuteAsync(
            new ReplayWebhookDeliveriesCommand(orgId, event_id, actor),
            context.RequestAborted);
    }
}
