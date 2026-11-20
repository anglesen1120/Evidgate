using System.Globalization;
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
/// <see cref="CopyAsync"/>, <see cref="DeleteAsync"/> and <see cref="GetAsync"/> do make a network call
/// and MUST run outside any database transaction (00 §5.7 P5, T86) — except the Worker's
/// <c>artifact_parse</c> read of <see cref="GetAsync"/>, the one recorded exception of that rule
/// (06.3.6-T1.4, receiver 06.3.6-T2). The S3 client is created once, lazily and
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
internal sealed partial class S3ObjectStorage : IObjectStorage, IDisposable
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

    /// <summary>
    /// The largest object <see cref="GetAsync"/> will buffer (06.3.6-T1.4): the kind caps it serves are at most
    /// 256 MiB (<c>artifact_kind_policy.max_bytes</c>, D-A9), so one artifact fits and a caller cannot ask for a
    /// buffer the process could not hold.
    /// </summary>
    internal const long MaxObjectBytes = ObjectStorageLimits.MaxObjectBytes;

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
    public PresignedGet PresignGet(string objectKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateObjectKey(objectKey, nameof(objectKey));

        var options = _options.Value;
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(options.PresignExpirySeconds);

        var presign = new GetPreSignedUrlRequest
        {
            BucketName = options.Bucket,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Protocol = ProtocolFor(options.Endpoint),
        };

        var client = _client.Value;

        // Signed against the same skew-corrected clock as PresignPut, so the signed lifetime is exactly the
        // configured seconds whatever an emulator's clock reports.
        var endpoint = client.Config
            .DetermineServiceOperationEndpoint(new ServiceOperationEndpointParameters(presign)).URL;
        presign.Expires = CorrectClockSkew.GetCorrectedUtcNowForEndpoint(endpoint)
            .AddSeconds(options.PresignExpirySeconds);

        return new PresignedGet(new Uri(client.GetPreSignedURL(presign), UriKind.Absolute), expiresAt);
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

        return new ObjectHead(metadata.ContentLength, ChecksumHex(metadata.ChecksumSHA256), metadata.ETag);
    }

    /// <inheritdoc />
    public async Task<byte[]?> ReadPrefixAsync(
        string objectKey, int maxBytes, CancellationToken cancellationToken, string? ifMatch = null)
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
            // pull a whole artifact into memory (00 §5.7 P1, 06.3.1 AC5). When ifMatch names the ETag the
            // preceding HEAD reported, the GET is conditional (If-Match), so the bytes returned are that
            // object: a store that finds a replacement answers 412, which the caller fails closed on
            // (06.3.1-T2.2) — it is not the 404-is-an-outcome path below.
            using var response = await _client.Value.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = options.Bucket,
                    Key = objectKey,
                    ByteRange = new ByteRange(0, maxBytes - 1),
                    EtagToMatch = ifMatch,
                },
                cancellationToken).ConfigureAwait(false);

            return await ReadBoundedAsync(
                response.ResponseStream, maxBytes, response.ContentLength, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (IsAbsent(exception))
        {
            // A store 404 is an outcome, not an exception (IObjectStorage.ReadPrefixAsync).
            return null;
        }
        catch (AmazonS3Exception exception) when (IsEmptyRange(exception))
        {
            // A zero-byte object: the store answers 416 InvalidRange because no byte can satisfy the requested
            // range. The object exists and has no leading bytes, so the prefix is empty — an outcome, not a
            // failure (06.3.1-T1 F-C10).
            return [];
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

    /// <inheritdoc />
    public async Task<byte[]?> GetAsync(string objectKey, long maxBytes, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateObjectKey(objectKey, nameof(objectKey));

        if (maxBytes is < 1 or > MaxObjectBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBytes), maxBytes, "maxBytes must be between 1 and 268435456");
        }

        var options = _options.Value;
        try
        {
            // One byte past the caller's cap is requested, so an object the caller's policy no longer admits is
            // refused below instead of being silently truncated: a truncated report is a different document.
            using var response = await _client.Value.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = options.Bucket,
                    Key = objectKey,
                    ByteRange = new ByteRange(0, maxBytes),
                },
                cancellationToken).ConfigureAwait(false);

            var bytes = await ReadBoundedAsync(
                response.ResponseStream,
                checked((int)maxBytes + 1),
                response.ContentLength,
                cancellationToken).ConfigureAwait(false);

            if (bytes.LongLength > maxBytes)
            {
                // Never a truncated success (IObjectStorage.GetAsync): the message names the cap, never the key.
                throw new InvalidOperationException(
                    $"the stored object is larger than the {maxBytes}-byte cap the caller stated");
            }

            return bytes;
        }
        catch (AmazonS3Exception exception) when (IsAbsent(exception))
        {
            // A store 404 is an outcome, not an exception (IObjectStorage.GetAsync).
            return null;
        }
        catch (AmazonS3Exception exception) when (IsEmptyRange(exception))
        {
            // A zero-byte object: the range cannot be satisfied because there is no first byte (06.3.1-T1 F-C10).
            return [];
        }
        catch (AmazonServiceException exception)
        {
            // SDK v4: AmazonServiceException no longer derives from AmazonClientException, so a store service
            // error would escape the client clause below with the SDK's message intact.
            throw StorageFailure("read", exception);
        }
        catch (AmazonClientException exception)
        {
            throw StorageFailure("read", exception);
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
}
