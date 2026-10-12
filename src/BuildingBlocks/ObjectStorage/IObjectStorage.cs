namespace Evidgate.BuildingBlocks.ObjectStorage;

/// <summary>
/// The object-storage seam (06.1.2, 00 §9.3 <c>POST /v1/artifacts/upload-url</c> and <c>/complete</c>):
/// the API mints a short-lived presigned <c>PUT</c> for a server-chosen staging key
/// so the client uploads bytes straight to storage and the API never proxies the
/// bandwidth (DECISIONS §9). Presigning is CPU-only (SigV4 arithmetic, no network call);
/// <see cref="HeadAsync"/>, <see cref="ReadPrefixAsync"/>, <see cref="CopyAsync"/> and
/// <see cref="DeleteAsync"/> are network calls and MUST run outside any database transaction
/// (00 §5.7 P5, T86).
/// </summary>
/// <remarks>
/// Keys are always server-minted (<c>pending/&lt;org_id&gt;/&lt;artifact_id&gt;</c> staging or the
/// FR-06-1 final key); the adapter still refuses any key with traversal, an empty segment, a
/// backslash or a control character. No key, URL, checksum or credential ever reaches a log
/// line or an exception message (00 §5.7 S3).
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

    /// <summary>
    /// <c>HEAD</c>s one object (06.1.2-T2, T86). Returns <c>null</c> when the object does not exist
    /// (a store 404 is an outcome, not an exception).
    /// </summary>
    /// <param name="objectKey">A server-minted staging or final key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The stored length and checksum, or <c>null</c> when absent.</returns>
    Task<ObjectHead?> HeadAsync(string objectKey, CancellationToken cancellationToken);

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> bytes from the start of one object (06.3.1-T1: the media
    /// sniff at <c>complete</c> reads a bounded prefix, never the whole artifact). The request is a ranged
    /// <c>GET</c> (<c>bytes=0-(maxBytes-1)</c>), so a store never sends more than asked for; the response
    /// stream is disposed before returning. Returns <c>null</c> when the object does not exist (a store 404
    /// is an outcome, not an exception, exactly like <see cref="HeadAsync"/>). A zero-byte object answers an
    /// empty array: the store may report the unsatisfiable range as 416 <c>InvalidRange</c>, which is also an
    /// outcome, not an exception.
    /// </summary>
    /// <param name="objectKey">A server-minted staging or final key.</param>
    /// <param name="maxBytes">The prefix cap in bytes, between 1 and 65536.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The first <paramref name="maxBytes"/> bytes (fewer when the object is shorter), or <c>null</c> when absent.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is below 1 or above 65536.</exception>
    Task<byte[]?> ReadPrefixAsync(string objectKey, int maxBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Copies the staging object <paramref name="sourceKey"/> (a <c>pending/</c> key) to the final
    /// <paramref name="destinationKey"/> (never a <c>pending/</c> key), preserving the SHA-256 checksum
    /// so a later <see cref="HeadAsync"/> of the destination reports it. Idempotent: copying onto an
    /// existing destination overwrites it with the same bytes. Throws when the source is missing.
    /// </summary>
    /// <param name="sourceKey">The staging key.</param>
    /// <param name="destinationKey">The final key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task CopyAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes one object. Idempotent: deleting a missing key succeeds (T86: the 24 h lifecycle may
    /// already have collected the staging object).
    /// </summary>
    /// <param name="objectKey">A server-minted staging or final key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);
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

/// <summary>
/// What a <c>HEAD</c> reports about a stored object (06.1.2-T2). <see cref="ChecksumSha256Hex"/> is
/// the lowercase hex of the SHA-256 the store recorded when the object was written (the signed
/// <c>x-amz-checksum-sha256</c> of the presigned <c>PUT</c>), or <c>null</c> when the store holds no
/// SHA-256 checksum for the object; a caller must treat <c>null</c> as "not verified".
/// </summary>
/// <param name="ContentLength">The stored byte count.</param>
/// <param name="ChecksumSha256Hex">64 lowercase hex characters, or <c>null</c> when none is recorded.</param>
public sealed record ObjectHead(long ContentLength, string? ChecksumSha256Hex);
