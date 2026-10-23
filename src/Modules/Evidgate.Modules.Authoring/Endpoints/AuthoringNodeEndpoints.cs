using System.Text.Json;
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
/// Routes 1-5 of 13.1.1 §5 — the feature tree and the node writes — mapped on the shared
/// <c>/v1/projects/{p}/authoring</c> group (<see cref="AuthoringEndpoints"/>). The delegates bind 00 §9.1's parameters,
/// refuse a malformed request through <see cref="AuthoringEndpointSupport"/> and answer in <see cref="Result"/> terms:
/// authorization is the C2 pipeline (the <c>[Permission]</c> of each handler class), the tenant is the request's, and the
/// 00 §9.2 envelope is the host's <c>ResultEndpointFilter</c>.
/// </summary>
/// <remarks>
/// <b>The tree read carries the reorder precondition.</b> Route 1 answers <c>ETag: "order:sha256:&lt;hex&gt;"</c> — the tag
/// over the sorted <c>(id, version)</c> pairs of the listed sibling group (<c>AuthoringHashes.OrderHash</c>),
/// which is exactly what route 4's <c>If-Match</c> requires (AC2/AC19). It is a header, not a body member: the §5 shape of
/// route 1 is closed, and a client that reads a group can hand the tag straight back when it reorders it.
/// </remarks>
internal static class AuthoringNodeEndpoints
{
    /// <summary>The root-level members <c>POST /nodes</c> accepts (route 2).</summary>
    private static readonly string[] CreateMembers = ["parent_id", "kind", "title", "position"];

    /// <summary>The root-level members <c>PATCH /nodes/{n}</c> accepts (route 3). <c>kind</c> is identity, not a member.</summary>
    private static readonly string[] UpdateMembers = ["title", "parent_id", "position"];

    /// <summary>The root-level members <c>POST /nodes/{n}/reorder</c> accepts (route 4).</summary>
    private static readonly string[] ReorderMembers = ["ordered_ids"];

    /// <summary>The only values <c>?include=</c> accepts (route 1: <c>path</c> and <c>counts</c>; counts are always sent).</summary>
    private static readonly string[] IncludeValues = ["path", "counts"];

    /// <summary>
    /// Maps the five node routes on the authoring group.
    /// </summary>
    /// <param name="authoring">The <c>/v1/projects/{p}/authoring</c> group.</param>
    internal static void MapAuthoringNodeEndpoints(RouteGroupBuilder authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);

        authoring.MapGet("/tree", GetTreeAsync)
            .Produces<AuthoringTreeResponse>(StatusCodes.Status200OK)
            .WithSummary("One sibling page of the feature tree, or one node with its path and children (13.1.1)");

        authoring.MapPost("/nodes", CreateNodeAsync)
            .Accepts<CreateNodeRequest>("application/json")
            .Produces<CreateNodeResponse>(StatusCodes.Status201Created)
            .WithSummary("Create a feature node under the AC1 kind ladder (13.1.1)");

        authoring.MapPatch("/nodes/{n}", UpdateNodeAsync)
            .Accepts<UpdateNodeRequest>("application/json")
            .Produces<AuthoringChangeResponse>(StatusCodes.Status200OK)
            .WithSummary("Change a node's title, parent or position under If-Match (13.1.1)");

        authoring.MapPost("/nodes/{n}/reorder", ReorderNodesAsync)
            .Accepts<ReorderNodesRequest>("application/json")
            .Produces<ReorderNodesResponse>(StatusCodes.Status200OK)
            .WithSummary("Renumber a sibling group under its order ETag (13.1.1)");

