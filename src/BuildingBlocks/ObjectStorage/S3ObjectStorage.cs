using System.Globalization;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Endpoints;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Evidgate.BuildingBlocks.ObjectStorage;

/// <summary>
/// The <c>AWSSDK.S3</c> implementation of <see cref="IObjectStorage"/> (06.1.2-T1).
/// Presigning is SigV4 arithmetic over the request — no network call — so the
/// adapter is safe to call from a request handler. The S3 client is created once,
/// lazily and thread-safely, and disposed with the adapter.
/// </summary>
/// <remarks>
/// The signed headers are exactly the ones the 01.8.1-T1 parity suite proved on
/// SeaweedFS and the provider (T18): <c>Content-Length</c> and
/// <c>x-amz-checksum-sha256</c> (base64 of the raw digest). No URL, key, checksum
/// or credential ever reaches a log line or an exception message (00 §5.7 S3).
/// </remarks>
internal sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    /// <summary>06.1.2 §3: staging keys are minted under this prefix (T18).</summary>
    internal const string PendingPrefix = "pending/";

    /// <summary>The provider-neutral checksum header the presigned PUT binds (T18).</summary>
    internal const string ChecksumHeaderName = "x-amz-checksum-sha256";

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

        ValidateObjectKey(request.ObjectKey);
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
    /// A staging key is a plain <c>pending/&lt;guid&gt;/&lt;guid&gt;</c> string: it must
    /// start with <c>pending/</c> and carry no traversal, empty segment, backslash
    /// or control character, so a crafted key cannot escape the staging prefix.
    /// </summary>
    private static void ValidateObjectKey(string objectKey)
    {
        if (string.IsNullOrEmpty(objectKey)
            || !objectKey.StartsWith(PendingPrefix, StringComparison.Ordinal)
            || objectKey.Contains("..", StringComparison.Ordinal)
            || objectKey.Contains("//", StringComparison.Ordinal)
            || objectKey.Contains('\\')
            || objectKey.Any(char.IsControl))
        {
            throw new ArgumentException(
                "ObjectKey must be a plain pending/ staging key", nameof(objectKey));
        }
    }

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
