using System.Globalization;
using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Endpoints;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Evidgate.BuildingBlocks.ObjectStorage;

/// <summary>
/// The <c>AWSSDK.S3</c> implementation of <see cref="IObjectStorage"/> (06.1.2-T1, T2).
/// Presigning is SigV4 arithmetic over the request — no network call — so it is safe to
/// call from a request handler. <see cref="HeadAsync"/>, <see cref="ReadPrefixAsync"/>,
/// <see cref="CopyAsync"/> and <see cref="DeleteAsync"/> do make a network call and MUST run
/// outside any database transaction (00 §5.7 P5, T86). The S3 client is created once, lazily and
/// thread-safely, and disposed with the adapter.
/// </summary>
/// <remarks>
/// The signed headers are exactly the ones the 01.8.1-T1 parity suite proved on
/// SeaweedFS and the provider (T18): <c>Content-Length</c> and
/// <c>x-amz-checksum-sha256</c> (base64 of the raw digest). No URL, key, checksum or
/// credential ever reaches a log line or an exception message (00 §5.7 S3): a failed
/// call is wrapped in <see cref="ObjectStorageException"/>, which carries only the
/// operation and the non-secret S3 error code/status, and the SDK exception is never
/// chained (its message and response body may carry the key and the request URL).
/// </remarks>
internal sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    /// <summary>06.1.2 §3: staging keys are minted under this prefix (T18).</summary>
    internal const string PendingPrefix = "pending/";

    /// <summary>The provider-neutral checksum header the presigned PUT binds (T18).</summary>
    internal const string ChecksumHeaderName = "x-amz-checksum-sha256";

    /// <summary>
    /// The largest prefix <see cref="ReadPrefixAsync"/> will read (06.3.1-T1): a media sniff needs a few
    /// signature bytes, never a whole artifact, so the cap is the only bound the caller can name.
    /// </summary>
    internal const int MaxPrefixBytes = 65536;

    private const string ContentLengthHeaderName = "Content-Length";

    private readonly IOptions<ObjectStorageOptions> _options;
    private readonly Lazy<AmazonS3Client> _client;
    private bool _disposed;

    public S3ObjectStorage(IOptions<ObjectStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _client = new Lazy<AmazonS3Client>(CreateClient, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public PresignedPut PresignPut(PresignPutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ValidateStagingKey(request.ObjectKey, "objectKey");
        var checksumBase64 = ChecksumBase64(request.ChecksumSha256Hex);

        var options = _options.Value;
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(options.PresignExpirySeconds);

        var presign = new GetPreSignedUrlRequest
        {
            BucketName = options.Bucket,
            Key = request.ObjectKey,
            Verb = HttpVerb.PUT,
            Protocol = ProtocolFor(options.Endpoint),
        };
        var contentLengthValue = request.ContentLength.ToString(CultureInfo.InvariantCulture);
        presign.Headers[ContentLengthHeaderName] = contentLengthValue;
        presign.Headers[ChecksumHeaderName] = checksumBase64;

        var client = _client.Value;

        // The SDK signs X-Amz-Expires as (Expires - CorrectClockSkew.GetCorrectedUtcNowForEndpoint(endpoint)),
        // not (Expires - DateTime.UtcNow): a per-endpoint skew correction recorded from an earlier response (an
        // emulator whose clock is behind ours) would push the signed lifetime past the configured cap. Sign
        // against the same corrected clock the SDK reads, so the signed lifetime is exactly the configured
        // seconds. X-Amz-Date stays the local clock, so ExpiresAt (local now + the cap) is what the URL carries.
        var endpoint = client.Config
            .DetermineServiceOperationEndpoint(new ServiceOperationEndpointParameters(presign)).URL;
        presign.Expires = CorrectClockSkew.GetCorrectedUtcNowForEndpoint(endpoint)
            .AddSeconds(options.PresignExpirySeconds);

        var url = new Uri(client.GetPreSignedURL(presign), UriKind.Absolute);

        return new PresignedPut(
            url,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ContentLengthHeaderName] = contentLengthValue,
                [ChecksumHeaderName] = checksumBase64,
            },
            expiresAt);
    }

    /// <inheritdoc />
    public async Task<ObjectHead?> HeadAsync(string objectKey, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateObjectKey(objectKey, nameof(objectKey));

        var options = _options.Value;
        GetObjectMetadataResponse metadata;
        try
        {
            metadata = await _client.Value.GetObjectMetadataAsync(
                new GetObjectMetadataRequest
                {
                    BucketName = options.Bucket,
                    Key = objectKey,
                    // The store echoes x-amz-checksum-sha256 only when the request asks for it
                    // (observed on SeaweedFS 4.47: without the mode the HEAD carries no checksum).
                    ChecksumMode = ChecksumMode.ENABLED,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (IsAbsent(exception))
        {
            // A store 404 is an outcome, not an exception (IObjectStorage.HeadAsync).
            return null;
        }
        catch (AmazonServiceException exception)
        {
            // SDK v4: AmazonServiceException no longer derives from AmazonClientException, so an S3
            // service error (AmazonS3Exception) is not caught by the client clause below; without
            // this clause its message — which may carry the key and the request URL — escapes.
            throw StorageFailure("head", exception);
        }
        catch (AmazonClientException exception)
        {
            throw StorageFailure("head", exception);
        }

        return new ObjectHead(metadata.ContentLength, ChecksumHex(metadata.ChecksumSHA256));
    }

    /// <inheritdoc />
    public async Task<byte[]?> ReadPrefixAsync(string objectKey, int maxBytes, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateObjectKey(objectKey, nameof(objectKey));

        if (maxBytes is < 1 or > MaxPrefixBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBytes), maxBytes, "maxBytes must be between 1 and 65536");
        }

        var options = _options.Value;
        try
        {
            // The range bounds the response: the store sends at most maxBytes, so the read below can never
            // pull a whole artifact into memory (00 §5.7 P1, 06.3.1 AC5).
            using var response = await _client.Value.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = options.Bucket,
                    Key = objectKey,
                    ByteRange = new ByteRange(0, maxBytes - 1),
                },
                cancellationToken).ConfigureAwait(false);

            return await ReadBoundedAsync(response.ResponseStream, maxBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (IsAbsent(exception))
        {
            // A store 404 is an outcome, not an exception (IObjectStorage.ReadPrefixAsync).
            return null;
        }
        catch (AmazonServiceException exception)
        {
            // SDK v4: AmazonServiceException no longer derives from AmazonClientException, so a store
            // service error would escape the client clause below with the SDK's message intact.
            throw StorageFailure("read-prefix", exception);
        }
        catch (AmazonClientException exception)
        {
            throw StorageFailure("read-prefix", exception);
        }
    }

    /// <summary>
    /// Reads the response stream up to <paramref name="maxBytes"/> bytes, disposing it first. The ranged GET
    /// already caps the body, but a store that ignores the range is still read no further than the cap, so the
    /// read is bounded by construction.
    /// </summary>
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[maxBytes];
            var total = 0;
            while (total < maxBytes)
            {
                var read = await stream
                    .ReadAsync(buffer.AsMemory(total, maxBytes - total), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return total == buffer.Length ? buffer : buffer[..total];
        }
    }

    /// <inheritdoc />
    public async Task CopyAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateStagingKey(sourceKey, nameof(sourceKey));
        ValidateFinalKey(destinationKey, nameof(destinationKey));

        var options = _options.Value;
        try
        {
            await _client.Value.CopyObjectAsync(
                new CopyObjectRequest
                {
                    SourceBucket = options.Bucket,
                    SourceKey = sourceKey,
                    DestinationBucket = options.Bucket,
                    DestinationKey = destinationKey,
                    // COPY keeps the source metadata (the checksum the presigned PUT bound); asking
                    // for SHA256 makes the destination report the same checksum on a later HEAD, and
                    // an overwrite of an existing destination is the same copy again (idempotent).
                    MetadataDirective = S3MetadataDirective.COPY,
                    ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonServiceException exception)
        {
            // SDK v4: AmazonServiceException no longer derives from AmazonClientException, so a COPY
            // of a missing or forbidden source surfaces as AmazonS3Exception and would escape the
            // client clause below with the SDK's message (key and request URL) intact.
            throw StorageFailure("copy", exception);
        }
        catch (AmazonClientException exception)
        {
            throw StorageFailure("copy", exception);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateObjectKey(objectKey, nameof(objectKey));

        var options = _options.Value;
        try
        {
            await _client.Value.DeleteObjectAsync(
                new DeleteObjectRequest
                {
                    BucketName = options.Bucket,
                    Key = objectKey,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (IsAbsent(exception))
        {
            // S3 deletes of a missing key already succeed (204); a store that answers 404 is the
            // same outcome, so the delete stays idempotent either way (IObjectStorage.DeleteAsync).
        }
        catch (AmazonServiceException exception)
        {
            // SDK v4: AmazonServiceException no longer derives from AmazonClientException, so a store
            // service error would escape the client clause below with the SDK's message intact.
            throw StorageFailure("delete", exception);
        }
        catch (AmazonClientException exception)
        {
            throw StorageFailure("delete", exception);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Builds the S3 client from the resolved options: <c>ServiceURL</c> from the
    /// validated endpoint when one is set, else the AWS regional endpoint of the
    /// configured region. Path-style addressing follows the option (true for
    /// SeaweedFS).
    /// </summary>
    private AmazonS3Client CreateClient()
    {
        var options = _options.Value;
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            AuthenticationRegion = options.Region,
        };

        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }
        else
        {
            config.ServiceURL = options.Endpoint;
        }

        return new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
    }

    /// <summary>
    /// The scheme the presigned URL carries: a local emulator endpoint is plain
    /// HTTP, a real provider (or the AWS regional endpoint) stays HTTPS.
    /// </summary>
    private static Protocol ProtocolFor(string? endpoint) =>
        string.IsNullOrWhiteSpace(endpoint)
        || endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? Protocol.HTTPS
            : Protocol.HTTP;

    /// <summary>
    /// Every key the adapter touches must be a plain relative key: non-empty, no leading or
    /// trailing slash, no empty segment, no traversal, no backslash and no control character,
    /// so a crafted key can never escape the prefix it names. The message never carries the
    /// key (00 §5.7 S3); <paramref name="paramName"/> names the offending argument.
    /// </summary>
    private static void ValidateObjectKey(string objectKey, string paramName)
    {
        if (string.IsNullOrEmpty(objectKey)
            || objectKey.StartsWith('/')
            || objectKey.EndsWith('/')
            || objectKey.Contains("..", StringComparison.Ordinal)
            || objectKey.Contains("//", StringComparison.Ordinal)
            || objectKey.Contains('\\')
            || objectKey.Any(char.IsControl))
        {
            throw new ArgumentException(
                "ObjectKey must be a plain relative key with no empty segment, traversal, backslash or control character",
                paramName);
        }
    }

    /// <summary>
    /// A staging key is a plain <c>pending/&lt;org_id&gt;/&lt;artifact_id&gt;</c> string (06.1.2 §5): it
    /// must start with <c>pending/</c> so a crafted key cannot name a final object, and it must pass
    /// <see cref="ValidateObjectKey"/> so it cannot escape the staging prefix.
    /// </summary>
    private static void ValidateStagingKey(string objectKey, string paramName)
    {
        ValidateObjectKey(objectKey, paramName);
        if (!objectKey.StartsWith(PendingPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "ObjectKey must be a plain pending/ staging key", paramName);
        }
    }

    /// <summary>
    /// The destination of a copy is always a final key (T86): a <c>pending/</c> destination is
    /// refused, so a copy can never write back into the staging prefix.
    /// </summary>
    private static void ValidateFinalKey(string destinationKey, string paramName)
    {
        ValidateObjectKey(destinationKey, paramName);
        if (destinationKey.StartsWith(PendingPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "DestinationKey must not be a pending/ staging key", paramName);
        }
    }

    /// <summary>
    /// True when the store answered "no such object": the SDK surfaces a <c>HEAD</c>/<c>COPY</c> 404
    /// (SeaweedFS answers an empty-bodied 404) as <see cref="HttpStatusCode.NotFound"/>, and a store
    /// that names the code instead reports <c>NoSuchKey</c>.
    /// </summary>
    private static bool IsAbsent(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound
        || string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.Ordinal)
        || string.Equals(exception.ErrorCode, "NotFound", StringComparison.Ordinal);

    /// <summary>
    /// Lowercase hex of a base64 SHA-256 the store reported, or <c>null</c> when the store holds
    /// none. A value that is not a 32-byte digest is "not verified" (<c>null</c>), never a wrong
    /// digest: the caller fails closed on <c>null</c> (T86).
    /// </summary>
    private static string? ChecksumHex(string? checksumSha256Base64)
    {
        if (string.IsNullOrEmpty(checksumSha256Base64))
        {
            return null;
        }

        try
        {
            var digest = Convert.FromBase64String(checksumSha256Base64);
            return digest.Length == 32 ? Convert.ToHexStringLower(digest) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Wraps a failed storage call so no key, URL, checksum or credential can reach a log line: only
    /// the operation and the non-secret S3 error code/status are carried. The SDK exception is never
    /// chained, because its message and response body may carry the request URL and the key
    /// (00 §5.7 S3).
    /// </summary>
    private static ObjectStorageException StorageFailure(string operation, Exception exception) =>
        exception is AmazonServiceException service
            ? new ObjectStorageException(operation, service.ErrorCode, service.StatusCode)
            : new ObjectStorageException(operation, null, null);

    /// <summary>Base64 of the raw digest, the value the signed checksum header carries.</summary>
    private static string ChecksumBase64(string checksumSha256Hex)
    {
        if (checksumSha256Hex is null
            || checksumSha256Hex.Length != 64
            || !checksumSha256Hex.All(IsHexDigit))
        {
            throw new ArgumentException(
                "ChecksumSha256Hex must be 64 hex characters", nameof(checksumSha256Hex));
        }

        return Convert.ToBase64String(Convert.FromHexString(checksumSha256Hex));
    }

    private static bool IsHexDigit(char value) =>
        (value >= '0' && value <= '9')
        || (value >= 'a' && value <= 'f')
        || (value >= 'A' && value <= 'F');
}
