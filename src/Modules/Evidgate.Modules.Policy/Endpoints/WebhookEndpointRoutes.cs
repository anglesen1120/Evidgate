using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Webhooks.Application;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Policy.Endpoints;

/// <summary>
/// The outbound-webhook management routes (07.6.2, 07.6.3): endpoint CRUD and the delivery log of a project. Like the
/// other policy routes the delegates only bind 00 §9.1's parameters and answer in <see cref="Result"/> terms:
/// authorization (<c>webhook:read</c>/<c>webhook:write</c>) is the C2 pipeline, the tenant is the request's, and the
/// 00 §9.2 envelope is the host's <c>ResultEndpointFilter</c>. The operator replay is not here: it is not a tenant route.
/// </summary>
public static class WebhookEndpointRoutes
{
    /// <summary>Maps the webhook routes and returns the <c>/v1</c> group so the host can apply its filters (00 §5.5 C8).</summary>
    /// <param name="routes">The host's route builder.</param>
    public static RouteGroupBuilder MapWebhookEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("webhook-endpoints");

        group.MapPost("/projects/{p}/webhook-endpoints", RegisterAsync)
            .Accepts<CreateWebhookEndpointRequest>("application/json")
            .Produces<WebhookEndpointSecretResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Register an outbound webhook endpoint; the signing secret is shown once (07.6.3)");

        group.MapGet("/projects/{p}/webhook-endpoints", ListEndpointsAsync)
            .Produces<WebhookEndpointListResponse>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of a project's webhook endpoints, never the secret (07.6.3)");

        group.MapGet("/projects/{p}/webhook-endpoints/{e}", GetEndpointAsync)
            .Produces<WebhookEndpointView>(StatusCodes.Status200OK)
            .WithSummary("One webhook endpoint; ETag is its version (07.6.3)");

        group.MapPatch("/projects/{p}/webhook-endpoints/{e}", UpdateEndpointAsync)
            .Accepts<UpdateWebhookEndpointRequest>("application/json")
            .Produces<WebhookEndpointView>(StatusCodes.Status200OK)
            .WithSummary("Change a webhook endpoint's URL, event types or state; If-Match is its version (07.6.3)");

        // The delegate answers Result, so the 200 declaration is what lets the host's envelope filter render the 204.
        group.MapDelete("/projects/{p}/webhook-endpoints/{e}", DeleteEndpointAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Delete a webhook endpoint; its delivery history is kept (07.6.3)");

        group.MapGet("/projects/{p}/webhook-deliveries", ListDeliveriesAsync)
            .Produces<WebhookDeliveryListResponse>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of a project's webhook deliveries, newest first (07.6.2)");

        return group;
    }

    private static async Task<Result<WebhookEndpointSecretResponse>> RegisterAsync(
        Guid p,
        CreateWebhookEndpointRequest request,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        // The answer carries the one-time secret: no cache or proxy may keep it.
        context.Response.Headers.CacheControl = "no-store";

        var result = await mediator.Send(
            new RegisterWebhookEndpointCommand(p, request.Url, request.EventTypes),
            cancellationToken).ConfigureAwait(false);

        return WithETag(context, result, created => created.Version);
    }

    private static async Task<Result<WebhookEndpointListResponse>> ListEndpointsAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (WebhookEndpointRules.Limit(context.Request.Query["limit"], out var limit) is { } error)
        {
            return Result.Failure<WebhookEndpointListResponse>(error);
        }

        return await mediator.Send(
            new ListWebhookEndpointsQuery(p, Blank(context.Request.Query["cursor"]), limit),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<WebhookEndpointView>> GetEndpointAsync(
        Guid p,
        Guid e,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetWebhookEndpointQuery(p, e), cancellationToken).ConfigureAwait(false);

        return WithETag(context, result, view => view.Version);
    }

    private static async Task<Result<WebhookEndpointView>> UpdateEndpointAsync(
        Guid p,
        Guid e,
        UpdateWebhookEndpointRequest request,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new UpdateWebhookEndpointCommand(
                p,
                e,
                context.Request.Headers.IfMatch.ToString(),
                request.Url,
                request.EventTypes,
                request.State),
            cancellationToken).ConfigureAwait(false);

        return WithETag(context, result, view => view.Version);
    }

    private static async Task<Result> DeleteEndpointAsync(
        Guid p,
        Guid e,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(new DeleteWebhookEndpointCommand(p, e), cancellationToken).ConfigureAwait(false);

    private static async Task<Result<WebhookDeliveryListResponse>> ListDeliveriesAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (WebhookEndpointRules.Limit(context.Request.Query["limit"], out var limit) is { } error)
        {
            return Result.Failure<WebhookDeliveryListResponse>(error);
        }

        return await mediator.Send(
            new ListWebhookDeliveriesQuery(
                p,
                Blank(context.Request.Query["state"]),
                Blank(context.Request.Query["cursor"]),
                limit),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Echoes the endpoint's version as the <c>ETag</c>, so the next change can send it back as <c>If-Match</c>.</summary>
    private static Result<T> WithETag<T>(HttpContext context, Result<T> result, Func<T, int> version)
    {
        if (result.IsSuccess && result.SuccessValue is T value)
        {
            context.Response.Headers.ETag = string.Create(CultureInfo.InvariantCulture, $"\"{version(value)}\"");
        }

        return result;
    }

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
