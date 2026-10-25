using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Commands;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Authoring.Endpoints;

/// <summary>
/// Routes 6, 11, 18 and 24 of 13.1.1 §5 — the four restore routes: a soft-deleted feature node, checklist item and manual
/// test case, plus re-applying one stored version of whichever entity it describes. Thin by design and like the CRUD
/// route files: the edge binds the two headers (AC18, AC19), the command owns every decision, the writer is the only
/// append path (AC8), and the host's <c>ResultEndpointFilter</c> renders the 00 §9.2 envelope once.
/// </summary>
/// <remarks>
/// <para>
/// <b>The preconditions differ per route, by the plan's table.</b> Routes 6/11/18 take <c>If-Match</c> — a soft-deleted row
/// keeps its <c>version</c>, so the caller can name the tombstone it read and a stale tag answers <c>412</c> — and route
/// 24 takes <c>Idempotency-Key</c> only: the version id already names the exact state to re-apply, so the plan declares no
/// tag there and this route invents none.
/// </para>
/// <para>
/// <b>No <c>ETag</c> on the answer.</b> §5 gives these four routes <c>200 {version}</c> and nothing else; the number in the
/// body is the tag the next write needs (AC19), so the response carries no header the contract does not declare.
/// </para>
/// </remarks>
internal static class AuthoringRestoreEndpoints
{
    /// <summary>Adds routes 6, 11, 18 and 24 to the authoring route group (13.1.1 §5).</summary>
    /// <param name="authoring">The group mapped at <c>/v1/projects/{p}/authoring</c>.</param>
    internal static void MapAuthoringRestoreEndpoints(RouteGroupBuilder authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);

        authoring.MapPost("/nodes/{n}/restore", RestoreNodeAsync)
            .Produces<AuthoringRestoreResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status412PreconditionFailed)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithSummary("Restore a soft-deleted feature node under If-Match (13.1.1 §5 route 6, AC8)");

        authoring.MapPost("/checklist-items/{i}/restore", RestoreChecklistItemAsync)
            .Produces<AuthoringRestoreResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status412PreconditionFailed)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithSummary("Restore a soft-deleted checklist item under If-Match (route 11, AC8)");

        authoring.MapPost("/manual-cases/{m}/restore", RestoreManualCaseAsync)
            .Produces<AuthoringRestoreResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status412PreconditionFailed)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithSummary("Restore a soft-deleted manual test case under If-Match (route 18, AC8)");

        authoring.MapPost("/versions/{v}/restore", RestoreVersionAsync)
            .Produces<AuthoringRestoreResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status412PreconditionFailed)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithSummary("Re-apply one stored version of the entity it describes (route 24, AC8)");
    }

    /// <summary>Route 6: <c>If-Match</c> and <c>Idempotency-Key</c>, answered with the node's new version.</summary>
    private static async Task<Result<AuthoringRestoreResponse>> RestoreNodeAsync(
        Guid p,
        Guid n,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (AuthoringEndpointSupport.IfMatchVersion(context, out var expectedVersion) is { } ifMatchError)
        {
            return AuthoringEndpointSupport.Failure<AuthoringRestoreResponse>(ifMatchError);
        }

        return await mediator.Send(
            new RestoreNodeCommand(p, n, expectedVersion, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Route 11: the same two headers, answered with the item's new version.</summary>
    private static async Task<Result<AuthoringRestoreResponse>> RestoreChecklistItemAsync(
        Guid p,
        Guid i,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (AuthoringEndpointSupport.IfMatchVersion(context, out var expectedVersion) is { } ifMatchError)
        {
            return AuthoringEndpointSupport.Failure<AuthoringRestoreResponse>(ifMatchError);
        }

        return await mediator.Send(
            new RestoreChecklistItemCommand(p, i, expectedVersion, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Route 18: the same two headers, answered with the case's new version.</summary>
    private static async Task<Result<AuthoringRestoreResponse>> RestoreManualCaseAsync(
        Guid p,
        Guid m,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (AuthoringEndpointSupport.IfMatchVersion(context, out var expectedVersion) is { } ifMatchError)
        {
            return AuthoringEndpointSupport.Failure<AuthoringRestoreResponse>(ifMatchError);
        }

        return await mediator.Send(
            new RestoreManualCaseCommand(p, m, expectedVersion, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Route 24: <c>Idempotency-Key</c> only — §5 declares no <c>If-Match</c> for this route.</summary>
    private static async Task<Result<AuthoringRestoreResponse>> RestoreVersionAsync(
        Guid p,
        Guid v,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(
            new RestoreVersionCommand(p, v, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
}
