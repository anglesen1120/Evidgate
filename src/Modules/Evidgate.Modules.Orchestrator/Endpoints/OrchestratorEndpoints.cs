using System.Text.Json;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Application.Manifests;
using Evidgate.Modules.Orchestrator.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.Modules.Orchestrator.Endpoints;

/// <summary>
/// The manifest and environment-revision routes of 05.1.1 §5 and 05.1.2 §5. The body is read with a hard byte cap and
/// validated against its contract schema before anything binds (00 §5.5 edge); authorization is the C2 pipeline and the
/// 00 §9.2 envelope is the host's <c>ResultEndpointFilter</c>.
/// </summary>
public static class OrchestratorEndpoints
{
    /// <summary>The manifest transport cap (05.1.1 §8 [DECISION]); the schema's <c>x-max-bytes</c> says the same.</summary>
    internal const int MaxManifestBytes = 256 * 1024;

    /// <summary>The environment-revision transport cap (the schema's <c>x-max-bytes</c>).</summary>
    internal const int MaxEnvironmentBytes = 64 * 1024;

    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>Maps the routes and returns the <c>/v1</c> group so the host can apply its filters (00 §5.5 C8).</summary>
    public static RouteGroupBuilder MapOrchestratorEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("runs");

        group.MapPost("/projects/{p}/manifests", UploadManifestAsync)
            .Produces<ManifestAccepted>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Upload and verify a run manifest (05.1.1)");

        group.MapPost("/projects/{p}/environments/{e}/revisions", CreateEnvironmentRevisionAsync)
            .Produces<EnvironmentRevisionCreated>(StatusCodes.Status201Created)
            .Produces<EnvironmentRevisionCreated>(StatusCodes.Status200OK)
            .WithSummary("Create an immutable, secret-free, hashed environment revision (05.1.2)");

        group.MapGet("/environment-revisions/{id}", GetEnvironmentRevisionAsync)
            .Produces<EnvironmentRevisionDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status304NotModified)
            .WithSummary("One environment revision; ETag is its hash (05.1.2)");

        return group;
    }

    private static async Task<Result<ManifestAccepted>> UploadManifestAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var (bytes, tooLarge) = await ReadBodyAsync(context, MaxManifestBytes, cancellationToken).ConfigureAwait(false);
        if (tooLarge is not null)
        {
            EvidgateTelemetry.RecordManifestRejected(ContractValidationError.BodyTooLargeReason);
            return Result.Failure<ManifestAccepted>(tooLarge);
        }

        var schemas = context.RequestServices.GetRequiredService<ContractSchemaSet>();
        var verdict = schemas.Validate(ContractSchemaKeys.Manifest, bytes);
        if (!verdict.IsValid)
        {
            var reason = verdict.Errors[0].Reason;
            EvidgateTelemetry.RecordManifestRejected(reason == ContractValidationError.InvalidJsonReason ? reason : "schema");
            return Result.Failure<ManifestAccepted>(Error.Unprocessable(
                ApiErrorCode.ManifestInvalid,
                "the manifest does not match manifest.v1",
                [.. verdict.Errors.Select(error => new ErrorDetail(SafePath(error), error.Reason, error.Index))]));
        }

        using var document = JsonDocument.Parse(bytes);
        var result = await mediator.Send(new UploadManifestCommand(p, document.RootElement.Clone(), IdempotencyKey(context)), cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess && result.SuccessValue is ManifestAccepted uploaded)
        {
            context.Response.Headers.ETag = $"\"{uploaded.ManifestHash}\"";
        }

        return result;
    }

    private static async Task<Result<EnvironmentRevisionCreated>> CreateEnvironmentRevisionAsync(
        Guid p,
        string e,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var (bytes, tooLarge) = await ReadBodyAsync(context, MaxEnvironmentBytes, cancellationToken).ConfigureAwait(false);
        if (tooLarge is not null)
        {
            return Result.Failure<EnvironmentRevisionCreated>(tooLarge);
        }

        var schemas = context.RequestServices.GetRequiredService<ContractSchemaSet>();
        var verdict = schemas.Validate(ContractSchemaKeys.EnvironmentRevision, bytes);
        if (!verdict.IsValid)
        {
            return Result.Failure<EnvironmentRevisionCreated>(Error.Unprocessable(
                ApiErrorCode.EnvironmentRevisionInvalid,
                "the body does not match environment-revision.v1",
                [.. verdict.Errors.Select(error => new ErrorDetail(SafePath(error), error.Reason, error.Index))]));
        }

        var request = JsonSerializer.Deserialize<CreateEnvironmentRevisionRequest>(bytes.Span, RequestJson)!;
        var result = await mediator.Send(new CreateEnvironmentRevisionCommand(p, e, request, IdempotencyKey(context)), cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess && result.SuccessValue is EnvironmentRevisionCreated created)
        {
            context.Response.Headers.ETag = $"\"{created.RevisionHash}\"";
        }

        return result;
    }

    private static async Task<Result> GetEnvironmentRevisionAsync(
        Guid id,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetEnvironmentRevisionQuery(id), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result;
        }

        // A revision never changes: its hash is its ETag, it caches privately for good, and a matching If-None-Match is 304.
        var tag = $"\"{result.Value.RevisionHash}\"";
        context.Response.Headers.ETag = tag;
        context.Response.Headers.CacheControl = "private, immutable";
        var presented = context.Request.Headers.IfNoneMatch.ToString();
        var matches = presented.Length > 0 && presented.Split(',')
            .Select(candidate => candidate.Trim())
            .Any(candidate => candidate == "*" || (candidate.StartsWith("W/", StringComparison.Ordinal) ? candidate[2..] : candidate) == tag);

        return matches ? Result.Success(StatusCodes.Status304NotModified) : result;
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

    /// <summary>An <c>additional_properties</c> path ends in the unknown member's name, which may be anything the caller sent: it is cut.</summary>
    private static string SafePath(ContractValidationError error)
    {
        var path = error.Path.Length > 200 ? error.Path[..200] : error.Path;
        if (!string.Equals(error.Reason, "additional_properties", StringComparison.Ordinal))
        {
            return path;
        }

        var cut = path.LastIndexOf('/');

        return cut <= 0 ? string.Empty : path[..cut];
    }

    private static string IdempotencyKey(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString();
}
