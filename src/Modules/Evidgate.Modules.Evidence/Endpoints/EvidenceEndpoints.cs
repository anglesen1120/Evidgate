using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Domain;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Evidence.Endpoints;

/// <summary>
/// The artifact routes of 06.1.2 §5. The body is read with the host's snake_case JSON options and validated by the
/// command's FluentValidation validator; authorization is the C2 pipeline and the 00 §9.2 envelope is the host's
/// <c>ResultEndpointFilter</c>. The upload-url body is bounded by <see cref="UploadUrlLimits.MaxBodyBytes"/> before
/// it is deserialised (06.1.2-T3.1), so an oversized body is the 00 §9.2 413 <c>payload_too_large</c> envelope and
/// never reaches the mediator.
/// </summary>
public static class EvidenceEndpoints
{
    /// <summary>The header every per-caller, credential-bearing answer carries: no cache may store it.</summary>
    private const string NoStore = "no-store";

    /// <summary>Default page size of the report comparison (00 §9.1, as every other cursor list).</summary>
    private const int DefaultLimit = 50;

    /// <summary>Largest accepted page size of the report comparison (00 §9.1).</summary>
    private const int MaxLimit = 200;

    /// <summary>Maps the routes and returns the <c>/v1</c> group so the host can apply its filters (00 §5.5 C8).</summary>
    public static RouteGroupBuilder MapEvidenceEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("artifacts");

        group.MapPost("/artifacts/upload-url", RequestUploadUrlAsync)
            .Produces<UploadUrlResponse>(StatusCodes.Status200OK)
            .WithSummary("Request a presigned upload URL for an artifact (06.1.2)");

        group.MapPost("/artifacts/{a:guid}/complete", CompleteArtifactAsync)
            .Produces<CompleteResponse>(StatusCodes.Status200OK)
            .WithSummary("Verify an uploaded artifact and complete its upload (06.1.2)");

        group.MapGet("/artifacts/{a:guid}", GetArtifactMetadataAsync)
            .Produces<ArtifactMetadataView>(StatusCodes.Status200OK)
            .WithSummary("Read one artifact's metadata; content-bearing members are gated when it is sensitive");

        group.MapGet("/artifacts/{a:guid}/download-url", IssueDownloadUrlAsync)
            .Produces<DownloadUrlResponse>(StatusCodes.Status200OK)
            .WithSummary("Issue a short-lived presigned download URL for one artifact");

        group.MapGet("/runs/{r:guid}/reports/{report_id:guid}/comparison", GetReportComparisonAsync)
            .Produces<ReportComparison>(StatusCodes.Status200OK)
            .WithSummary("Compare one stored report's declared counters with the cases its parse observed");

