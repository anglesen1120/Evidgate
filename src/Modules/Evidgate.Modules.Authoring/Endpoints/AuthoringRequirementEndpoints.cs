using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Commands;
using Evidgate.Modules.Authoring.Application.Queries;
using Evidgate.Modules.Authoring.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Authoring.Endpoints;

/// <summary>
/// The requirement routes of the authoring group (<c>/v1/projects/{p}/authoring/requirements</c>). Delegates only bind
/// the route, the <c>If-Match</c> and the body and answer in <see cref="Result"/> terms: authorization is the pipeline,
/// the tenant is the request's, and the envelope is the host's filter. The member <em>names</em> of a body are checked
/// here, the member <em>values</em> by the command.
/// </summary>
internal static partial class AuthoringRequirementEndpoints
{
    /// <summary>Maps the requirement routes onto the authoring group.</summary>
    /// <param name="authoring">The <c>/v1/projects/{p}/authoring</c> group the host already created.</param>
    internal static void MapAuthoringRequirementEndpoints(RouteGroupBuilder authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);

        MapReads(authoring);
        MapWrites(authoring);
        MapLinkRoutes(authoring);
    }

    private static void MapReads(RouteGroupBuilder authoring)
    {
        authoring.MapGet("/requirements", ListAsync)
            .Produces<RequirementListResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Keyset page of a project's live requirements, order (created_at, id)");

        authoring.MapGet("/requirements/{r}", GetAsync)
            .Produces<RequirementDetailDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("One requirement with its head body and an ETag");

        authoring.MapGet("/requirements/{r}/versions", ListVersionsAsync)
            .Produces<RequirementVersionListResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("A page of a requirement's history, newest first, capped by rows and by body bytes");
    }

    private static void MapWrites(RouteGroupBuilder authoring)
    {
        // The bodiless `.Produces(200)` keeps the C8 `Result<T>` wrapper out of the served OpenAPI document.
        authoring.MapPost("/requirements", CreateAsync)
            .Accepts<CreateRequirementRequest>("application/json")
            .Produces<RequirementWriteResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithSummary("Create a requirement and its first version");

        authoring.MapPatch("/requirements/{r}", PatchAsync)
            .Accepts<UpdateRequirementRequest>("application/json")
            .Produces<RequirementWriteResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status412PreconditionFailed)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithSummary("Edit a requirement; every accepted edit appends one version");

        authoring.MapDelete("/requirements/{r}", DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status412PreconditionFailed)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Hide a requirement from the live list; its versions are kept");

        authoring.MapPost("/requirements/{r}/restore", RestoreAsync)
            .Accepts<RestoreRequirementRequest>("application/json")
            .Produces<RequirementWriteResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status412PreconditionFailed)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithSummary("Append an earlier version's body as the newest version");
    }

    private static async Task<Result<RequirementListResponse>> ListAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);

        var requestError = AuthoringEndpointSupport.LimitError(context.Request.Query["limit"], out var limit);

        // The pipeline authorizes the caller before the handler answers the query error.
        return await mediator.Send(
            new ListRequirementsQuery(p, AuthoringEndpointText.Blank(context.Request.Query["cursor"]), limit, requestError),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<RequirementDetailDto>> GetAsync(
        Guid p,
        Guid r,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);

        var result = await mediator.Send(new GetRequirementQuery(p, r), cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            AuthoringEndpointSupport.SetVersionETag(context, result.Value.Version);
        }

        return result;
    }

    private static async Task<Result<RequirementVersionListResponse>> ListVersionsAsync(
        Guid p,
        Guid r,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);

        var requestError = AuthoringEndpointSupport.LimitError(context.Request.Query["limit"], out var limit);

        return await mediator.Send(
            new ListRequirementVersionsQuery(p, r, AuthoringEndpointText.Blank(context.Request.Query["cursor"]), limit, requestError),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<RequirementWriteResponse>> CreateAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Refuse an unresolved caller before anything else reads the request.
        RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);

        var (body, bodyError) = await AuthoringEndpointSupport.ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);
        if (bodyError is not null)
        {
            return Result.Failure<RequirementWriteResponse>(bodyError);
        }

        if (AuthoringEndpointSupport.UnknownMembers(body, RequirementBody.WriteMembers, "requirement") is { } unknown)
        {
            return Result.Failure<RequirementWriteResponse>(unknown);
        }

        var result = await mediator.Send(
            new CreateRequirementCommand(p, body, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            AuthoringEndpointSupport.SetVersionETag(context, result.Value.Version);
        }

        return result;
    }

    private static async Task<Result<RequirementWriteResponse>> PatchAsync(
        Guid p,
        Guid r,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Refuse an unresolved caller before the precondition is parsed.
        RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);

        if (AuthoringEndpointSupport.IfMatchVersion(context, out var version) is { } ifMatchError)
        {
            return Result.Failure<RequirementWriteResponse>(ifMatchError);
        }

        var (body, bodyError) = await AuthoringEndpointSupport.ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);
        if (bodyError is not null)
        {
            return Result.Failure<RequirementWriteResponse>(bodyError);
        }

        if (AuthoringEndpointSupport.UnknownMembers(body, RequirementBody.WriteMembers, "requirement") is { } unknown)
        {
            return Result.Failure<RequirementWriteResponse>(unknown);
        }

        var result = await mediator.Send(
            new UpdateRequirementCommand(p, r, version, body, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            AuthoringEndpointSupport.SetVersionETag(context, result.Value.Version);
        }

        return result;
    }

    private static async Task<Result> DeleteAsync(
        Guid p,
        Guid r,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Refuse an unresolved caller before the precondition is parsed.
        RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);

        if (AuthoringEndpointSupport.IfMatchVersion(context, out var version) is { } ifMatchError)
        {
            return Result.Failure(ifMatchError);
        }

        return await mediator.Send(
            new DeleteRequirementCommand(p, r, version, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<RequirementWriteResponse>> RestoreAsync(
        Guid p,
        Guid r,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Refuse an unresolved caller before the precondition is parsed.
        RawBodyGuard.RequireAuthenticatedTenant(context.RequestServices);

        if (AuthoringEndpointSupport.IfMatchVersion(context, out var version) is { } ifMatchError)
        {
            return Result.Failure<RequirementWriteResponse>(ifMatchError);
        }

        var (body, bodyError) = await AuthoringEndpointSupport.ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);
        if (bodyError is not null)
        {
            return Result.Failure<RequirementWriteResponse>(bodyError);
        }

        if (AuthoringEndpointSupport.UnknownMembers(body, RequirementBody.RestoreMembers, "requirement restore") is { } unknown)
        {
            return Result.Failure<RequirementWriteResponse>(unknown);
        }

        var result = await mediator.Send(
            new RestoreRequirementCommand(p, r, version, body, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            AuthoringEndpointSupport.SetVersionETag(context, result.Value.Version);
        }

        return result;
    }
}