        authoring.MapDelete("/nodes/{n}", DeleteNodeAsync)
            .Produces(StatusCodes.Status204NoContent)
            .WithSummary("Soft-delete a leaf node under If-Match (13.1.1)");
    }

    /// <summary>Route 1: the tree, its counts and — with <c>include=path</c> — the breadcrumb.</summary>
    private static async Task<Result<AuthoringTreeResponse>> GetTreeAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (AuthoringEndpointSupport.LimitError(context.Request.Query["limit"], out var limit) is { } limitError)
        {
            return Result.Failure<AuthoringTreeResponse>(limitError);
        }

        Guid? parentId = null;
        if (AuthoringEndpointSupport.Blank(context.Request.Query["parent_id"]) is { } rawParent)
        {
            if (!AuthoringEndpointSupport.TryParseGuid(rawParent, out var parsedParent))
            {
                return Result.Failure<AuthoringTreeResponse>(
                    Error.Invalid("parent_id must be the id of a node", [new ErrorDetail("parent_id", "invalid")]));
            }

            parentId = parsedParent;
        }

        Guid? nodeId = null;
        if (AuthoringEndpointSupport.Blank(context.Request.Query["node_id"]) is { } rawNode)
        {
            if (!AuthoringEndpointSupport.TryParseGuid(rawNode, out var parsedNode))
            {
                return Result.Failure<AuthoringTreeResponse>(
                    Error.Invalid("node_id must be the id of a node", [new ErrorDetail("node_id", "invalid")]));
            }

            nodeId = parsedNode;
        }

        if (parentId is not null && nodeId is not null)
        {
            // Two addresses, one page: route 1's members are alternatives (`node_id` answers one node plus its children,
            // `parent_id` a group's children), so an ambiguous request is refused instead of silently preferring one.
            return Result.Failure<AuthoringTreeResponse>(Error.Invalid(
                "node_id and parent_id address different pages and cannot be combined",
                [new ErrorDetail("node_id", "conflict")]));
        }

        var (includePath, includeError) = ReadInclude(context.Request.Query["include"]);
        if (includeError is not null)
        {
            return Result.Failure<AuthoringTreeResponse>(includeError);
        }

        var result = await mediator.Send(
            new GetAuthoringTreeQuery(
                p,
                parentId,
                nodeId,
                includePath,
                AuthoringEndpointSupport.Blank(context.Request.Query["cursor"]),
                limit),
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return Result.Failure<AuthoringTreeResponse>(result.Error!);
        }

        context.Response.Headers.ETag = $"\"{result.Value.OrderHash}\"";

        return Result.Success(result.Value.Body);
    }

    /// <summary>Route 2: <c>Idempotency-Key</c> only (AC18); a create answers 201 with the new id and version.</summary>
    private static async Task<Result<CreateNodeResponse>> CreateNodeAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var (body, readError) = await AuthoringEndpointSupport.ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);
        if (readError is not null)
        {
            return Result.Failure<CreateNodeResponse>(readError);
        }

        if (AuthoringEndpointSupport.UnknownMembers(body, CreateMembers, "the feature node create") is { } unknown)
        {
            return Result.Failure<CreateNodeResponse>(unknown);
        }

        CreateNodeRequest request;
        try
        {
            request = body.Deserialize<CreateNodeRequest>(AuthoringEndpointSupport.RequestJson)
                ?? new CreateNodeRequest(null, null, null, null);
        }
        catch (JsonException exception)
        {
            return Result.Failure<CreateNodeResponse>(Error.Invalid(
                "the body does not match the feature node shape",
                [new ErrorDetail(exception.Path ?? string.Empty, "invalid")]));
        }

        var result = await mediator.Send(
            new CreateNodeCommand(p, request.ParentId, request.Kind, request.Title, request.Position, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return result;
        }

        AuthoringEndpointSupport.SetVersionETag(context, result.Value.Version);

        return Result.Success(result.Value, StatusCodes.Status201Created);
    }

    /// <summary>Route 3: <c>If-Match</c> and <c>Idempotency-Key</c> (AC19, AC18).</summary>
    private static async Task<Result<AuthoringChangeResponse>> UpdateNodeAsync(
        Guid p,
        Guid n,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (AuthoringEndpointSupport.IfMatchVersion(context, out var expectedVersion) is { } ifMatchError)
        {
            return Result.Failure<AuthoringChangeResponse>(ifMatchError);
        }

        var (body, readError) = await AuthoringEndpointSupport.ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);
        if (readError is not null)
        {
            return Result.Failure<AuthoringChangeResponse>(readError);
        }

        if (AuthoringEndpointSupport.UnknownMembers(body, UpdateMembers, "the feature node update") is { } unknown)
        {
            return Result.Failure<AuthoringChangeResponse>(unknown);
        }

        UpdateNodeRequest request;
        try
        {
            request = body.Deserialize<UpdateNodeRequest>(AuthoringEndpointSupport.RequestJson)
                ?? new UpdateNodeRequest(null, null, null);
        }
        catch (JsonException exception)
        {
            return Result.Failure<AuthoringChangeResponse>(Error.Invalid(
                "the body does not match the feature node shape",
                [new ErrorDetail(exception.Path ?? string.Empty, "invalid")]));
        }

        var result = await mediator.Send(
            new UpdateNodeCommand(p, n, request.Title, request.ParentId, request.Position, expectedVersion, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            AuthoringEndpointSupport.SetVersionETag(context, result.Value.Version);
        }

        return result;
    }

    /// <summary>Route 4: the order ETag of route 1 plus <c>Idempotency-Key</c> (AC2, AC19).</summary>
    private static async Task<Result<ReorderNodesResponse>> ReorderNodesAsync(
        Guid p,
        Guid n,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (AuthoringEndpointSupport.IfMatchOrder(context, out var orderHash) is { } ifMatchError)
        {
            return Result.Failure<ReorderNodesResponse>(ifMatchError);
        }

        var (body, readError) = await AuthoringEndpointSupport.ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);
        if (readError is not null)
        {
            return Result.Failure<ReorderNodesResponse>(readError);
        }

        if (AuthoringEndpointSupport.UnknownMembers(body, ReorderMembers, "the node reorder") is { } unknown)
        {
            return Result.Failure<ReorderNodesResponse>(unknown);
        }

        ReorderNodesRequest request;
        try
        {
            request = body.Deserialize<ReorderNodesRequest>(AuthoringEndpointSupport.RequestJson)
                ?? new ReorderNodesRequest(null);
        }
        catch (JsonException exception)
        {
            return Result.Failure<ReorderNodesResponse>(Error.Invalid(
                "the body does not match the reorder shape",
                [new ErrorDetail(exception.Path ?? string.Empty, "invalid")]));
        }

        if (request.OrderedIds is null)
        {
            return Result.Failure<ReorderNodesResponse>(Error.Invalid(
                "ordered_ids is required",
                [new ErrorDetail("/ordered_ids", "required")]));
        }

        return await mediator.Send(
            new ReorderNodesCommand(p, n, request.OrderedIds, orderHash, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Route 5: <c>If-Match</c>, an optional <c>{reason?}</c> body and a 204 (AC7, AC19).</summary>
    private static async Task<Result> DeleteNodeAsync(
        Guid p,
        Guid n,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (AuthoringEndpointSupport.IfMatchVersion(context, out var expectedVersion) is { } ifMatchError)
        {
            return Result.Failure(ifMatchError);
        }

        var (body, readError) = await AuthoringEndpointSupport.ReadOptionalBodyAsync(context, cancellationToken).ConfigureAwait(false);
        if (readError is not null)
        {
            return Result.Failure(readError);
        }

        string? reason = null;
        if (body.ValueKind != JsonValueKind.Undefined)
        {
            if (AuthoringEndpointSupport.UnknownMembers(body, ["reason"], "the feature node delete") is { } unknown)
            {
                return Result.Failure(unknown);
            }

            var (parsedReason, reasonError) = ReadReason(body);
            if (reasonError is not null)
            {
                return Result.Failure(reasonError);
            }

            reason = parsedReason;
        }

        var result = await mediator.Send(
            new DeleteNodeCommand(p, n, reason, expectedVersion, AuthoringEndpointSupport.IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Result.Success(StatusCodes.Status204NoContent) : Result.Failure(result.Error!);
    }

    /// <summary>
    /// Reads <c>?include=</c> (route 1, [DECISION D-S55-Q2]): a comma-separated subset of <see cref="IncludeValues"/>,
    /// absent or empty is "nothing extra". <c>counts</c> is accepted and changes nothing — §5's shape always carries the two
    /// counts — and an unknown token is the 422 of <c>/include</c> <c>invalid</c> rather than a silently ignored value.
    /// </summary>
    private static (bool Path, Error? Error) ReadInclude(string? raw)
    {
        var includePath = false;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return (false, null);
        }

        foreach (var token in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IncludeValues.Contains(token, StringComparer.Ordinal))
            {
                return (false, Error.Invalid(
                    "include accepts path and counts",
                    [new ErrorDetail("/include", "invalid")]));
            }

            includePath |= string.Equals(token, "path", StringComparison.Ordinal);
        }

        return (includePath, null);
    }

    /// <summary>The optional <c>{reason?}</c> of a delete: a string 1..500 characters, or absent.</summary>
    private static (string? Reason, Error? Error) ReadReason(JsonElement body)
    {
        if (!body.TryGetProperty("reason", out var reason) || reason.ValueKind == JsonValueKind.Null)
        {
            return (null, null);
        }

        if (reason.ValueKind != JsonValueKind.String)
        {
            return (null, Error.Invalid("reason must be text", [new ErrorDetail("/reason", "invalid_type")]));
        }

        var text = reason.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 500)
        {
            return (null, Error.Invalid(
                "reason must be 1..500 characters",
                [new ErrorDetail("/reason", "invalid_length")]));
        }

        return (text, null);
    }
}
