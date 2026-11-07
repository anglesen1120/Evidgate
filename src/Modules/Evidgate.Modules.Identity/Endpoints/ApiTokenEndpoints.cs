using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Application.Commands;
using Evidgate.Modules.Identity.Application.Queries;
using Evidgate.Modules.Identity.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Identity.Endpoints;

/// <summary>
/// The API-token routes: <c>POST/GET /v1/projects/{p}/api-tokens</c> and <c>DELETE /v1/api-tokens/{t}</c>. Delegates
/// only bind and answer in <see cref="Result"/> terms — authorization is the handlers' <c>[Permission]</c>, the tenant
/// and the 00 §9.2 envelope are the pipeline's and the host's. Create answers a secret exactly once, so the route
/// marks the response uncacheable.
/// </summary>
public static class ApiTokenEndpoints
{
    /// <summary>Maps the routes and returns the group so the host applies its filters.</summary>
    /// <param name="routes">The route builder.</param>
    public static RouteGroupBuilder MapApiTokenEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("identity");

        // The bodiless `.Produces(200)` keeps the internal Result wrapper out of the served OpenAPI document.
        group.MapPost("/projects/{p}/api-tokens", CreateAsync)
            .Produces<ApiTokenCreatedResponse>(StatusCodes.Status200OK)
            .WithSummary("Create a project API token; the plaintext is returned once (02.4.4)");
        group.MapGet("/projects/{p}/api-tokens", ListAsync)
            .Produces<ApiTokenListResponse>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of a project's API tokens, metadata only (02.4.4)");
        group.MapDelete("/api-tokens/{t}", RevokeAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Revoke an API token immediately (02.4.4)");

        return group;
    }

    private static async Task<Result<ApiTokenCreatedResponse>> CreateAsync(
        Guid p,
        CreateApiTokenRequest request,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";

        return await mediator.Send(
            new CreateApiTokenCommand(p, request.Name, request.Permissions, request.ExpiresAt),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<ApiTokenListResponse>> ListAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (EdgeInputs.LimitError(context.Request.Query["limit"], out var limit) is { } error)
        {
            return Result.Failure<ApiTokenListResponse>(error);
        }

        return await mediator.Send(
            new ListApiTokensQuery(p, EdgeInputs.Cursor(context), limit),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result> RevokeAsync(
        Guid t,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(new RevokeApiTokenCommand(t), cancellationToken).ConfigureAwait(false);
}
