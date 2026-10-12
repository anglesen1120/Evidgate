using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Ingestion.Application.Commands;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.Modules.Ingestion.Endpoints;

/// <summary>
/// The events route of 04.1.1 (00 §9.3): <c>POST /v1/runs/{r}/events</c>. The body is read with a hard byte
/// cap and handed to the handler as bytes; authorization is the C2 pipeline and the 00 §9.2 envelope is the
/// host's <c>ResultEndpointFilter</c>, exactly as <c>OrchestratorEndpoints</c> does it.
/// <para>
/// The route is exempt from <c>Idempotency-Key</c> (contracts/README.md): a resend must be answered with the
/// per-event <c>duplicate</c> acks, not a cached response, so the header is neither required nor read.
/// </para>
/// </summary>
public static class IngestionEndpoints
{
    /// <summary>
    /// The event-batch transport cap (00 §9.3: 1 MiB before decompression), the same number
    /// <c>event-envelope.v1</c>'s <c>x-max-bytes</c> declares; the handler re-checks it against the schema.
    /// </summary>
    internal const int MaxEventBatchBytes = 1_048_576;

    /// <summary>Maps the routes and returns the <c>/v1</c> group so the host can apply its filters (00 §5.5 C8).</summary>
    public static RouteGroupBuilder MapIngestionEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("events");

        group.MapPost("/runs/{r}/events", IngestEventBatchAsync)
            .Accepts<object>("application/json")
            .Produces<BatchAckResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .WithSummary("Atomically ingest an event batch of ≤ 500 events and ≤ 1 MiB (04.1.1)");

        return group;
    }

    private static async Task<Result<BatchAckResult>> IngestEventBatchAsync(
        Guid r,
        HttpContext context,
        IRequestPrincipalAccessor principals,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        // Nothing of the body is read for a caller who is nobody; parsing and validation run in the handler,
        // after the pipeline's permission check (04.1.1 §5).
        if (principals.Current is null)
        {
            return Result.Failure<BatchAckResult>(new Error(
                ApiErrorCode.Unauthenticated,
                "no authenticated producer could be resolved",
                StatusCodes.Status401Unauthorized));
        }

        // Gzip is 04.1.1-T3's: until the decode path exists, a compressed body is refused rather than read
        // as garbage. 422 request_invalid carries the reason at the offending header (the 00 §9.2 envelope
        // has no code for an unsupported encoding, and the task file allows either shape).
        if (context.Request.Headers.ContentEncoding.Count > 0
            && !context.Request.Headers.ContentEncoding.All(encoding =>
                string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase)))
        {
            return Result.Failure<BatchAckResult>(Error.Invalid(
                "the request body encoding is not supported",
                [new ErrorDetail("Content-Encoding", "unsupported", null)]));
        }

        var (bytes, tooLarge) = await ReadBodyAsync(context, MaxEventBatchBytes, cancellationToken).ConfigureAwait(false);
        if (tooLarge is not null)
        {
            return Result.Failure<BatchAckResult>(tooLarge);
        }

        return await mediator
            .Send(new IngestEventBatch(r, bytes, context.TraceIdentifier), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the request body with a hard cap, so an oversized body is refused before it is buffered whole
    /// (00 §5.5 edge). A <c>Content-Length</c> over the cap is refused without reading a byte, and a body
    /// that lies about its length (or is chunked) is refused as soon as the cap is passed; exactly
    /// <paramref name="cap"/> bytes is the accepted boundary (AC7). The buffer never grows past
    /// <c>cap + 1</c> bytes, which is all the evidence the decision needs.
    /// </summary>
    private static async Task<(ReadOnlyMemory<byte> Bytes, Error? TooLarge)> ReadBodyAsync(
        HttpContext context,
        int cap,
        CancellationToken cancellationToken)
    {
        var declared = context.Request.ContentLength;
        if (declared > cap)
        {
            return (default, TooLarge());
        }

        var buffer = new byte[declared is { } known ? (int)known + 1 : Math.Min(16 * 1024, cap + 1)];
        var length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                if (buffer.Length > cap)
                {
                    return (default, TooLarge());
                }

                Array.Resize(ref buffer, Math.Min(buffer.Length * 2, cap + 1));
            }

            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        return length > cap ? (default, TooLarge()) : (buffer.AsMemory(0, length), null);
    }

    private static Error TooLarge() => new(
        ApiErrorCode.BatchTooLarge,
        "the event batch exceeds the route's transport cap",
        StatusCodes.Status413PayloadTooLarge);
}
