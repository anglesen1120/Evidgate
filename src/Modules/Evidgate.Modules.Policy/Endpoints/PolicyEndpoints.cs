using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Application;
using Evidgate.Modules.Policy.Application.Commands;
using Evidgate.Modules.Policy.Application.Queries;
using Evidgate.Modules.Policy.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.Modules.Policy.Endpoints;

/// <summary>
/// The policy routes of 07.1.1 §5. The body is read with a hard byte cap and validated against its contract schema
/// before anything binds (00 §5.5 edge); authorization is the C2 pipeline and the 00 §9.2 envelope is the host's
/// <c>ResultEndpointFilter</c>.
/// </summary>
public static class PolicyEndpoints
{
    /// <summary>The policy-revision transport cap (the schema's <c>x-max-bytes</c>).</summary>
    internal const int MaxPolicyBytes = 64 * 1024;

    /// <summary>
    /// The activation transport cap (07.1.1-T4 F2c): the body is a revision id and an optional mode, so 4 KiB
    /// is ample and the 64 KiB revision cap is not read for a ~100-byte request.
    /// </summary>
    internal const int MaxActivationBytes = 4 * 1024;

    internal const int DefaultLimit = 50;

    internal const int MaxLimit = 200;

    /// <summary>Maps the routes and returns the <c>/v1</c> group so the host can apply its filters (00 §5.5 C8).</summary>
    public static RouteGroupBuilder MapPolicyEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("policies");

        group.MapPost("/projects/{p}/policies", CreatePolicyRevisionAsync)
            .Produces<PolicyRevisionDto>(StatusCodes.Status201Created)
            .Produces<PolicyRevisionDto>(StatusCodes.Status200OK)
            .WithSummary("Create an immutable, hashed policy revision (07.1.1)");

        group.MapPost("/projects/{p}/policy-activation", ActivatePolicyAsync)
            .Produces<PolicyActivationDto>(StatusCodes.Status200OK)
            .WithSummary("Activate a policy revision for the project; runs created afterwards pin it (07.1.1)");

        group.MapGet("/projects/{p}/policy-activation", GetPolicyActivationAsync)
            .Produces<PolicyActivationStateDto>(StatusCodes.Status200OK)
            .WithSummary("The project's policy activation; 404 when it has never been activated (F18)");

        group.MapGet("/policies/{id}", GetPolicyRevisionAsync)
            .Produces<PolicyRevisionDto>(StatusCodes.Status200OK)
            .WithSummary("One policy revision; ETag is its id (07.1.1)");

        group.MapGet("/policies/{id}/revisions", ListPolicyRevisionsAsync)
            .Produces<PolicyRevisionListResponse>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of a project's policy revisions, append-only order (07.1.1)");

        group.MapGet("/projects/{p}/policies/head", GetPolicyHeadAsync)
            .Produces<PolicyRevisionSummaryDto>(StatusCodes.Status200OK)
            .WithSummary("The project's newest policy revision; ETag is its id (F18)");

