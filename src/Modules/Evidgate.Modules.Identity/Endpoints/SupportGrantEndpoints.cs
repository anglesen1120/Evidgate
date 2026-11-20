using Evidgate.BuildingBlocks.SharedKernel.Api;
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
/// The support-grant routes a tenant serves: <c>POST /v1/support-grants/{g}/approve</c>,
/// <c>POST /v1/support-grants/{g}/revoke</c> and <c>GET /v1/orgs/{o}/support-grants</c>. Delegates bind and answer
/// in <see cref="Result"/> terms — authorization is the handlers' <c>[Permission]</c>, the tenant and the 00 §9.2
/// envelope are the pipeline's and the host's. The openapi <c>/support-grants</c> request route is not mapped here:
/// it is the operator side and is not part of this change.
/// </summary>
public static class SupportGrantEndpoints
{
    /// <summary>Maps the routes and returns the group so the host applies its filters.</summary>
    /// <param name="routes">The route builder.</param>
    public static RouteGroupBuilder MapSupportGrantEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("support");

        // Both mutations answer an empty 200: the grant's state is the answer and the list serves it. The bodiless
        // `.Produces(200)` keeps the internal Result wrapper out of the served OpenAPI document.
        group.MapPost("/support-grants/{g}/approve", ApproveAsync)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Approve a support grant as a tenant_admin of its org (02.8.1)");
        group.MapPost("/support-grants/{g}/revoke", RevokeAsync)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Revoke a support grant immediately (02.8.1)");
        group.MapGet("/orgs/{o}/support-grants", ListAsync)
            .Produces<SupportGrantList>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of the org's support grants, newest first (02.8.1)");

        return group;
    }

    private static async Task<Result> ApproveAsync(
        Guid g,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(
            new ApproveSupportGrantCommand(g, EdgeInputs.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

    private static async Task<Result> RevokeAsync(
        Guid g,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(
            new RevokeSupportGrantCommand(g, EdgeInputs.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

    private static async Task<Result<SupportGrantList>> ListAsync(
        Guid o,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (EdgeInputs.LimitError(context.Request.Query["limit"], out var limit) is { } error)
        {
            return Result.Failure<SupportGrantList>(error);
        }

        return await mediator.Send(
            new ListSupportGrantsQuery(o, EdgeInputs.Cursor(context), limit),
            cancellationToken).ConfigureAwait(false);
    }
}