        return group;
    }

    /// <summary>
    /// <c>POST /v1/artifacts/{a}/complete</c> (06.1.2 §5, T86): the route carries no body, so the verification is
    /// server-derived and runs here — before the command dispatches — with no transaction open. The verdict then
    /// travels into <see cref="CompleteArtifactUpload"/>, whose handler re-reads the row inside the C2 tenant
    /// transaction and commits it.
    /// <para>
    /// The <c>Idempotency-Key</c> header is validated here FIRST, before the verifier's permission read and its
    /// <c>HEAD</c>/copy: the C2 idempotency stage (00 §5.5 C2, T92) refuses a missing or over-long key only inside
    /// <c>mediator.Send</c> — i.e. after the verifier has already promoted the staged bytes — so the identical 400
    /// is raised up front and a request the stage will refuse never touches the store.
    /// </para>
    /// </summary>
    /// <param name="context">The request, for the <c>Idempotency-Key</c> header.</param>
    /// <param name="a">The artifact the path names; the <c>guid</c> route constraint refuses anything else.</param>
    /// <param name="principals">The caller's token identity.</param>
    /// <param name="verifier">The endpoint-side C2 gate + storage verification (T86 steps 1-4).</param>
    /// <param name="mediator">Dispatches the writer command.</param>
    /// <param name="cancellationToken">Cancels the verification and the command.</param>
    private static async Task<Result<CompleteResponse>> CompleteArtifactAsync(
        HttpContext context,
        Guid a,
        IRequestPrincipalAccessor principals,
        ArtifactCompletionVerifier verifier,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        // Nothing of the artifact is read for a caller who is nobody; the C2 pipeline authorizes the rest.
        if (principals.Current is not { } principal)
        {
            return Result.Failure<CompleteResponse>(Unauthenticated);
        }

        // T92: the C2 idempotency stage validates the key only inside mediator.Send, after this verifier has
        // HEADed the staged object and copied it to the final key — so a missing or over-long key would promote
        // bytes for a request the pipeline then refuses. The stage's own rule is applied here first, before any
        // permission read or storage call, and the request touches nothing when the key is unacceptable.
        if (IdempotencyKeyError(context) is { } keyError)
        {
            return Result.Failure<CompleteResponse>(keyError);
        }

        var verdict = await verifier.VerifyAsync(a, principal, cancellationToken).ConfigureAwait(false);

        if (!verdict.IsSuccess)
        {
            return Result.Failure<CompleteResponse>(verdict.Error!);
        }

        // The caller's token identity travels into the command, which carries it into the idempotency hash, so a
        // replay by another principal is a conflict rather than the first caller's response (T1 decision 11).
        return await mediator.Send(
            new CompleteArtifactUpload(a, verdict.Value, principal.CredentialId, IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<UploadUrlResponse>> RequestUploadUrlAsync(
        HttpContext context,
        IRequestPrincipalAccessor principals,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        // Nothing of the body is read for a caller who is nobody; the C2 pipeline authorizes the rest.
        if (principals.Current is not { } principal)
        {
            return Result.Failure<UploadUrlResponse>(Unauthenticated);
        }

        // 06.1.2-T3.1: the route's body limit is enforced BEFORE deserialisation, so an oversized body costs no
        // parse, no statement and no row. A declared Content-Length over the limit is refused without reading the
        // body at all; a chunked or length-less body, which no header reveals, is counted while it is read by
        // BodySizeLimitStream below.
        if (context.Request.ContentLength is { } declared && declared > UploadUrlLimits.MaxBodyBytes)
        {
            return Result.Failure<UploadUrlResponse>(CappedRequestBody.TooLarge);
        }

        // The body is read explicitly rather than bound by the request-delegate factory: the spec declares this
        // request body inline (no component schema), so the served document must not name one, and every body-read
        // failure mode still produces the 00 §9.2 envelope — malformed JSON, an unsupported/missing content type
        // (InvalidOperationException) and Kestrel's own BadHttpRequestException (whose status, e.g. 413, is kept),
        // which is also what the size-limiting stream raises when the body passes the limit.
        UploadUrlRequest? request;
        var body = context.Request.Body;
        try
        {
            context.Request.Body = new CappedBodyStream(body, UploadUrlLimits.MaxBodyBytes);
            request = await context.Request.ReadFromJsonAsync<UploadUrlRequest>(cancellationToken).ConfigureAwait(false);
        }
        catch (BadHttpRequestException exception)
        {
            return Result.Failure<UploadUrlResponse>(CappedRequestBody.Unreadable(exception));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            return Result.Failure<UploadUrlResponse>(Error.BadRequest("the request body is not valid JSON"));
        }
        finally
        {
            context.Request.Body = body;
        }

        if (request is null)
        {
            return Result.Failure<UploadUrlResponse>(Error.BadRequest("the request body is required"));
        }

        // The caller's token identity travels into the command: the handler binds the producer slot to it (T9/T85)
        // and never reads the principal itself, so the binding is decided in one place.
        return await mediator.Send(
            new RequestArtifactUploadUrl(
                request.RunId,
                request.ProducerId,
                request.ExecutionKey,
                request.AttemptNo,
                request.Kind,
                request.MediaType,
                request.Bytes,
                request.Sha256,
                principal.CredentialId,
                principal.CiRun?.ToBindingReference(),
                IdempotencyKey(context)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /v1/artifacts/{a}</c>: the metadata of one artifact. The pipeline authorizes <c>artifact:read</c>; the
    /// shared read model decides whether the caller also holds <c>artifact:read_sensitive</c> and masks the content
    /// when it does not, so a gated answer is a 200 with <c>content_gated: true</c> and never a 403.
    /// </summary>
    private static ValueTask<Result<ArtifactMetadataView>> GetArtifactMetadataAsync(
        HttpContext context,
        Guid a,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = NoStore;
        return mediator.Send(new Application.Artifacts.GetArtifactMetadata(a), cancellationToken);
    }

    /// <summary>
    /// <c>GET /v1/artifacts/{a}/download-url</c>: the gate decides first — it is the only place the
    /// <c>artifact:read_sensitive</c> answer and the row are read together, and the only place a denial's audit row
    /// is written and committed before the command transaction exists. The command then presigns the GET the gate
    /// cleared.
    /// </summary>
    private static async Task<Result<DownloadUrlResponse>> IssueDownloadUrlAsync(
        HttpContext context,
        Guid a,
        IRequestPrincipalAccessor principals,
        Application.Artifacts.ArtifactDownloadGate gate,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = NoStore;

        if (principals.Current is not { } principal)
        {
            return Result.Failure<DownloadUrlResponse>(Unauthenticated);
        }

        var authorized = await gate.AuthorizeAsync(a, principal, cancellationToken).ConfigureAwait(false);
        if (!authorized.IsSuccess)
        {
            return Result.Failure<DownloadUrlResponse>(authorized.Error!);
        }

        var row = authorized.Value;

        return await mediator.Send(
            new Application.Artifacts.IssueArtifactDownloadUrl(a),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /v1/runs/{r}/reports/{report_id}/comparison</c>: one stored report against the events its parse wrote.
    /// The pipeline authorizes <c>run:read</c>; the <c>limit</c>/<c>cursor</c> binding is the cursor-list convention
    /// of 00 §9.1, and the answer is per-caller, so no cache may store it.
    /// </summary>
    /// <param name="r">The run the report must belong to.</param>
    /// <param name="report_id">The stored report the path names.</param>
    /// <param name="context">The request, for the query string and the response headers.</param>
    /// <param name="mediator">Dispatches the read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    private static async Task<Result<ReportComparison>> GetReportComparisonAsync(
        Guid r,
        Guid report_id,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = NoStore;

        if (ComparisonLimitError(context.Request.Query["limit"], out var limit) is { } error)
        {
            return Result.Failure<ReportComparison>(error);
        }

        return await mediator.Send(
            new Application.Reports.GetReportComparison(r, report_id, Blank(context.Request.Query["cursor"]), limit),
            cancellationToken).ConfigureAwait(false);
    }

    private static Error Unauthenticated => new(ApiErrorCode.Unauthenticated, "authentication is required", StatusCodes.Status401Unauthorized);

    /// <summary>00 §7 / the <c>idempotency_record</c> key column: the C2 idempotency stage's own bound (T92).</summary>
    private const int MaxIdempotencyKeyLength = 128;

    /// <summary>
    /// The <c>Idempotency-Key</c> rule of the C2 idempotency stage (<c>IdempotencyBehaviour</c>, 00 §5.5 C2,
    /// T92), applied endpoint-side so a request the stage will refuse never reaches the verifier's permission read
    /// or the store: a missing/blank key is <c>required</c> and one longer than <see cref="MaxIdempotencyKeyLength"/>
    /// is <c>too_long</c>, both the stage's 400 <c>request_invalid</c> — the same message and detail the stage
    /// produces, so the two ends cannot disagree. Null means the key is acceptable.
    /// </summary>
    /// <param name="context">The request carrying the header.</param>
    private static Error? IdempotencyKeyError(HttpContext context)
    {
        var key = IdempotencyKey(context);

        return string.IsNullOrWhiteSpace(key) || key.Length > MaxIdempotencyKeyLength
            ? Error.BadRequest(
                $"Idempotency-Key must be 1..{MaxIdempotencyKeyLength} characters",
                [new ErrorDetail("Idempotency-Key", string.IsNullOrWhiteSpace(key) ? "required" : "too_long")])
            : null;
    }

    private static string IdempotencyKey(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString();

    /// <summary>
    /// The comparison page size: absent or blank keeps the default. A value that is not the canonical decimal
    /// spelling of an integer is the route's 400 (malformed), while a canonical integer outside 1–200 — a negative
    /// one included — is the list 422 with <c>details[].path</c> = <c>limit</c>, exactly as the evidence-revision
    /// list binds the same member.
    /// </summary>
    /// <param name="raw">The raw query value.</param>
    /// <param name="limit">The page size the query uses.</param>
    private static Error? ComparisonLimitError(string? raw, out int limit)
    {
        limit = DefaultLimit;

        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        // A sign is allowed so that `-1` reads as the integer it is and takes the range answer below, not the
        // malformed one; the canonical spelling check then refuses `+5` and `01`, whose value alone would pass.
        if (!BigInteger.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
            || !string.Equals(parsed.ToString(CultureInfo.InvariantCulture), raw, StringComparison.Ordinal))
        {
            return Error.BadRequest(
                "limit must be a canonical integer (00 §9.1); the request is malformed",
                [new ErrorDetail("limit", "malformed")]);
        }

        // Read unbounded so a canonical integer of any size is a range error, not a malformed one.
        if (parsed < 1 || parsed > MaxLimit)
        {
            return Error.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"limit must be an integer between 1 and {MaxLimit} (00 §9.1)"),
                [new ErrorDetail("limit", "out_of_range")]);
        }

        limit = (int)parsed;

        return null;
    }

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>
/// Body of <c>POST /v1/artifacts/upload-url</c> (06.1.2 §5, T85): the run, the producer slot, the planned
/// <c>(execution_key, attempt_no)</c> pair, the kind, the media type, the declared size and the declared checksum.
/// The wire names are the spec's snake_case ones; <c>Idempotency-Key</c> is a header, never a body member (T92).
/// </summary>
/// <param name="RunId">The run the artifact is captured in.</param>
/// <param name="ProducerId">The manifest shard's deterministic producer id (T54).</param>
/// <param name="ExecutionKey">The attempt's execution key, sha256 hex (00 §7).</param>
/// <param name="AttemptNo">The attempt number within the execution key, at least 1.</param>
/// <param name="Kind">One of the 00 §7 artifact kinds.</param>
/// <param name="MediaType">The declared media type, 1–255 characters.</param>
/// <param name="Bytes">The declared size in bytes, greater than zero.</param>
/// <param name="Sha256">The declared content address, <c>sha256:&lt;64 lowercase hex&gt;</c>.</param>
public sealed record UploadUrlRequest(
    [property: JsonPropertyName("run_id")] Guid RunId,
    [property: JsonPropertyName("producer_id")] string ProducerId,
    [property: JsonPropertyName("execution_key")] string ExecutionKey,
    [property: JsonPropertyName("attempt_no")] int AttemptNo,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>
/// 200 body of <c>POST /v1/artifacts/upload-url</c> (06.1.2 §5, AC1). <see cref="ObjectKey"/> is the
/// <c>pending/&lt;org_id&gt;/&lt;artifact_id&gt;</c> staging key the client <c>PUT</c>s to;
/// <see cref="RequiredHeaders"/> are the signed headers the client must send verbatim
/// (<c>Content-Length</c> = the declared <c>bytes</c>, <c>x-amz-checksum-sha256</c> = the base64 of the declared
/// digest); <see cref="ExpiresAt"/> is at most 15 minutes out. The URL is a bearer credential and is never logged.
/// </summary>
/// <param name="ArtifactId">The minted artifact's UUIDv7 id.</param>
/// <param name="ObjectKey">The server-chosen staging key the client uploads to.</param>
/// <param name="UploadUrl">The presigned <c>PUT</c> URL.</param>
/// <param name="RequiredHeaders">The signed headers the client must send verbatim.</param>
/// <param name="ExpiresAt">When the URL stops being valid.</param>
public sealed record UploadUrlResponse(
    [property: JsonPropertyName("artifact_id")] Guid ArtifactId,
    [property: JsonPropertyName("object_key")] string ObjectKey,
    [property: JsonPropertyName("upload_url")] Uri UploadUrl,
    [property: JsonPropertyName("required_headers")] IReadOnlyDictionary<string, string> RequiredHeaders,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

/// <summary>
/// 200 body of <c>POST /v1/artifacts/{a}/complete</c> (06.1.2 §5, AC3/AC4/AC5). <see cref="State"/> is
/// <c>ready</c> when the staged object matched the declared size and checksum and the commit landed, or
/// <c>rejected</c> when verification refused it — a rejection is a committed 200 carrying
/// <see cref="FailureCode"/>, never an error response. <see cref="Bytes"/> is the length the store reported and is
/// <c>null</c> on a rejection (nothing was verified); <see cref="ScanState"/> is <c>pending</c> on a <c>ready</c>
/// artifact and <c>null</c> on a rejected one. The final object key is never returned.
/// <para>
/// It lives in this namespace, not in <c>Application</c>, because the 00 §5.5 module-boundary rule allows a
/// public type inside a module only under <c>.Contracts</c>/<c>.Endpoints</c> (the same reason
/// <see cref="UploadUrlResponse"/> is declared here); the writer command and its handler reach it through
/// <c>using Evidgate.Modules.Evidence.Endpoints;</c>.
/// </para>
/// </summary>
/// <param name="ArtifactId">The artifact that was completed.</param>
/// <param name="State"><c>ready</c> or <c>rejected</c> (00 §8.4).</param>
/// <param name="Bytes">The verified size, or <c>null</c> when nothing was verified.</param>
/// <param name="ScanState">The masking-scan state of a <c>ready</c> artifact (<c>pending</c>/<c>clean</c>/<c>flagged</c>), else <c>null</c>.</param>
/// <param name="FailureCode">One of the <c>artifact.failure_code</c> reasons, or <c>null</c> when verified.</param>
public sealed record CompleteResponse(
    [property: JsonPropertyName("artifact_id")] Guid ArtifactId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("bytes")] long? Bytes,
    [property: JsonPropertyName("scan_state")] string? ScanState,
    [property: JsonPropertyName("failure_code")] string? FailureCode);

/// <summary>
/// 200 body of <c>GET /v1/artifacts/{a}</c>: the artifact's descriptive metadata. <see cref="Sensitivity"/> is the
/// effective classification every read path enforces (raised by the project's kind policy and by an unfinished or
/// flagged scan, never lowered by reading), and <see cref="ContentGated"/> is true when the caller holds only
/// <c>artifact:read</c>: the storage key, the checksum and the byte count are then absent from the document, not
/// null-ed in place, so no client can mistake a redacted field for a value.
/// </summary>
/// <param name="ArtifactId">The artifact id.</param>
/// <param name="ProjectId">The owning project.</param>
/// <param name="RunId">The run the artifact belongs to.</param>
/// <param name="Kind">One of the eight contract kinds.</param>
/// <param name="MediaType">The stored media type.</param>
/// <param name="State">The artifact's lifecycle state.</param>
/// <param name="Sensitivity"><c>normal</c> or <c>sensitive</c>, as resolved for this read.</param>
/// <param name="ScanState">The secret-scan state, or null when the row carries none.</param>
/// <param name="Bytes">The stored length, or null when the content is gated.</param>
/// <param name="Checksum">The content address, or null when the content is gated.</param>
/// <param name="ObjectKey">The storage key, or null when the content is gated.</param>
/// <param name="ContentGated">True when the content-bearing members were withheld from this caller, either because
/// it may not read sensitive content or because the row's bytes are being deleted.</param>
/// <param name="CreatedAt">When the row was created.</param>
public sealed record ArtifactMetadataView(
    [property: JsonPropertyName("artifact_id")] Guid ArtifactId,
    [property: JsonPropertyName("project_id")] Guid ProjectId,
    [property: JsonPropertyName("run_id")] Guid RunId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("sensitivity")] string Sensitivity,
    [property: JsonPropertyName("scan_state")] string? ScanState,
    [property: JsonPropertyName("bytes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Bytes,
    [property: JsonPropertyName("checksum"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Checksum,
    [property: JsonPropertyName("object_key"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ObjectKey,
    [property: JsonPropertyName("content_gated")] bool ContentGated,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt)
{
    /// <summary>Renders one row of the shared read model; the masking already happened in its statement.</summary>
    internal static ArtifactMetadataView From(Application.Artifacts.ArtifactReadRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new ArtifactMetadataView(
            row.Id,
            row.ProjectId,
            row.RunId,
            row.Kind,
            row.MediaType,
            row.State,
            row.EffectiveSensitivity,
            row.ScanState,
            row.Bytes,
            row.Checksum,
            row.ObjectKey,
            row.ContentGated,
            row.CreatedAt);
    }
}

/// <summary>
/// 200 body of <c>GET /v1/artifacts/{a}/download-url</c>: a short-lived presigned <c>GET</c> for the artifact's
/// bytes. The URL is a bearer credential, so it is returned once, never logged and never audited; the audit row
/// names the artifact, its run and the outcome only.
/// </summary>
/// <param name="ArtifactId">The artifact the URL serves.</param>
/// <param name="DownloadUrl">The presigned <c>GET</c> URL.</param>
/// <param name="ExpiresAt">When the URL stops being valid.</param>
public sealed record DownloadUrlResponse(
    [property: JsonPropertyName("artifact_id")] Guid ArtifactId,
    [property: JsonPropertyName("download_url")] Uri DownloadUrl,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
