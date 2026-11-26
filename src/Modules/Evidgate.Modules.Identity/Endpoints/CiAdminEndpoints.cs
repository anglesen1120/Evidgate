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
/// The admin routes for project ingest tokens. Delegates bind and
/// answer in <see cref="Result"/> terms; authorization, the tenant and the 00 §9.2 envelope are the pipeline's and the
/// host's. Routes without <c>{o}</c> name their org with the <c>X-Evidgate-Org</c> header, like <c>/projects/{p}</c>.
/// </summary>
public static class CiAdminEndpoints
{
    /// <summary>Maps the routes and returns the group so the host applies its filters.</summary>
    /// <param name="routes">The route builder.</param>
    public static RouteGroupBuilder MapCiAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("identity");

        // The bodiless `.Produces(200)` keeps the internal Result wrapper out of the served OpenAPI document.
        group.MapPost("/projects/{p}/ingest-tokens", CreateTokenAsync)
            .Produces<IngestTokenCreatedResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Create an ingest token; the plaintext is returned once (02.4.2)");
        group.MapGet("/projects/{p}/ingest-tokens", ListTokensAsync)
            .Produces<IngestTokenListResponse>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of a project's ingest tokens, metadata only (02.4.2)");
        group.MapPost("/ingest-tokens/{t}/rotate", RotateTokenAsync)
            .Produces<IngestTokenCreatedResponse>(StatusCodes.Status200OK)
            .WithSummary("Mint a successor; the old token verifies for 24 h more (02.4.2)");
        group.MapDelete("/ingest-tokens/{t}", RevokeTokenAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Revoke an ingest token immediately (02.4.2)");

        return group;
    }

    private static async Task<Result<IngestTokenCreatedResponse>> CreateTokenAsync(
        Guid p,
        CreateIngestTokenRequest request,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(new CreateIngestTokenCommand(p, request.Name, request.Scopes, request.ExpiresAt), cancellationToken)
            .ConfigureAwait(false);

    private static async Task<Result<IngestTokenListResponse>> ListTokensAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (EdgeInputs.LimitError(context.Request.Query["limit"], out var limit) is { } error)
        {
            return Result.Failure<IngestTokenListResponse>(error);
        }

        return await mediator.Send(new ListIngestTokensQuery(p, EdgeInputs.Cursor(context), limit), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Result<IngestTokenCreatedResponse>> RotateTokenAsync(
        Guid t,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(new RotateIngestTokenCommand(t), cancellationToken).ConfigureAwait(false);

    private static async Task<Result> RevokeTokenAsync(Guid t, HttpContext context, IMediator mediator, CancellationToken cancellationToken) =>
        await mediator.Send(new RevokeIngestTokenCommand(t, EdgeInputs.IdempotencyKey(context)), cancellationToken).ConfigureAwait(false);
}
