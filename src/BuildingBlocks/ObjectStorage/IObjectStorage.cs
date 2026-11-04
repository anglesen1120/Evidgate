namespace Evidgate.BuildingBlocks.ObjectStorage;

/// <summary>
/// The object-storage seam (06.1.2, 00 §9.3 <c>POST /v1/artifacts/upload-url</c> and <c>/complete</c>):
/// the API mints a short-lived presigned <c>PUT</c> for a server-chosen staging key
/// so the client uploads bytes straight to storage and the API never proxies the
/// bandwidth (DECISIONS §9). Presigning is CPU-only (SigV4 arithmetic, no network call);
/// <see cref="HeadAsync"/>, <see cref="ReadPrefixAsync"/>, <see cref="GetAsync"/>, <see cref="CopyAsync"/> and
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
    /// Presigns a <c>GET</c> of one stored object, the read half of the same seam: the download route hands the
    /// client a short-lived URL for bytes it was authorized to see, so the API never proxies the download. Like
    /// <see cref="PresignPut"/> it is SigV4 arithmetic only — no network call — and the URL is a bearer credential
    /// that is never logged, thrown or audited.
    /// </summary>
    /// <param name="objectKey">A server-minted final key.</param>
    /// <returns>The presigned URL and its expiry.</returns>
    PresignedGet PresignGet(string objectKey);

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
    /// <para>
    /// When <paramref name="ifMatch"/> is set the GET is conditional (<c>If-Match</c>) on the ETag a preceding
    /// <see cref="HeadAsync"/> of the same object reported (06.3.1-T2.2): the bytes returned are then the object
    /// that HEAD verified, never a replacement written between the two calls. A store that finds a different
    /// object answers <c>412 PreconditionFailed</c> and the adapter throws
    /// <see cref="ObjectStorageException"/> (it is not an absent-object outcome), so the caller fails closed.
    /// </para>
    /// </summary>
    /// <param name="objectKey">A server-minted staging or final key.</param>
    /// <param name="maxBytes">The prefix cap in bytes, between 1 and 65536.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <param name="ifMatch">
    /// The ETag the object must still carry, from the preceding <see cref="HeadAsync"/>, or <c>null</c> to read
    /// unconditionally. Passed verbatim as <c>If-Match</c> (the store's own ETag value, quotes included).
    /// </param>
    /// <returns>The first <paramref name="maxBytes"/> bytes (fewer when the object is shorter), or <c>null</c> when absent.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is below 1 or above 65536.</exception>
    /// <exception cref="ObjectStorageException">The store refused the conditional read (the object changed).</exception>
    Task<byte[]?> ReadPrefixAsync(string objectKey, int maxBytes, CancellationToken cancellationToken, string? ifMatch = null);

    /// <summary>
    /// Fetches one whole object, bounded by <paramref name="maxBytes"/> (06.3.6-T1.4): the <c>parse</c> handler hands
    /// an artifact's bytes to the child process from here, so the caller names the cap its own kind policy allows
    /// (<c>artifact_kind_policy.max_bytes</c>) and never buffers more than that. Returns <see langword="null"/> when
    /// the object does not exist (a store 404 is an outcome, not an exception, exactly like <see cref="HeadAsync"/>);
    /// a zero-byte object answers an empty array. The read allocates one buffer of the object's own length, so its
    /// peak is about one copy of the artifact.
    /// <para>
    /// The read fails closed on a longer object: an object above <paramref name="maxBytes"/> throws
    /// <see cref="InvalidOperationException"/> instead of answering a truncated array, because a truncated report is
    /// a different document and must never be parsed as if it were the stored one.
    /// </para>
    /// <para>
    /// Like every other storage call this is a network call and MUST run outside any database transaction
    /// (00 §5.7 P5) — with one recorded exception: the Worker's <c>artifact_parse</c> handler calls it inside the
    /// consumer-owned tenant transaction, which that handler cannot release. It is recorded as the P5 exception of
    /// 06.3.6-T1.4 with receiver <c>06.3.6-T2</c>, whose own parse queue and concurrency take the read out of the
    /// consumer's transaction. The exception belongs to that handler alone: a new caller MUST NOT read an object
    /// inside a transaction, and either reads after the commit or through a queue of its own.
    /// </para>
    /// </summary>
    /// <param name="objectKey">A server-minted final (or staging) key.</param>
    /// <param name="maxBytes">The most bytes the stored object may hold, between 1 and 268 435 456 (256 MiB).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The whole object, or <see langword="null"/> when it is absent.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is below 1 or above the cap.</exception>
    /// <exception cref="InvalidOperationException">The stored object is larger than <paramref name="maxBytes"/>.</exception>
    /// <exception cref="ObjectStorageException">The store refused the read.</exception>
    Task<byte[]?> GetAsync(string objectKey, long maxBytes, CancellationToken cancellationToken);

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
/// The hard ceiling of one stored object, shared by the adapter that refuses to buffer more and by the kind-policy
/// route that may not raise an artifact's cap above it. One constant, so the two can never disagree.
/// </summary>
public static class ObjectStorageLimits
{
    /// <summary>
    /// The largest object one read may buffer, 256 MiB: the highest kind cap the static defaults set
    /// (<c>raw_report</c>), so a cap above it would be a policy no scan or parse could ever read.
    /// </summary>
    public const long MaxObjectBytes = 256L * 1024 * 1024;
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
/// A presigned <c>GET</c> of one stored object. <see cref="Url"/> is a bearer credential: it is returned to the
/// authorized caller once and never logged, thrown or audited (00 §5.7 S3).
/// </summary>
/// <param name="Url">The presigned URL.</param>
/// <param name="ExpiresAt">When the URL stops being valid.</param>
public sealed record PresignedGet(Uri Url, DateTimeOffset ExpiresAt);

/// <summary>
/// What a <c>HEAD</c> reports about a stored object (06.1.2-T2). <see cref="ChecksumSha256Hex"/> is
/// the lowercase hex of the SHA-256 the store recorded when the object was written (the signed
/// <c>x-amz-checksum-sha256</c> of the presigned <c>PUT</c>), or <c>null</c> when the store holds no
/// SHA-256 checksum for the object; a caller must treat <c>null</c> as "not verified".
/// <see cref="ETag"/> is the entity tag the store reports (the value it would compare against
/// <c>If-Match</c>), or <c>null</c> when the store reports none; 06.3.1-T2.2 pins the prefix read with it.
/// </summary>
/// <param name="ContentLength">The stored byte count.</param>
/// <param name="ChecksumSha256Hex">64 lowercase hex characters, or <c>null</c> when none is recorded.</param>
/// <param name="ETag">The store's entity tag as reported, quotes included, or <c>null</c> when none is reported.</param>
public sealed record ObjectHead(long ContentLength, string? ChecksumSha256Hex, string? ETag = null);
