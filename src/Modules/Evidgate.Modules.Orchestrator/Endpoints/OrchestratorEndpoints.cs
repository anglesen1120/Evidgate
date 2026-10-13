using System.Text.Json;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Application.Manifests;
using Evidgate.Modules.Orchestrator.Application.Queries;
using Evidgate.Modules.Orchestrator.Application.Runs;
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

    /// <summary>The run-create transport cap (05.2.1 §5): the body is a manifest id, an optional release and components.</summary>
    internal const int MaxRunBytes = 64 * 1024;

    /// <summary>Maps the routes and returns the <c>/v1</c> group so the host can apply its filters (00 §5.5 C8).</summary>
    public static RouteGroupBuilder MapOrchestratorEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("runs");

        group.MapPost("/projects/{p}/manifests", UploadManifestAsync)
            .Produces<ManifestAccepted>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Upload and verify a run manifest (05.1.1)");

        group.MapPost("/projects/{p}/runs", CreateRunAsync)
            .Accepts<CreateRunRequest>("application/json")
            .Produces<RunCreated>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Create a run from a verified manifest in one transaction (05.2.1)");

        group.MapPost("/projects/{p}/environments/{e}/revisions", CreateEnvironmentRevisionAsync)
            .Produces<EnvironmentRevisionCreated>(StatusCodes.Status201Created)
            .Produces<EnvironmentRevisionCreated>(StatusCodes.Status200OK)
            .WithSummary("Create an immutable, secret-free, hashed environment revision (05.1.2)");

        group.MapGet("/environment-revisions/{id}", GetEnvironmentRevisionAsync)
            .Produces<EnvironmentRevisionDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status304NotModified)
            .WithSummary("One environment revision; ETag is its hash (05.1.2)");

        group.MapGet("/runs/{r}", GetRunAsync)
            .Produces<RunStateDto>(StatusCodes.Status200OK)
            .WithSummary("One run's state, current evidence revision, version and purpose (05.3.1)");

        return group;
    }

    private static async Task<Result<ManifestAccepted>> UploadManifestAsync(
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
            return Result.Failure<ManifestAccepted>(Unauthenticated);
        }

        var (bytes, tooLarge) = await ReadBodyAsync(context, MaxManifestBytes, cancellationToken).ConfigureAwait(false);
        if (tooLarge is not null)
        {
            EvidgateTelemetry.RecordManifestRejected(ContractValidationError.BodyTooLargeReason);
            return Result.Failure<ManifestAccepted>(tooLarge);
        }

        var result = await mediator.Send(new UploadManifestCommand(p, bytes, Sha256(bytes), IdempotencyKey(context)), cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess && result.SuccessValue is ManifestAccepted uploaded)
        {
            context.Response.Headers.ETag = $"\"{uploaded.ManifestHash}\"";
        }

        return result;
    }

    private static async Task<Result<RunCreated>> CreateRunAsync(
        Guid p,
        HttpContext context,
        IRequestPrincipalAccessor principals,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        // Nothing of the body is read for a caller who is nobody; parsing and validation run in the handler, after the
        // pipeline's permission check (05.2.1 §5).
        var principal = principals.Current;
        if (principal is null)
        {
            return Result.Failure<RunCreated>(Unauthenticated);
        }

        var (bytes, tooLarge) = await ReadBodyAsync(context, MaxRunBytes, cancellationToken).ConfigureAwait(false);
        if (tooLarge is not null)
        {
            return Result.Failure<RunCreated>(tooLarge);
        }

        // 05.2.2-T1.4: the caller's verified CI run reference joins the request identity the idempotency store keys on,
        // so the same key and body replayed from a different run attempt of the repository is a 409 conflict instead of
        // a replay (while a re-minted token for the same attempt still replays). Null for every caller whose token
        // carries no reference, which leaves the digest of such a request exactly as it was before this story.
        return await mediator.Send(
            new CreateRunCommand(p, bytes, IdempotencyKey(context), principal.CiRun?.ToBindingReference()),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<EnvironmentRevisionCreated>> CreateEnvironmentRevisionAsync(
        Guid p,
        string e,
        HttpContext context,
        IRequestPrincipalAccessor principals,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (principals.Current is null)
        {
            return Result.Failure<EnvironmentRevisionCreated>(Unauthenticated);
        }

        var (bytes, tooLarge) = await ReadBodyAsync(context, MaxEnvironmentBytes, cancellationToken).ConfigureAwait(false);
        if (tooLarge is not null)
        {
            return Result.Failure<EnvironmentRevisionCreated>(tooLarge);
        }

        var result = await mediator.Send(new CreateEnvironmentRevisionCommand(p, e, bytes, Sha256(bytes), IdempotencyKey(context)), cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess && result.SuccessValue is EnvironmentRevisionCreated created)
        {
            context.Response.Headers.ETag = $"\"{created.RevisionHash}\"";
        }

        return result;
    }

    private static Error Unauthenticated => new(ApiErrorCode.Unauthenticated, "authentication is required", StatusCodes.Status401Unauthorized);

    private static string Sha256(ReadOnlyMemory<byte> bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes.Span));

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

        // A revision never changes: its hash is its ETag, a matching If-None-Match is 304, and it caches privately for an
        // hour (bounded, so a revoked reader does not keep it for good).
        var tag = $"\"{result.Value.RevisionHash}\"";
        context.Response.Headers.ETag = tag;
        context.Response.Headers.CacheControl = "private, max-age=3600, immutable";
        var presented = context.Request.Headers.IfNoneMatch.ToString();
        var matches = presented.Length > 0 && presented.Split(',')
            .Select(candidate => candidate.Trim())
            .Any(candidate => candidate == "*" || (candidate.StartsWith("W/", StringComparison.Ordinal) ? candidate[2..] : candidate) == tag);

        return matches ? Result.Success(StatusCodes.Status304NotModified) : result;
    }

    /// <summary>
    /// <c>GET /v1/runs/{r}</c> (05.3.1 §5): the run's state, current evidence revision, version and purpose. The
    /// handler's <c>run:read</c> permission is the C2 pipeline's; an unknown, another tenant's or another project's
    /// run is its 404 <c>not_found</c>. No <c>ETag</c>: 05.3.1 §5 pins <c>If-Match</c> semantics to the transition
    /// command, and the body already carries the <c>version</c> a client echoes there.
    /// </summary>
    private static async Task<Result<RunStateDto>> GetRunAsync(
        Guid r,
        IMediator mediator,
        CancellationToken cancellationToken) =>
        await mediator.Send(new GetRunQuery(r), cancellationToken).ConfigureAwait(false);

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

    private static string IdempotencyKey(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString();
}