        return group;
    }

    private static async Task<Result<PolicyRevisionDto>> CreatePolicyRevisionAsync(
        Guid p,
        HttpContext context,
        IRequestPrincipalAccessor principals,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        // Nothing of the body is read for a caller who is nobody; parsing and the schema run in the handler, after the
        // pipeline's permission check.
        if (principals.Current is null)
        {
            return Result.Failure<PolicyRevisionDto>(Unauthenticated);
        }

        var (bytes, tooLarge) = await ReadBodyAsync(context, MaxPolicyBytes, cancellationToken).ConfigureAwait(false);
        if (tooLarge is not null)
        {
            return Result.Failure<PolicyRevisionDto>(tooLarge);
        }

        var result = await mediator.Send(new CreatePolicyRevisionCommand(p, bytes, IfMatch(context)), cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess)
        {
            context.Response.Headers.ETag = $"\"{result.Value.Id}\"";
        }

        return result;
    }

    private static async Task<Result<PolicyActivationDto>> ActivatePolicyAsync(
        Guid p,
        HttpContext context,
        IRequestPrincipalAccessor principals,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        // Same edge as the append route: an anonymous caller is refused before any byte of the body is read,
        // and the pipeline's permission check runs in the handler.
        if (principals.Current is null)
        {
            return Result.Failure<PolicyActivationDto>(Unauthenticated);
        }

        var (bytes, tooLarge) = await ReadBodyAsync(context, MaxActivationBytes, cancellationToken).ConfigureAwait(false);
        if (tooLarge is not null)
        {
            return Result.Failure<PolicyActivationDto>(tooLarge);
        }

        return await mediator.Send(new ActivatePolicyCommand(p, bytes, IdempotencyKey(context)), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<PolicyActivationStateDto>> GetPolicyActivationAsync(
        Guid p,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        // The handler reads the pointer; the 404 (never activated, unknown, soft-deleted or another tenant's
        // project) and the 403 scope refusal are its answers, not this edge's.
        return await mediator.Send(new GetPolicyActivationQuery(p), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<PolicyRevisionDto>> GetPolicyRevisionAsync(
        Guid id,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        // The internal query stays the in-process read surface (07.2.2); the endpoint maps its view to the wire DTO.
        var result = await mediator.Send(new GetPolicyRevisionQuery(id), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Result.Failure<PolicyRevisionDto>(result.Error!);
        }

        context.Response.Headers.ETag = $"\"{result.Value.Id}\"";
        return Result.Success(ToDto(result.Value));
    }

    private static async Task<Result<PolicyRevisionListResponse>> ListPolicyRevisionsAsync(
        Guid id,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (LimitError(context.Request.Query["limit"], out var limit) is { } error)
        {
            return Result.Failure<PolicyRevisionListResponse>(error);
        }

        var result = await mediator.Send(
            new ListPolicyRevisionsQuery(id, Blank(context.Request.Query["cursor"]), limit, IfMatch(context)),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Result.Failure<PolicyRevisionListResponse>(result.Error!);
        }

        if (result.Value.HeadId is { } head)
        {
            context.Response.Headers.ETag = $"\"{head}\"";
        }

        return Result.Success(result.Value.Response);
    }

    private static async Task<Result<PolicyRevisionSummaryDto>> GetPolicyHeadAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetPolicyHeadQuery(p), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result;
        }

        // The same ETag GET /v1/policies/{id} issues: the head's own revision id.
        context.Response.Headers.ETag = $"\"{result.Value.Id}\"";
        return result;
    }

    private static Error Unauthenticated => new(ApiErrorCode.Unauthenticated, "authentication is required", StatusCodes.Status401Unauthorized);

    /// <summary>The stored revision as the wire DTO; the body text is the database's <c>jsonb</c> rendering.</summary>
    private static PolicyRevisionDto ToDto(PolicyRevisionView view)
    {
        using var document = JsonDocument.Parse(view.Body);

        return new PolicyRevisionDto(
            view.Id,
            view.ProjectId,
            view.RevisionNo,
            view.Hash,
            view.State,
            document.RootElement.Clone(),
            view.CreatedBy,
            view.CreatedAt,
            view.Version);
    }

    /// <summary>The raw body, refused with 413 as soon as it passes <paramref name="limit"/> bytes (never buffered beyond it).</summary>
    private static async Task<(ReadOnlyMemory<byte> Bytes, Error? TooLarge)> ReadBodyAsync(HttpContext context, int limit, CancellationToken cancellationToken)
    {
        var tooLarge = new Error(ApiErrorCode.PayloadTooLarge, "the request body is too large", StatusCodes.Status413PayloadTooLarge);
        var declared = context.Request.ContentLength;
        if (declared > limit)
        {
            return (default, tooLarge);
        }

        var buffer = new byte[declared is { } known ? (int)known + 1 : Math.Min(16 * 1024, limit + 1)];
        var length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                if (buffer.Length > limit)
                {
                    return (default, tooLarge);
                }

                Array.Resize(ref buffer, Math.Min(buffer.Length * 2, limit + 1));
            }

            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        return length > limit ? (default, tooLarge) : (buffer.AsMemory(0, length), null);
    }

    /// <summary>The <c>If-Match</c> header, or null when the caller sent none (00 §9.1).</summary>
    private static string? IfMatch(HttpContext context) => Blank(context.Request.Headers.IfMatch.ToString());

    /// <summary>The <c>Idempotency-Key</c> header (00 §9.1); an absent header is the empty string the C2 stage refuses.</summary>
    private static string IdempotencyKey(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString();

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>The <c>limit</c> query parameter, 1–200, default 50 (00 §9.1); anything else is 422.</summary>
    private static Error? LimitError(string? raw, out int limit)
    {
        limit = DefaultLimit;

        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 1
            || parsed > MaxLimit)
        {
            return PolicyErrors.LimitInvalid(MaxLimit);
        }

        limit = parsed;
        return null;
    }
}
