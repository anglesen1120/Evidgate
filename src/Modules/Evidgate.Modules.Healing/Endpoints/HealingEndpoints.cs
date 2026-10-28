using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Healing.Application;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Healing.Endpoints;

/// <summary>
/// The healing switch routes of 08.6.3 §5. Like the policy and waiver routes the delegates only bind 00 §9.1's
/// parameters and answer in <see cref="Result"/> terms: authorization is the C2 pipeline (<c>healing:configure</c> on
/// the write, <c>healing:triage</c> on the read), the tenant is the request's, and the 00 §9.2 envelope is the host's
/// <c>ResultEndpointFilter</c>.
/// </summary>
public static class HealingEndpoints
{
    /// <summary>Cap of <c>PUT</c>'s body (the route's transport bound, <c>healing-switch-set.v1</c>'s <c>x-max-bytes</c>).</summary>
    internal const int MaxSwitchBytes = 4 * 1024;

    /// <summary>Maps the switch routes and returns the <c>/v1</c> group so the host can apply its filters (00 §5.5 C8).</summary>
    /// <param name="routes">The route builder of the host.</param>
    public static RouteGroupBuilder MapHealingEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("healing");

        group.MapGet("/healing/switch", GetHealingSwitchAsync)
            .Produces<HealingSwitchResolutionView>(StatusCodes.Status200OK)
            .WithSummary("Resolved healing availability per scope (most restrictive wins; 08.6.3)");

        group.MapPut("/healing/switch", SetHealingSwitchAsync)
            .Produces<HealingSwitchView>(StatusCodes.Status200OK)
            .WithSummary("Set the healing switch of the tenant or a project (enabled|shadow|disabled only; 08.6.3)");

        return group;
    }

    /// <summary>
    /// <c>GET /v1/healing/switch?project_id=</c>: the three scope rows (missing ones null), the resolved mode and the
    /// dependency observations.
    /// </summary>
    private static async Task<Result<HealingSwitchResolutionView>> GetHealingSwitchAsync(
        [FromQuery(Name = "project_id")] Guid? project_id,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(new GetHealingSwitchQuery(project_id), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// <c>PUT /v1/healing/switch</c>: reads the body under the route's cap, requires the <c>Idempotency-Key</c> header
    /// (00 §9.1) and hands the bytes to the handler, which validates them against the contract schema.
    /// </summary>
    private static async Task<Result<HealingSwitchView>> SetHealingSwitchAsync(
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrEmpty(key))
        {
            return Result.Failure<HealingSwitchView>(Error.BadRequest(
                "the request needs an Idempotency-Key header",
                [new ErrorDetail("Idempotency-Key", "required")]));
        }

        var (bytes, tooLarge) = await ReadBodyAsync(context, MaxSwitchBytes, cancellationToken).ConfigureAwait(false);
        if (tooLarge is not null)
        {
            return Result.Failure<HealingSwitchView>(tooLarge);
        }

        return await mediator.Send(new SetHealingSwitchCommand(bytes, key), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the body under <paramref name="limit"/> bytes (413 rather than buffering a hostile body), the shape the
    /// policy routes use.
    /// </summary>
    private static async Task<(ReadOnlyMemory<byte> Bytes, Error? TooLarge)> ReadBodyAsync(
        HttpContext context,
        int limit,
        CancellationToken cancellationToken)
    {
        var tooLarge = new Error(ApiErrorCode.PayloadTooLarge, "the request body is too large", StatusCodes.Status413PayloadTooLarge);

        if (context.Request.ContentLength > limit)
        {
            return (default, tooLarge);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(4096, limit + 1)];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return (default, tooLarge);
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return (buffer.ToArray(), null);
    }
}
