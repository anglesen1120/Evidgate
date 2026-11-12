using Evidgate.BuildingBlocks.Contracts.Validation;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using Evidgate.BuildingBlocks.Observability.Correlation;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Ingestion.Application;
using Evidgate.Modules.Ingestion.Application.Commands;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Usage.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Ingestion.Endpoints;

/// <summary>
/// The events route of 04.1.1 (00 §9.3): <c>POST /v1/runs/{r}/events</c>. The body is read with a hard byte
/// cap and handed to the handler as bytes; authorization is the C2 pipeline and the 00 §9.2 envelope is the
/// host's <c>ResultEndpointFilter</c>, exactly as <c>OrchestratorEndpoints</c> does it.
/// <para>
/// 04.1.1-T3 adds the gzip path: the 1 MiB cap applies to the bytes as sent, so a compressed body is capped
/// on its compressed size, and a <c>Content-Encoding: gzip</c> body is then decoded under the two boot-bound
/// caps of <see cref="IngestionLimits"/> before the handler ever parses it. Every refusal is counted and
/// logged once (04.1.1 §5/§8).
/// </para>
/// <para>
/// The route is exempt from <c>Idempotency-Key</c> (contracts/README.md): a resend must be answered with the
/// per-event <c>duplicate</c> acks, not a cached response, so the header is neither required nor read.
/// </para>
/// </summary>
public static partial class IngestionEndpoints
{
    /// <summary>
    /// The event-batch transport cap (00 §9.3: 1 MiB before decompression), the same number
    /// <c>event-envelope.v1</c>'s <c>x-max-bytes</c> declares; the handler re-checks it against the schema.
    /// It is a contract constant, not an option, and it caps a <c>Content-Encoding: gzip</c> body on its
    /// compressed size exactly as it caps an identity body.
    /// </summary>
    internal const int MaxEventBatchBytes = 1_048_576;

    /// <summary>Maps the routes and returns the <c>/v1</c> group so the host can apply its filters (00 §5.5 C8).</summary>
    public static RouteGroupBuilder MapIngestionEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("events");

        group.MapPost("/runs/{r}/events", IngestEventBatchAsync)
            .Accepts<object>("application/json")
            .WithContractBodyLimit(ContractSchemaKeys.EventEnvelope)
            .Produces<BatchAckResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .WithSummary("Atomically ingest an event batch of ≤ 500 events and ≤ 1 MiB (04.1.1)");

