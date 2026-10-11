namespace Evidgate.BuildingBlocks.ObjectStorage;

/// <summary>
/// The object-storage seam (06.1.2-T1, 00 §9.3 <c>POST /v1/artifacts/upload-url</c>):
/// the API mints a short-lived presigned <c>PUT</c> for a server-chosen staging key
/// so the client uploads bytes straight to storage and the API never proxies the
/// bandwidth (DECISIONS §9). The adapter is CPU-only — presigning is SigV4 arithmetic,
/// no network call — so a request handler can call it inside its own scope.
/// </summary>
/// <remarks>
/// The interface carries only the presign half of the adapter in this task; the
/// <c>HEAD</c>/<c>Copy</c>/<c>Delete</c> members the <c>complete</c> path needs
/// (06.1.2-T2) are added with that task.
/// </remarks>
public interface IObjectStorage
{
    /// <summary>
    /// Presigns a <c>PUT</c> that binds the declared byte count and the SHA-256
    /// checksum (T18): both are part of the SigV4 signature, so a client cannot
    /// upload a different length or different bytes without invalidating the URL.
    /// </summary>
    /// <param name="request">The staging key, declared length and checksum.</param>
    /// <returns>The presigned URL, the headers the client must send verbatim, and the expiry.</returns>
    /// <exception cref="ArgumentException">
    /// <see cref="PresignPutRequest.ObjectKey"/> is not a plain <c>pending/</c> staging
    /// key, or <see cref="PresignPutRequest.ChecksumSha256Hex"/> is not a SHA-256 hex
    /// digest. The message never carries the key or the checksum value.
    /// </exception>
    PresignedPut PresignPut(PresignPutRequest request);
}

/// <summary>
/// A presign request (06.1.2-T1). The key is the deterministic staging key
/// <c>pending/&lt;org_id&gt;/&lt;artifact_id&gt;</c> the handler mints; the checksum is
/// the lowercase hex SHA-256 of the bytes the client will upload, without the
/// <c>sha256:</c> prefix.
/// </summary>
/// <param name="ObjectKey">The staging key; must start with <c>pending/</c>.</param>
/// <param name="ContentLength">The declared byte count, signed as <c>Content-Length</c>.</param>
/// <param name="ChecksumSha256Hex">64 lowercase hex characters of the raw SHA-256 digest.</param>
public sealed record PresignPutRequest(string ObjectKey, long ContentLength, string ChecksumSha256Hex);

/// <summary>
/// A presigned <c>PUT</c> (06.1.2-T1). <see cref="RequiredHeaders"/> holds exactly
/// the signed headers the client must send verbatim — <c>Content-Length</c> and
/// <c>x-amz-checksum-sha256</c> (base64 of the raw digest) — so a client that sends
/// them unchanged cannot upload a different length or different bytes.
/// </summary>
/// <param name="Url">The presigned URL.</param>
/// <param name="RequiredHeaders">The signed headers the client must send verbatim.</param>
/// <param name="ExpiresAt">When the URL stops being valid.</param>
public sealed record PresignedPut(
    Uri Url,
    IReadOnlyDictionary<string, string> RequiredHeaders,
    DateTimeOffset ExpiresAt);
