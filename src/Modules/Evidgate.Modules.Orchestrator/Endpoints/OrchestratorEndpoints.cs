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
using Evidgate.Modules.Ingestion.Contracts;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Orchestrator.Endpoints;

/// <summary>
/// The manifest and environment-revision routes of 05.1.1 §5 and 05.1.2 §5. The body is read with a hard byte cap and
/// validated against its contract schema before anything binds (00 §5.5 edge); authorization is the C2 pipeline and the
/// 00 §9.2 envelope is the host's <c>ResultEndpointFilter</c>.
/// </summary>
public static class OrchestratorEndpoints
{
    /// <summary>The environment-revision transport cap (the schema's <c>x-max-bytes</c>).</summary>
    internal const int MaxEnvironmentBytes = 64 * 1024;

    /// <summary>The cap name a manifest over its byte budget carries in the 413 detail.</summary>
    internal const string ManifestBytesCap = "manifest_bytes";

    /// <summary>The run-create transport cap: the body is a manifest id, an optional release and components.</summary>
    internal const int MaxRunBytes = 64 * 1024;

    /// <summary>The cap name a run-create body over its transport cap carries in the 413 detail.</summary>
    internal const string RunBodyBytesCap = "run_body_bytes";

    /// <summary>
    /// The finalize transport cap (05.3.2 §5): the body is five scalars (<c>producer_id</c>, <c>shard</c>,
    /// <c>last_sequence</c>, <c>count</c>, <c>manifest_hash</c>), so 8 KiB is generous and still refuses an oversized
    /// body before it is buffered whole — the same bound-the-edge rule the manifest body cap follows.
    /// </summary>
    internal const int MaxFinalizeBytes = 8 * 1024;

    /// <summary>
    /// The cancel transport cap (05.4.1 §5): the body is one optional string of at most 1000 characters, so 4 KiB
    /// refuses an oversized body before it is buffered — the same bound-the-edge rule the manifest body cap follows.
    /// </summary>
    internal const int MaxCancelBytes = 4 * 1024;

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
            .WithHandlerOwnedBodyLimit()
            .Produces<ManifestAccepted>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Upload and verify a run manifest (05.1.1)");

