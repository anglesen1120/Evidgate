using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Policy.Application.Waivers;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Policy.Endpoints;

/// <summary>
/// The waiver routes of 07.3.1 §5. Like the catalog and orchestrator routes the delegates only bind 00 §9.1's
/// parameters and answer in <see cref="Result"/> terms: authorization is the C2 pipeline, the tenant is the
/// request's (never the URL's), and the 00 §9.2 envelope is the host's <c>ResultEndpointFilter</c>.
/// </summary>
public static class WaiverEndpoints
{
    internal const int DefaultLimit = 50;
    internal const int MaxLimit = 200;

    /// <summary>Maps the waiver routes and returns the <c>/v1</c> group so the host can apply its filters (00 §5.5 C8).</summary>
    public static RouteGroupBuilder MapWaiverEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("policy");

        group.MapPost("/waivers", CreateWaiverAsync)
            .Accepts<WaiverCreateRequest>("application/json")
            .Produces<WaiverDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Request a waiver for a case or suite (07.3.1)");

        group.MapPost("/waivers/{w}/approve", ApproveWaiverAsync)
            .Produces<WaiverDto>(StatusCodes.Status200OK)
            .WithSummary("Approve a waiver; the approver must differ from the requester (07.3.1)");

        group.MapPost("/waivers/{w}/revoke", RevokeWaiverAsync)
            .Produces<WaiverDto>(StatusCodes.Status200OK)
            .WithSummary("Revoke a waiver before or after approval (07.3.1)");

        group.MapGet("/projects/{p}/waivers", ListWaiversAsync)
            .Produces<WaiverPage>(StatusCodes.Status200OK)
            .WithSummary("Cursor page of a project's waivers (07.3.1)");

        return group;
    }

    private static async Task<Result<WaiverDto>> CreateWaiverAsync(
        WaiverCreateRequest request,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(
            new CreateWaiverCommand(request.ProjectId, request.Scope, request.TargetId, request.Reason, request.ExpiresAt, IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);

    private static async Task<Result<WaiverDto>> ApproveWaiverAsync(
        Guid w,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(new ApproveWaiverCommand(w, IdempotencyKey(context)), cancellationToken).ConfigureAwait(false);

    private static async Task<Result<WaiverDto>> RevokeWaiverAsync(
        Guid w,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(new RevokeWaiverCommand(w, IdempotencyKey(context)), cancellationToken).ConfigureAwait(false);

    private static async Task<Result<WaiverPage>> ListWaiversAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (LimitError(context.Request.Query["limit"], out var limit) is { } error)
        {
            return Result.Failure<WaiverPage>(error);
        }

        return await mediator.Send(
            new ListWaiversQuery(p, Blank(context.Request.Query["cursor"]), limit),
            cancellationToken).ConfigureAwait(false);
    }

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string IdempotencyKey(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString();

    private static Error? LimitError(string? raw, out int limit)
    {
        limit = DefaultLimit;

        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1 || parsed > MaxLimit)
        {
            return Error.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"limit must be an integer between 1 and {MaxLimit} (00 §9.1)"),
                [new ErrorDetail("limit", "out_of_range")]);
        }

        limit = parsed;

        return null;
    }
}
