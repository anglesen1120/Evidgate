using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Application;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Evidence.Endpoints;

/// <summary>
/// The artifact routes of 06.1.2 §5. The body is read with the host's snake_case JSON options and validated by the
/// command's FluentValidation validator; authorization is the C2 pipeline and the 00 §9.2 envelope is the host's
/// <c>ResultEndpointFilter</c>.
/// </summary>
public static class EvidenceEndpoints
{
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

        // The body is read explicitly rather than bound by the request-delegate factory: the spec declares this
        // request body inline (no component schema), so the served document must not name one, and every body-read
        // failure mode still produces the 00 §9.2 envelope — malformed JSON, an unsupported/missing content type
        // (InvalidOperationException) and Kestrel's own BadHttpRequestException (whose status, e.g. 413, is kept).
        UploadUrlRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<UploadUrlRequest>(cancellationToken).ConfigureAwait(false);
        }
        catch (BadHttpRequestException exception)
        {
            var status = exception.StatusCode;
            return Result.Failure<UploadUrlResponse>(new Error(
                status == StatusCodes.Status413PayloadTooLarge ? ApiErrorCode.PayloadTooLarge : ApiErrorCode.RequestInvalid,
                "the request body could not be read",
                status));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            return Result.Failure<UploadUrlResponse>(Error.BadRequest("the request body is not valid JSON"));
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
