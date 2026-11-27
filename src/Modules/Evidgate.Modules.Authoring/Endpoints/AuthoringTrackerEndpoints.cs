using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Trackers.Application;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.Modules.Authoring.Endpoints;

/// <summary>
/// The read-only tracker connection routes: start a consent, the browser's return from it, the status list and
/// disconnect. Delegates only bind, send one message and map the <see cref="Result"/>: authorization is each handler's
/// <c>[Permission]</c>, the tenant is the pipeline's, and neither a state, a code nor a token is ever echoed back.
/// </summary>
internal static class AuthoringTrackerEndpoints
{
    internal static void MapAuthoringTrackerEndpoints(RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);

        // The consent starts with a POST that stores nothing about the tracker; the callback is a GET by protocol.
        v1.MapPost("/projects/{p}/tracker-connections/{provider}/authorize", AuthorizeAsync)
            .Produces<TrackerAuthorizeResponse>(StatusCodes.Status200OK)
            .WithSummary("Start connecting a tracker: answers the provider's consent URL");

        v1.MapGet("/tracker-connections/{provider}/callback", CallbackAsync)
            .Produces<TrackerConnectionDto>(StatusCodes.Status200OK)
            .WithSummary("Complete a tracker connection: take the consent, exchange the code and store the connection");

        v1.MapGet("/projects/{p}/tracker-connections", ListAsync)
            .Produces<TrackerConnectionListResponse>(StatusCodes.Status200OK)
            .WithSummary("The project's tracker connections");

        v1.MapDelete("/projects/{p}/tracker-connections/{c}", DisconnectAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Disconnect a tracker connection and clear its stored tokens");
    }

    /// <summary>
    /// The org a callback state names. The browser reaches the callback with a session and no org header, so the host
    /// resolves the tenant from this; membership of that org is proven by the resolution that follows, not here.
    /// </summary>
    /// <param name="state">The untrusted <c>state</c> query value.</param>
    internal static Guid? CallbackOrg(string? state) => TrackerState.Org(state);

    private static async Task<Result<TrackerAuthorizeResponse>> AuthorizeAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);
        context.Response.Headers.CacheControl = "no-store";

        return await mediator.Send(
            new AuthorizeTrackerConnectionCommand(p, RouteProvider(context)),
            cancellationToken).ConfigureAwait(false);
    }

    // Reads and consumes the state-bound consent in short units outside one transaction, so the callback cannot be
    // one mediator message (see TrackerCallbackService); the architecture scan names it as the one such delegate.
    private static async Task<Result<TrackerConnectionDto>> CallbackAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        // With no org in the state, the guard throws the missing-tenant 401 before anything is read or consumed.
        RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);
        context.Response.Headers.CacheControl = "no-store";

        var query = context.Request.Query;
        var callback = context.RequestServices.GetRequiredService<TrackerCallbackService>();

        return await callback.CompleteAsync(RouteProvider(context), query["code"], query["state"], query["error"], cancellationToken).ConfigureAwait(false);
    }

    private static string RouteProvider(HttpContext context) => context.Request.RouteValues["provider"] as string ?? string.Empty;

    private static async Task<Result<TrackerConnectionListResponse>> ListAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);

        return await mediator.Send(new ListTrackerConnectionsQuery(p), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result> DisconnectAsync(
        Guid p,
        Guid c,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);

        return await mediator.Send(
            new DisconnectTrackerConnectionCommand(p, c, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }
}
