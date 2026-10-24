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
/// Routes 7-10 of 13.1.1 §5 — the checklist item CRUD: a keyset page, a create, an edit and a soft delete. Thin by
/// design: the edge parses and member-checks the request (<see cref="AuthoringEndpointSupport"/>), the command owns
/// every decision, and the host's <c>ResultEndpointFilter</c> renders the 00 §9.2 envelope once (AC16/AC18/AC19).
/// </summary>
/// <remarks>
/// <c>node_id</c> on the PATCH is refused as an unknown member on purpose: an item does not move between nodes on
/// this route (13.1.1 §5 route 9), and the writer owns every placement rule (AC3). The <c>ETag</c> of a create or an
/// edit is the row's new version, so the next write needs no extra GET (AC19).
/// </remarks>
internal static class AuthoringChecklistEndpoints
{
    /// <summary>Adds routes 7-10 to the authoring route group (13.1.1 §5).</summary>
    /// <param name="authoring">The group mapped at <c>/v1/projects/{p}/authoring</c>.</param>
    internal static void MapAuthoringChecklistEndpoints(RouteGroupBuilder authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);

        authoring.MapGet("/checklist-items", ListAsync)
            .Produces<ChecklistItemListResponse>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of a project's checklist items, ordered (position, id) (13.1.1)");

        // ASP.NET derives a response for the default status (200) from the delegate's return type whenever the declared
        // success status is not 200, and that type is the C8 `Result<T>` wrapper — never the wire shape. The bodiless
        // `.Produces(200)` replaces that inferred response so the wrapper never reaches the served OpenAPI document
        // (`ServedDocumentTests` diffs the served schema names against `contracts/openapi.yaml`).
        authoring.MapPost("/checklist-items", CreateAsync)
            .Accepts<CreateChecklistItemRequest>("application/json")
            .Produces<CreateChecklistItemResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Create a checklist item under a category or sub-category node (13.1.1)");

        authoring.MapPatch("/checklist-items/{i}", UpdateAsync)
            .Accepts<UpdateChecklistItemRequest>("application/json")
            .Produces<AuthoringChangeResponse>(StatusCodes.Status200OK)
            .WithSummary("Change a checklist item under If-Match; a move into retired needs a reason (13.1.1)");

        authoring.MapDelete("/checklist-items/{i}", DeleteAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Soft-delete a checklist item under If-Match (13.1.1)");
    }

    /// <summary>Route 7: one page, optionally narrowed by <c>node_id</c>, <c>state</c> and <c>priority</c>.</summary>
    private static async Task<Result<ChecklistItemListResponse>> ListAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (AuthoringEndpointSupport.LimitError(context.Request.Query["limit"], out var limit) is { } limitError)
        {
            return AuthoringEndpointSupport.Failure<ChecklistItemListResponse>(limitError);
        }

        Guid? nodeId = null;
        if (AuthoringEndpointSupport.Blank(context.Request.Query["node_id"]) is { } rawNodeId)
        {
            if (!AuthoringEndpointSupport.TryParseGuid(rawNodeId, out var parsed))
            {
                return Result.Failure<ChecklistItemListResponse>(
                    Error.Invalid("node_id must be the id of a node", [new ErrorDetail("/node_id", "invalid")]));
            }

            nodeId = parsed;
        }

        return await mediator.Send(
            new ListChecklistItemsQuery(
                p,
                nodeId,
                AuthoringEndpointSupport.Blank(context.Request.Query["state"]),
                AuthoringEndpointSupport.Blank(context.Request.Query["priority"]),
                AuthoringEndpointSupport.Blank(context.Request.Query["cursor"]),
                limit),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Route 8: the body is read strictly (unknown members refused) and answered with 201 and the new ETag.</summary>
    private static async Task<Result<CreateChecklistItemResponse>> CreateAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var (body, readError) = await AuthoringEndpointSupport
            .ReadBodyAsync(context, cancellationToken)
            .ConfigureAwait(false);

        if (readError is not null)
        {
            return AuthoringEndpointSupport.Failure<CreateChecklistItemResponse>(readError);
        }

        if (AuthoringEndpointSupport.UnknownMembers(body, ChecklistItemFields.CreateMembers, "the checklist item create") is { } unknown)
        {
            return AuthoringEndpointSupport.Failure<CreateChecklistItemResponse>(unknown);
        }

        var result = await mediator.Send(
            new CreateChecklistItemCommand(p, body, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess && result.SuccessValue is CreateChecklistItemResponse created)
        {
            AuthoringEndpointSupport.SetVersionETag(context, created.Version);
        }

        return result;
    }

    /// <summary>Route 9: <c>If-Match</c> and <c>Idempotency-Key</c> are required (AC19, AC18).</summary>
    private static async Task<Result<AuthoringChangeResponse>> UpdateAsync(
        Guid p,
        Guid i,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (AuthoringEndpointSupport.IfMatchVersion(context, out var expectedVersion) is { } ifMatchError)
        {
            return AuthoringEndpointSupport.Failure<AuthoringChangeResponse>(ifMatchError);
        }

        var (body, readError) = await AuthoringEndpointSupport
            .ReadBodyAsync(context, cancellationToken)
            .ConfigureAwait(false);

        if (readError is not null)
        {
            return AuthoringEndpointSupport.Failure<AuthoringChangeResponse>(readError);
        }

        if (AuthoringEndpointSupport.UnknownMembers(body, ChecklistItemFields.UpdateMembers, "the checklist item update") is { } unknown)
        {
            return AuthoringEndpointSupport.Failure<AuthoringChangeResponse>(unknown);
        }

        var result = await mediator.Send(
            new UpdateChecklistItemCommand(p, i, expectedVersion, body, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess && result.SuccessValue is AuthoringChangeResponse changed)
        {
            AuthoringEndpointSupport.SetVersionETag(context, changed.Version);
        }

        return result;
    }

    /// <summary>
    /// Route 10: the same two headers, an optional <c>{reason?}</c> body and a 204. The version moves, so a caller that
    /// wants the tombstone's number reads the entity's versions (AC9) instead of receiving it here.
    /// </summary>
    private static async Task<Result> DeleteAsync(
        Guid p,
        Guid i,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (AuthoringEndpointSupport.IfMatchVersion(context, out var expectedVersion) is { } ifMatchError)
        {
            return Result.Failure(ifMatchError);
        }

        var (body, readError) = await AuthoringEndpointSupport
            .ReadOptionalBodyAsync(context, cancellationToken)
            .ConfigureAwait(false);

        if (readError is not null)
        {
            return Result.Failure(readError);
        }

        if (body.ValueKind != System.Text.Json.JsonValueKind.Undefined
            && AuthoringEndpointSupport.UnknownMembers(body, ["reason"], "the checklist item delete") is { } unknown)
        {
            return Result.Failure(unknown);
        }

        var (reason, reasonError) = ChecklistItemFields.ReadDeleteReason(body);
        if (reasonError is not null)
        {
            return Result.Failure(reasonError);
        }

        return await mediator.Send(
            new DeleteChecklistItemCommand(
                p,
                i,
                expectedVersion,
                reason,
                AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }
}