        group.MapPost("/projects/{p}/runs", CreateRunAsync)
            .WithHandlerOwnedBodyLimit()
            .Accepts<CreateRunRequest>("application/json")
            .Produces<RunCreated>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status200OK)
            .WithSummary("Create a run from a verified manifest in one transaction (05.2.1)");

        group.MapPost("/projects/{p}/environments/{e}/revisions", CreateEnvironmentRevisionAsync)
            .WithContractBodyLimit(ContractSchemaKeys.EnvironmentRevision)
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

        group.MapGet("/runs/{r}/attempts", ListRunAttemptsAsync)
            .Produces<RunAttemptPage>(StatusCodes.Status200OK)
            .WithSummary("Cursor list of a run's attempts: raw and normalised status, retry chain, ready artifacts (F52)");

        group.MapGet("/runs/{run_id}/evidence-revisions", ListEvidenceRevisionsAsync)
            .Produces<EvidenceRevisionPage>(StatusCodes.Status200OK)
            .WithSummary("Cursor list of a run's evidence revisions, newest first, with their live evaluation id (05.3.2)");

        // Cancel answers 202 on an accepted request and on a terminal no-op alike. The body is optional and is read as
        // raw bytes by the handler, so the accepted media type is declared with its own metadata (`isOptional`) instead
        // of a bound parameter — and the bodiless 200 pins the slot the framework would otherwise infer with the shared
        // Result schema, which the spec does not declare (ServedDocumentTests' invariant, the finalize route's note).
        group.MapPost("/runs/{r}/cancel", CancelRunAsync)
            .WithMetadata(new AcceptsMetadata(["application/json"], typeof(CancelRunRequest), isOptional: true))
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Record a cancel request: the run turns canceled now, execution stops on the configured windows (05.4.1)");

        // The route answers 202 only, but the framework infers a 200 from the bodiless Result the delegate returns
        // (the shared-kernel type, whose Error/SuccessValue members would then appear as contract schemas the spec does
        // not declare — ServedDocumentTests' invariant). Declaring the status with no type pins that slot to "no body",
        // the same way CiAdminEndpoints/MembershipEndpoints declare the bodiless Result routes they serve.
        group.MapPost("/runs/{r}/finalize", FinalizeShardAsync)
            .Accepts<object>("application/json")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Finalize a producer shard: bound identity, slot close and one reconcile trigger (05.3.2)");

        group.MapGet("/projects/{p}/runs", ListRunsAsync)
            .Produces<RunPage>(StatusCodes.Status200OK)
            .WithSummary("Cursor list of a project's runs, filtered by the recorded branch/PR/commit identity plus state, decision, rerun scope and parent run (05.2.2, F43)");

        ReleaseEndpoints.Map(group);

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

        AllowCounterToOwnTheRefusal(context);

        var manifestLimit = context.RequestServices.GetRequiredService<IOptions<IngestionLimits>>().Value.MaxManifestBytes;
        var (bytes, tooLarge) = await ReadBodyAsync(context, manifestLimit, ManifestBytesCap, cancellationToken).ConfigureAwait(false);
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

        AllowCounterToOwnTheRefusal(context);

        var (bytes, tooLarge) = await ReadBodyAsync(context, MaxRunBytes, RunBodyBytesCap, cancellationToken).ConfigureAwait(false);
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

        var (bytes, tooLarge) = await ReadBodyAsync(context, MaxEnvironmentBytes, null, cancellationToken).ConfigureAwait(false);
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

    internal static Error Unauthenticated => new(ApiErrorCode.Unauthenticated, "authentication is required", StatusCodes.Status401Unauthorized);

    /// <summary>
    /// 401 for a caller with no principal at all — no token and no session. The same code and wording the events route
    /// (<c>POST /v1/runs/{r}/events</c>) answers, so both producer write routes refuse the same callers identically: a
    /// resolved member session is <b>not</b> refused here, because the C2 pipeline's <c>event:write</c> check is what
    /// answers it (403, 00 §9.2) and an endpoint must not pre-empt the permission gate with a different status.
    /// </summary>
    private static Error UnauthenticatedProducer => new(
        ApiErrorCode.Unauthenticated,
        "no authenticated producer could be resolved",
        StatusCodes.Status401Unauthorized);

    internal static string Sha256(ReadOnlyMemory<byte> bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes.Span));

    /// <summary>
    /// <c>POST /v1/runs/{r}/cancel</c> (05.4.1 §5): binds the path, the capped body and the caller's
    /// <c>Idempotency-Key</c>, then sends the one command — the handler owns the edge, the durable request row and the
    /// audit. A caller with no principal at all is 401 here, before a byte of the body is read; the C2 pipeline's
    /// <c>run:cancel</c> check answers a resolved member without it (403, 00 §9.2).
    /// </summary>
    private static async Task<Result> CancelRunAsync(
        Guid r,
        HttpContext context,
        IRequestPrincipalAccessor principals,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (principals.Current is null)
        {
            return Result.Failure(Unauthenticated);
        }

        var (bytes, tooLarge) = await ReadBodyAsync(context, MaxCancelBytes, null, cancellationToken).ConfigureAwait(false);
        if (tooLarge is not null)
        {
            return Result.Failure(tooLarge);
        }

        return await mediator.Send(new CancelRunCommand(r, bytes, IdempotencyKey(context)), cancellationToken)
            .ConfigureAwait(false);
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
    /// <c>GET /v1/runs/{r}/attempts</c> (F52/S143; 09.3.1-T4's attempts table): the run's <c>case_attempt</c> rows in
    /// <c>(execution_key, attempt_no)</c> order. The delegate only binds and validates the 00 §9.1 query parameters —
    /// <c>limit</c> (1–200) and an opaque <c>cursor</c> — and the handler owns the run lookup, the project scope and
    /// the keyset page; a cursor this endpoint did not issue, and an out-of-range <c>limit</c>, are the handler's (and
    /// this delegate's) <c>422 request_invalid</c> with <c>details[].path</c> = <c>cursor</c> / <c>limit</c>. An
    /// unknown query parameter is ignored, as on every other list.
    /// </summary>
    private static async Task<Result<RunAttemptPage>> ListRunAttemptsAsync(
        Guid r,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (LimitError(context.Request.Query["limit"], out var limit) is { } error)
        {
            return Result.Failure<RunAttemptPage>(error);
        }

        return await mediator.Send(
            new ListRunAttemptsQuery(r, Blank(context.Request.Query["cursor"]), limit),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /v1/runs/{run_id}/evidence-revisions</c>: the run's <c>evidence_revision</c> rows, newest first. The
    /// delegate binds the 00 §9.1 query parameters and owns the one validation split the route documents: a
    /// <c>limit</c> that is not the canonical spelling of an integer (<c>abc</c>, <c>1.5</c>, <c> 5</c>, <c>+5</c>,
    /// <c>01</c>) is not a page size at all, so it is <c>400 request_invalid</c> here (the request itself is
    /// malformed, 00 §9.2), while an integer outside 1–200 — <c>-1</c> and <c>0</c> included — and a cursor this
    /// endpoint did not issue are the list convention's <c>422 request_invalid</c> with
    /// <c>details[].path</c> = <c>limit</c> / <c>cursor</c>. An unknown query parameter is ignored, as on every other
    /// list.
    /// </summary>
    private static async Task<Result<EvidenceRevisionPage>> ListEvidenceRevisionsAsync(
        Guid run_id,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (EvidenceRevisionLimitError(context.Request.Query["limit"], out var limit) is { } error)
        {
            return Result.Failure<EvidenceRevisionPage>(error);
        }

        return await mediator.Send(
            new ListEvidenceRevisionsQuery(run_id, Blank(context.Request.Query["cursor"]), limit),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The evidence-revisions page size: absent or blank keeps the default. A value that is not the canonical decimal
    /// spelling of an integer (letters, a fraction, padding, whitespace, a <c>+</c> sign) is the route's 400 —
    /// malformed, so the client can tell "not this number" from "this number is refused" — while a canonical integer
    /// outside 1–200, a negative one included, is the list 422 with <c>details[].path</c> = <c>limit</c>. Separate from
    /// <see cref="LimitError"/> because no other list distinguishes the two, and widening them all is not this route's
    /// change.
    /// </summary>
    private static Error? EvidenceRevisionLimitError(string? raw, out int limit)
    {
        limit = DefaultLimit;

        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        // A sign is allowed so that `-1` reads as the integer it is and takes the range answer below, not the
        // malformed one; the canonical spelling check then refuses `+5` and `01`, whose integer value alone would
        // otherwise pass — the cursor codec applies the same rule in the handler.
        if (!int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
            || !string.Equals(parsed.ToString(CultureInfo.InvariantCulture), raw, StringComparison.Ordinal))
        {
            return Error.BadRequest(
                "limit must be a canonical integer (00 §9.1); the request is malformed",
                [new ErrorDetail("limit", "malformed")]);
        }

        if (parsed < 1 || parsed > MaxLimit)
        {
            return Error.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"limit must be an integer between 1 and {MaxLimit} (00 §9.1)"),
                [new ErrorDetail("limit", "out_of_range")]);
        }

        limit = parsed;

        return null;
    }

    /// <summary>
    /// <c>POST /v1/runs/{r}/finalize</c> (05.3.2 §5): a CI shard declares it has sent everything; the body is read
    /// with a hard 8 KiB cap and handed to the handler as bytes, because the five member shapes are the handler's.
    /// The read happens <b>before</b> <c>mediator.Send</c>, so up to 8 KiB is buffered for any resolved principal —
    /// the C2 pipeline's permission, scope and idempotency stages answer later, inside the send — and only a caller
    /// with no principal at all is refused before a byte is read. The handler answers bodiless <c>202</c>:
    /// reconciliation is asynchronous.
    /// <para>
    /// The credential gate mirrors the events route exactly: a caller with no principal is <c>401 unauthenticated</c>
    /// here, before a byte of the body is read, and a resolved principal (a member session included) goes on to the
    /// pipeline, whose <c>event:write</c> permission is what refuses it (403 — the same answer the events route gives a
    /// viewer). The handler's own credential check stays as the fail-closed backstop for a principal that somehow
    /// reaches it without a token identity: the slot binding <b>is</b> that identity (05.3.2 §5, T9).
    /// </para>
    /// <para>
    /// The caller's identity (<c>CredentialId</c> and the CI run reference) travels with the command so the
    /// idempotency record is bound to it: another credential replaying this key with the same body is 409
    /// <c>idempotency_conflict</c>, never the stored <c>202</c> (see <see cref="FinalizeShardCommand.BodyHash"/>).
    /// </para>
    /// </summary>
    private static async Task<Result> FinalizeShardAsync(
        Guid r,
        HttpContext context,
        IRequestPrincipalAccessor principals,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (principals.Current is not { } principal)
        {
            return Result.Failure(UnauthenticatedProducer);
        }

        var (bytes, tooLarge) = await ReadBodyAsync(context, MaxFinalizeBytes, null, cancellationToken).ConfigureAwait(false);
        if (tooLarge is not null)
        {
            return Result.Failure(tooLarge);
        }

        return await mediator.Send(
            new FinalizeShardCommand(
                r,
                bytes,
                IdempotencyKey(context),
                principal.CredentialId,
                principal.CiRun?.ToBindingReference()),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /v1/projects/{p}/runs</c> (05.2.2 §5, AC6; F43 filters S143): the project's runs in <c>(created_at, id)</c>
    /// order, narrowed by the run's recorded identity and its state/decision/rerun labels. The delegate only binds and
    /// validates the 00 §9.1 query parameters — <c>limit</c> (1–200), an opaque <c>cursor</c>, and the five identity
    /// filters <c>ref_name</c>, <c>pr_number</c>, <c>head_sha</c>, <c>trigger_event</c>, <c>is_fork</c> plus the four
    /// F43 filters <c>state</c>, <c>decision</c>, <c>rerun_scope</c>, <c>parent_run_id</c>; the handler owns
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

        // F43 (S143): the four further exact-match filters. Each value is checked against the closed vocabulary its
        // column admits, so a present-but-invalid value is 422 with details[].path = the parameter — never a silently
        // narrowed (or widened) page. `decision` is `pending` plus `pass`/`fail`/`inconclusive` — filter tokens in
        // lower snake that the handler maps to the stored verdict, never the stored value itself — and
        // `parent_run_id` is the one filter only this binding can refuse, because it binds to a Guid.
        string? state = null;
        if (Present(query, "state", out var rawState))
        {
            if (!RunListFilterRules.IsKnownState(rawState))
            {
                return Result.Failure<RunPage>(InvalidFilter("state"));
            }

            state = rawState;
        }

        string? decision = null;
        if (Present(query, "decision", out var rawDecision))
        {
            if (!RunListFilterRules.Decisions.Contains(rawDecision, StringComparer.Ordinal))
            {
                return Result.Failure<RunPage>(InvalidFilter("decision"));
            }

            decision = rawDecision;
        }

        string? rerunScope = null;
        if (Present(query, "rerun_scope", out var rawRerunScope))
        {
            if (!RunListFilterRules.RerunScopes.Contains(rawRerunScope, StringComparer.Ordinal))
            {
                return Result.Failure<RunPage>(InvalidFilter("rerun_scope"));
            }

            rerunScope = rawRerunScope;
        }

        Guid? parentRunId = null;
        if (Present(query, "parent_run_id", out var rawParentRunId))
        {
            if (!Guid.TryParse(rawParentRunId, out var parsedParentRunId))
            {
                return Result.Failure<RunPage>(InvalidFilter("parent_run_id"));
            }

            parentRunId = parsedParentRunId;
        }

        return await mediator.Send(
            new ListRunsQuery(
                p,
                new RunListFilters(refName, prNumber, headSha, triggerEvent, isFork, state, decision, rerunScope, parentRunId),
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

    /// <summary>
    /// Lifts the host's own request-body limit for a route whose body this class counts. The Kestrel default
    /// (30 000 000 bytes) sits below a manifest's configured cap, so it would answer a bare 413 that carries none of
    /// the detail the route publishes; with the feature cleared the counter below is what refuses the body, at the cap
    /// and with its detail. A feature the host has already made read-only is left as it is, and the counter still
    /// refuses at its own cap.
    /// </summary>
    internal static void AllowCounterToOwnTheRefusal(HttpContext context)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = null;
        }
    }

    /// <summary>The raw body, refused with 413 as soon as it passes <paramref name="limit"/> bytes (never buffered beyond it).</summary>
    /// <remarks>
    /// The read is a byte counter, not a length check: a body without a <c>Content-Length</c>, or one whose
    /// declared length lies about it, is refused at <c>limit + 1</c> bytes anyway, so the buffer never holds more
    /// than the cap plus one byte. A declared length over the cap is refused before a byte is read, and the detail
    /// then carries that declared length as the actual value. The buffer starts at the declared length plus one,
    /// but never larger than one read, and grows by doubling up to that same ceiling, so a body declaring a large
    /// length costs a small allocation until the bytes actually arrive.
    /// </remarks>
    internal static async Task<(ReadOnlyMemory<byte> Bytes, Error? TooLarge)> ReadBodyAsync(
        HttpContext context,
        long limit,
        string? cap,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var tooLarge = (long? actual) => new Error(
            ApiErrorCode.PayloadTooLarge,
            "the request body is too large",
            StatusCodes.Status413PayloadTooLarge,
            Details: cap is null
                ? null
                : [new ErrorDetail(cap, ApiErrorCode.PayloadTooLarge, Limit: limit, Actual: actual)]);

        var declared = context.Request.ContentLength;
        if (declared > limit)
        {
            Record(cap);
            return (default, tooLarge(declared));
        }

        const int DeclaredStart = 64 * 1024;
        const int ChunkedStart = 16 * 1024;
        var ceiling = (int)Math.Min(limit + 1, int.MaxValue);
        var buffer = new byte[declared is { } known
            ? (int)Math.Min(Math.Min(known, ceiling - 1L) + 1, DeclaredStart)
            : Math.Min(ChunkedStart, ceiling)];
        var length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                if (length > limit)
                {
                    Record(cap);
                    return (default, tooLarge(length));
                }

                Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, ceiling));
            }

            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;

            // A body at or past the cap is refused before the next read, so the stream is never pulled further
            // than the cap plus one byte whatever the loop's buffer arithmetic says.
            if (length > limit)
            {
                Record(cap);
                return (default, tooLarge(length));
            }
        }

        return (buffer.AsMemory(0, length), null);

        static void Record(string? name)
        {
            if (name is not null)
            {
                EvidgateTelemetry.RecordIngestCapExceeded(name);
            }
        }
    }

    internal static string IdempotencyKey(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString();
}
