using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Application.Releases;
using Evidgate.Modules.Orchestrator.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Orchestrator.Endpoints;

/// <summary>
/// The release routes: the declaration (<c>POST /v1/projects/{p}/releases</c>), the read (<c>GET /v1/releases/{id}</c>)
/// and the explicit bind (<c>POST /v1/releases/{id}/runs</c>). All are thin: the edge reads the capped body and the
/// <c>Idempotency-Key</c>, the handler owns authorization, validation, the transaction and the audit.
/// </summary>
internal static class ReleaseEndpoints
{
    /// <summary>
    /// The release transport cap: a name and up to 100 components of ~90 bytes each, so 64 KiB refuses an oversized body
    /// before it is buffered whole — the same bound-the-edge rule the run and environment routes follow.
    /// </summary>
    internal const int MaxReleaseBytes = 64 * 1024;

    /// <summary>Maps the release routes onto the module's <c>/v1</c> group.</summary>
    internal static void Map(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/projects/{p}/releases", CreateReleaseAsync)
            .Produces<ReleaseCreated>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Declare a release from the exact build digest of each component");

        group.MapGet("/releases/{id}", GetReleaseAsync)
            .Produces<ReleaseDetail>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithSummary("One release, its components and its stored bindings");

        // The delegate returns the `Result<T>` wrapper, which the host's filter unwraps and never the wire shape;
        // the bodiless `.Produces(200)` below replaces the response ASP.NET infers for the default status from that
        // return type, so `ResultOfReleaseBindResult` never reaches the served document (the create route does the same).
        group.MapPost("/releases/{id}/runs", BindReleaseRunAsync)
            .Produces<ReleaseBindResult>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Bind a run to a release by exact build digest");
    }

    /// <summary>
    /// <c>POST /v1/projects/{p}/releases</c>: binds the path, the capped body, its digest and the caller's
    /// <c>Idempotency-Key</c>, then sends the one command. A caller with no principal at all is 401 here, before a byte
    /// of the body is read; the pipeline's <c>run:create</c> check answers a resolved member without it (403).
    /// </summary>
    private static async Task<Result<ReleaseCreated>> CreateReleaseAsync(
        Guid p,
        HttpContext context,
        IRequestPrincipalAccessor principals,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (principals.Current is null)
        {
            return Result.Failure<ReleaseCreated>(OrchestratorEndpoints.Unauthenticated);
        }

        var (bytes, tooLarge) = await OrchestratorEndpoints
            .ReadBodyAsync(context, MaxReleaseBytes, null, cancellationToken)
            .ConfigureAwait(false);
        if (tooLarge is not null)
        {
            return Result.Failure<ReleaseCreated>(tooLarge);
        }

        return await mediator.Send(
            new CreateReleaseCommand(p, bytes, OrchestratorEndpoints.Sha256(bytes), OrchestratorEndpoints.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /v1/releases/{id}</c>: the header and its components. The handler's <c>run:read</c> permission
    /// is the pipeline's; an unknown, another tenant's or a sibling project's release is its one 404 <c>not_found</c>.
    /// No <c>ETag</c>: the create path is the only writer and the body carries the row's <c>version</c>.
    /// </summary>
    private static async Task<Result<ReleaseDetail>> GetReleaseAsync(
        Guid id,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(new GetReleaseQuery(id), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// <c>POST /v1/releases/{id}/runs</c>: binds the path, the capped body, its digest and the caller's
    /// <c>Idempotency-Key</c>, then sends the one command. A caller with no principal at all is 401 here, before a byte of
    /// the body is read; the pipeline's <c>run:create</c> check answers a resolved member without it (403).
    /// </summary>
    private static async Task<Result<ReleaseBindResult>> BindReleaseRunAsync(
        Guid id,
        HttpContext context,
        IRequestPrincipalAccessor principals,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (principals.Current is null)
        {
            return Result.Failure<ReleaseBindResult>(OrchestratorEndpoints.Unauthenticated);
        }

        var (bytes, tooLarge) = await OrchestratorEndpoints
            .ReadBodyAsync(context, MaxReleaseBytes, null, cancellationToken)
            .ConfigureAwait(false);
        if (tooLarge is not null)
        {
            return Result.Failure<ReleaseBindResult>(tooLarge);
        }

        return await mediator.Send(
            new BindReleaseRunCommand(id, bytes, OrchestratorEndpoints.Sha256(bytes), OrchestratorEndpoints.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }
}