        return group;
    }

    /// <summary>
    /// Admits, reads and hands one batch to the handler. The burst limit is charged before the
    /// body is read and before the pipeline's permission check: a caller over its budget is
    /// refused cheaply there by design, and every refusal is counted once. A caller the limiter
    /// cannot key on is left to the pipeline and the handler, which refuse it without a write.
    /// </summary>
    private static async Task<Result<BatchAckResult>> IngestEventBatchAsync(
        Guid r,
        HttpContext context,
        IRequestPrincipalAccessor principals,
        IMediator mediator,
        IngestQuotaVeto quotaVeto,
        IQuotaBlockStamp blockStamp,
        CancellationToken cancellationToken)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(IngestionEndpoints));

        // The quota block's Retry-After is decided inside the handler's tenant transaction; the carrier's
        // per-request slot is what brings it back out to the header this response writes.
        using var vetoScope = quotaVeto.BeginRequest();

        // AC7: a refusal never reaches the handler's module span, so the run id and the request id
        // go on the request span first. Neither the token id nor any other credential value is tagged.
        TagRequestSpan(context, r);

        // Nothing of the body is read for a caller who is nobody; parsing and validation run in the handler,
        // after the pipeline's permission check (04.1.1 §5).
        if (principals.Current is null)
        {
            IngestBatchRejection.Record(logger, IngestBatchRejection.Unauthenticated, context.TraceIdentifier, r, 0, 0);

            return Result.Failure<BatchAckResult>(new Error(
                ApiErrorCode.Unauthenticated,
                "no authenticated producer could be resolved",
                StatusCodes.Status401Unauthorized));
        }

        var limits = context.RequestServices.GetRequiredService<IOptions<IngestionLimits>>().Value;

        // The burst limit runs before a byte of the body is read, keyed by the authenticated project
        // and token id only: no header or body value moves a caller into another partition. A caller
        // whose key cannot be resolved is not refused here — the limiter never answers before the
        // pipeline does — and the handler refuses it without a write.
        if (TryRatePartition(principals.Current, out var partition))
        {
            var limiter = context.RequestServices.GetRequiredService<IngestionRateLimiter>();

            // One charge against both budgets (the token's own and the project's), committed only when
            // both admit, so a refused token never spends the budget its siblings share.
            var admission = limiter.AcquireBoth(partition.ProjectId, partition.TokenId);

            if (!admission.Allowed)
            {
                context.Response.Headers.RetryAfter = RetryAfterHeader.Format(admission.RetryAfterSeconds);
                IngestBatchRejection.Record(logger, IngestBatchRejection.RateLimited, context.TraceIdentifier, r, 0, 0);

                return Result.Failure<BatchAckResult>(new Error(
                    ApiErrorCode.RateLimited,
                    "the project's event ingest rate was exceeded",
                    StatusCodes.Status429TooManyRequests,
                    Retryable: true));
            }
        }

        // Content-Encoding must be exactly one of gzip/identity (case-insensitive). Anything else — an
        // unknown coding or a stacked list — is refused at the header: the 00 §9.2 envelope has no code for
        // an unsupported encoding, so the detail names it (04.1.1-T3; no new wire code).
        var encodings = context.Request.Headers.ContentEncoding;
        var gzip = false;
        if (encodings.Count > 0)
        {
            if (encodings.Count == 1 && string.Equals(encodings[0], "identity", StringComparison.OrdinalIgnoreCase))
            {
                gzip = false;
            }
            else if (encodings.Count == 1 && string.Equals(encodings[0], "gzip", StringComparison.OrdinalIgnoreCase))
            {
                gzip = true;
            }
            else
            {
                IngestBatchRejection.Record(
                    logger, IngestBatchRejection.EncodingUnsupported, context.TraceIdentifier, r, context.Request.ContentLength ?? 0, 0);
                return Result.Failure<BatchAckResult>(Error.Invalid(
                    "the request body encoding is not supported",
                    [new ErrorDetail("Content-Encoding", "unsupported", null)]));
            }
        }

        // The 1 MiB cap applies to the bytes as sent, so a compressed body is capped on its compressed size
        // and the decoded size is bounded by the two IngestionLimits caps below. The rented buffer is the
        // request's one large allocation (04.1.1 §8 P4) and is returned once the handler is done with it.
        var buffer = ArrayPool<byte>.Shared.Rent(MaxEventBatchBytes + 1);
        try
        {
            var (bytes, bytesIn, tooLarge) = await ReadBodyAsync(context, buffer, MaxEventBatchBytes, cancellationToken)
                .ConfigureAwait(false);
            if (tooLarge is not null)
            {
                IngestBatchRejection.Record(logger, IngestBatchRejection.BodyTooLarge, context.TraceIdentifier, r, bytesIn, 0);
                return Result.Failure<BatchAckResult>(tooLarge);
            }

            if (!gzip)
            {
                return await CountedAsync(
                        await mediator
                            .Send(new IngestEventBatch(r, bytes, context.TraceIdentifier, bytesIn, bytesIn), cancellationToken)
                            .ConfigureAwait(false),
                        logger,
                        context,
                        r,
                        bytesIn,
                        quotaVeto,
                        blockStamp,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            using var decoded = EventBodyDecoder.DecodeGzip(bytes, limits);
            if (decoded.Reason is { } reason)
            {
                IngestBatchRejection.Record(logger, reason, context.TraceIdentifier, r, bytesIn, decoded.ExpandedBytes);
                return reason == IngestBatchRejection.GzipInvalid
                    ? Result.Failure<BatchAckResult>(Error.Invalid(
                        "the gzip request body could not be decoded",
                        [new ErrorDetail("Content-Encoding", "invalid_gzip", null)]))
                    : Result.Failure<BatchAckResult>(TooLarge());
            }

            return await CountedAsync(
                    await mediator
                        .Send(
                            new IngestEventBatch(r, decoded.Bytes, context.TraceIdentifier, bytesIn, decoded.ExpandedBytes),
                            cancellationToken)
                        .ConfigureAwait(false),
                    logger,
                    context,
                    r,
                    bytesIn,
                    quotaVeto,
                    blockStamp,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // The buffer held the tenant's request body, so it is cleared before it is pooled.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <summary>
    /// Counts one rejection for a failure the pipeline or handler returned, under
    /// <see cref="IngestBatchRejection.ReasonFor"/>'s closed vocabulary. A
    /// <c>batch_too_large</c> is skipped because the cap that refused it already counted it. A quota refusal
    /// carries the gate's own <c>Retry-After</c> and its stamp, both of which the carrier holds for this request.
    /// </summary>
    private static async Task<Result<BatchAckResult>> CountedAsync(
        Result<BatchAckResult> result,
        ILogger logger,
        HttpContext context,
        Guid runId,
        int bytesIn,
        IngestQuotaVeto quotaVeto,
        IQuotaBlockStamp blockStamp,
        CancellationToken cancellationToken)
    {
        if (result.Error is { } error)
        {
            if (string.Equals(error.Code, ApiErrorCode.QuotaExceeded, StringComparison.Ordinal))
            {
                if (quotaVeto.RetryAfterSeconds is { } retryAfter)
                {
                    context.Response.Headers.RetryAfter = RetryAfterHeader.Format(retryAfter);
                }

                await StampBlockedAsync(blockStamp, quotaVeto, logger, context, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!string.Equals(error.Code, ApiErrorCode.BatchTooLarge, StringComparison.Ordinal))
            {
                IngestBatchRejection.Record(
                    logger, IngestBatchRejection.ReasonFor(error), context.TraceIdentifier, runId, bytesIn, 0);
            }
        }

        return result;
    }

    /// <summary>
    /// Writes the refusal stamp the rolled-back batch carried away, in a scope of its own. Best effort on purpose:
    /// the block already decided the 429 and nothing here admits work, so a stamp the database refused is logged
    /// and the caller still reads the 429 it earned.
    /// </summary>
    private static async Task StampBlockedAsync(
        IQuotaBlockStamp blockStamp,
        IngestQuotaVeto quotaVeto,
        ILogger logger,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (quotaVeto.Block is not { } block)
        {
            return;
        }

        try
        {
            await blockStamp.StampAsync(block.OrgId, block.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The refusal itself is already answered; only the diagnostics of the retry are lost here.
            LogBlockStampFailed(logger, context.TraceIdentifier, exception);
        }
    }

    [LoggerMessage(
        EventId = 4111,
        Level = LogLevel.Warning,
        Message = "quota block stamp not written request_id={RequestId}")]
    private static partial void LogBlockStampFailed(ILogger logger, string requestId, Exception exception);

    /// <summary>
    /// Puts the route's run id and the effective request id on the request span before any refusal,
    /// so a 401/403/429 is attributable without the handler's module span (AC7). A no-op when nothing
    /// listens to tracing.
    /// </summary>
    private static void TagRequestSpan(HttpContext context, Guid runId)
    {
        if (Activity.Current is not { } activity)
        {
            return;
        }

        activity.SetTag(CorrelationNames.RunId, runId.ToString("D", CultureInfo.InvariantCulture));
        activity.SetTag(CorrelationNames.RequestId, context.TraceIdentifier);
    }

    /// <summary>
    /// The limiter partition of the authenticated caller: the token's project scope and its
    /// credential id. Only a service principal carries both; any other shape has no budget to
    /// charge and is left to the pipeline and the handler, which refuse it without a write.
    /// </summary>
    private static bool TryRatePartition(IRequestPrincipal? principal, out (Guid ProjectId, string TokenId) partition)
    {
        if (principal is { Kind: PrincipalKind.ServicePrincipal, ProjectScope: { } projectId }
            && !string.IsNullOrEmpty(principal.CredentialId))
        {
            partition = (projectId, principal.CredentialId);

            return true;
        }

        partition = default;

        return false;
    }

    /// <summary>
    /// Reads the request body with a hard cap, so an oversized body is refused before it is buffered whole
    /// (00 §5.5 edge). A <c>Content-Length</c> over the cap is refused without reading a byte, and a body
    /// that lies about its length (or is chunked) is refused as soon as the cap is passed; exactly
    /// <paramref name="cap"/> bytes is the accepted boundary (AC7). The rented <paramref name="buffer"/>
    /// never holds more than <c>cap + 1</c> bytes, which is all the evidence the decision needs.
    /// </summary>
    private static async Task<(ReadOnlyMemory<byte> Bytes, int BytesIn, Error? TooLarge)> ReadBodyAsync(
        HttpContext context,
        byte[] buffer,
        int cap,
        CancellationToken cancellationToken)
    {
        var declared = context.Request.ContentLength;
        if (declared > cap)
        {
            return (default, (int)Math.Min(declared.Value, int.MaxValue), TooLarge());
        }

        // cap + 1 is the read limit: the (cap + 1)-th byte is all the evidence an over-cap body needs.
        var limit = cap + 1;
        var length = 0;
        while (true)
        {
            if (length == limit)
            {
                return (default, length, TooLarge());
            }

            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(length, limit - length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        return length > cap ? (default, length, TooLarge()) : (buffer.AsMemory(0, length), length, null);
    }

    private static Error TooLarge() => new(
        ApiErrorCode.BatchTooLarge,
        "the event batch exceeds the route's transport cap",
        StatusCodes.Status413PayloadTooLarge);
}
