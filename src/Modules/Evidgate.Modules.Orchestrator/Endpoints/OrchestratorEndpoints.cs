using System.Globalization;
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

    /// <summary>
    /// The finalize transport cap (05.3.2 §5): the body is five scalars (<c>producer_id</c>, <c>shard</c>,
    /// <c>last_sequence</c>, <c>count</c>, <c>manifest_hash</c>), so 8 KiB is generous and still refuses an oversized
    /// body before it is buffered whole — the same bound-the-edge rule <see cref="MaxRunBytes"/> follows.
    /// </summary>
    internal const int MaxFinalizeBytes = 8 * 1024;

    /// <summary>Default page size of the run list (00 §9.1, as every other cursor list).</summary>
    internal const int DefaultLimit = 50;

    /// <summary>Largest accepted page size (00 §9.1).</summary>
    internal const int MaxLimit = 200;

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

        group.MapPost("/runs/{r}/finalize", FinalizeShardAsync)
            .Accepts<object>("application/json")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Finalize a producer shard: bound identity, slot close and one reconcile trigger (05.3.2)");

        group.MapGet("/projects/{p}/runs", ListRunsAsync)
            .Produces<RunPage>(StatusCodes.Status200OK)
            .WithSummary("Cursor list of a project's runs, filtered by the recorded branch/PR/commit identity (05.2.2)");

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

    /// <summary>
    /// 401 for a caller with no token identity: a finalize's slot binding <b>is</b> the caller's credential (05.3.2 §5,
    /// T9), so a member session cannot perform one. The same code and wording the events route (<c>POST /v1/events</c>)
    /// answers, so both producer write routes refuse the same callers identically.
    /// </summary>
    private static Error UnauthenticatedProducer => new(
        ApiErrorCode.Unauthenticated,
        "no authenticated producer could be resolved",
        StatusCodes.Status401Unauthorized);

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

    /// <summary>
    /// <c>POST /v1/runs/{r}/finalize</c> (05.3.2 §5): a CI shard declares it has sent everything; the body is read
    /// with a hard 8 KiB cap and handed to the handler as bytes, because the five member shapes are the handler's
    /// (nothing is parsed for a caller who is nobody, and the C2 pipeline has already authorized the request). The
    /// handler answers bodiless <c>202</c>: reconciliation is asynchronous.
    /// <para>
    /// The credential check mirrors the events route exactly: a finalize is a producer's write, so a caller with no
    /// token identity (a member session) is <c>401 unauthenticated</c> here, before a byte of the body is read.
    /// </para>
    /// </summary>
    private static async Task<Result> FinalizeShardAsync(
        Guid r,
        HttpContext context,
        IRequestPrincipalAccessor principals,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var principal = principals.Current;
        if (principal is null || string.IsNullOrEmpty(principal.CredentialId))
        {
            return Result.Failure(UnauthenticatedProducer);
        }

        var (bytes, tooLarge) = await ReadBodyAsync(context, MaxFinalizeBytes, cancellationToken).ConfigureAwait(false);
        if (tooLarge is not null)
        {
            return Result.Failure(tooLarge);
        }

        return await mediator.Send(new FinalizeShardCommand(r, bytes, IdempotencyKey(context)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /v1/projects/{p}/runs</c> (05.2.2 §5, AC6): the project's runs in <c>(created_at, id)</c> order,
    /// narrowed by the run's recorded identity. The delegate only binds and validates the 00 §9.1 query
    /// parameters — <c>limit</c> (1–200), an opaque <c>cursor</c>, and the five identity filters
    /// <c>ref_name</c>, <c>pr_number</c>, <c>head_sha</c>, <c>trigger_event</c>, <c>is_fork</c>; the handler owns
    /// the project check, the tenant scope and the keyset page. An unknown query parameter is ignored (as on every
    /// other list), a present-but-invalid filter value is <c>422 request_invalid</c> with
    /// <c>details[].path</c> = the parameter name and reason <c>invalid_value</c> (a blank value is invalid, not
    /// absent), and every value is passed to the handler as a bound parameter, never concatenated into SQL.
    /// </summary>
    private static async Task<Result<RunPage>> ListRunsAsync(
        Guid p,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (LimitError(context.Request.Query["limit"], out var limit) is { } error)
        {
            return Result.Failure<RunPage>(error);
        }

        var query = context.Request.Query;

        string? refName = null;
        if (Present(query, "ref_name", out var rawRefName))
        {
            if (!RunIdentityRules.IsStorableName(rawRefName))
            {
                return Result.Failure<RunPage>(InvalidFilter("ref_name"));
            }

            refName = rawRefName;
        }

        int? prNumber = null;
        if (Present(query, "pr_number", out var rawPrNumber))
        {
            if (!int.TryParse(rawPrNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                return Result.Failure<RunPage>(InvalidFilter("pr_number"));
            }

            prNumber = parsed;
        }

        string? headSha = null;
        if (Present(query, "head_sha", out var rawHeadSha))
        {
            if (!RunIdentityRules.IsSha(rawHeadSha))
            {
                return Result.Failure<RunPage>(InvalidFilter("head_sha"));
            }

            headSha = rawHeadSha;
        }

        string? triggerEvent = null;
        if (Present(query, "trigger_event", out var rawTriggerEvent))
        {
            if (!RunIdentityRules.TriggerEvents.Contains(rawTriggerEvent, StringComparer.Ordinal))
            {
                return Result.Failure<RunPage>(InvalidFilter("trigger_event"));
            }

            triggerEvent = rawTriggerEvent;
        }

        bool? isFork = null;
        if (Present(query, "is_fork", out var rawIsFork))
        {
            if (rawIsFork is not ("true" or "false"))
            {
                return Result.Failure<RunPage>(InvalidFilter("is_fork"));
            }

            isFork = rawIsFork == "true";
        }

        return await mediator.Send(
            new ListRunsQuery(
                p,
                new RunListFilters(refName, prNumber, headSha, triggerEvent, isFork),
                Blank(query["cursor"]),
                limit),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>True when the query carries <paramref name="name"/> at least once; <paramref name="value"/> is then its first value (possibly blank).</summary>
    private static bool Present(IQueryCollection query, string name, out string value)
    {
        if (query.TryGetValue(name, out var values) && values.Count > 0)
        {
            value = values[0] ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>422 for a filter value the shapes of 05.2.2 §5 cannot hold, naming the parameter (00 §9.2).</summary>
    private static Error InvalidFilter(string name) =>
        Error.Invalid($"{name} has an invalid value", [new ErrorDetail(name, "invalid_value")]);

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

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
