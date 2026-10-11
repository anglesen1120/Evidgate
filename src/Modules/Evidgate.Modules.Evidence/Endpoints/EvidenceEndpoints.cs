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

        return group;
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
