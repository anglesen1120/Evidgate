using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Application.Commands;
using Evidgate.Modules.Identity.Application.Queries;
using Evidgate.Modules.Identity.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Endpoints;

/// <summary>
/// The API-token routes: <c>POST/GET /v1/projects/{p}/api-tokens</c> and <c>DELETE /v1/api-tokens/{t}</c>. Delegates
/// only bind and answer in <see cref="Result"/> terms — authorization is the handlers' <c>[Permission]</c>, the tenant
/// and the 00 §9.2 envelope are the pipeline's and the host's. Create answers a secret exactly once, so the route
/// marks the response uncacheable.
/// </summary>
public static class ApiTokenEndpoints
{
    /// <summary>
    /// The largest create body the route accepts, in bytes: 32 permission codes of at most 64 characters plus the
    /// 120-character name is a few KiB of JSON, so 16 KiB is far above any legal request and still bounded before the
    /// body is parsed. Over it the answer is 413 <c>payload_too_large</c>, as the module's other bounded routes answer.
    /// </summary>
    private const int MaxBodyBytes = 16 * 1024;

    /// <summary>Maps the routes and returns the group so the host applies its filters.</summary>
    /// <param name="routes">The route builder.</param>
    public static RouteGroupBuilder MapApiTokenEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("identity");

        // The create reads its body itself (bounded before parsing), so the documented request schema is declared here;
        // the bodiless `.Produces(200)` keeps the internal Result wrapper out of the served OpenAPI document.
        group.MapPost("/projects/{p}/api-tokens", CreateAsync)
            .Accepts<CreateApiTokenRequest>("application/json")
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
        HttpContext context,
        IMediator mediator,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";

        var (body, bodyError) = await BoundedJsonBody
            .ReadAsync(context, MaxBodyBytes, cancellationToken)
            .ConfigureAwait(false);

        using (body)
        {
            if (body is null)
            {
                return Result.Failure<ApiTokenCreatedResponse>(bodyError!);
            }

            if (body.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return Result.Failure<ApiTokenCreatedResponse>(
                    BoundedJsonBody.Invalid("the request body must be a JSON object"));
            }

            CreateApiTokenRequest request;
            try
            {
                // The host's own binding options (snake_case, the source-generated contexts), so the members this route
                // reads are named and shaped exactly as the served document declares them. A null here cannot become a
                // valid create: it degrades to the empty name the command validator refuses.
                request = body.RootElement.Deserialize<CreateApiTokenRequest>(json.Value.SerializerOptions)
                    ?? new CreateApiTokenRequest(string.Empty, null, null);
            }
            catch (JsonException exception)
            {
                return Result.Failure<ApiTokenCreatedResponse>(Error.Invalid(
                    "the body does not match the API token shape",
                    [new ErrorDetail(exception.Path ?? string.Empty, "invalid")]));
            }

            return await mediator.Send(
                new CreateApiTokenCommand(p, request.Name, request.Permissions, request.ExpiresAt),
                cancellationToken).ConfigureAwait(false);
        }
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
