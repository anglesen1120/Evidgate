using System.Globalization;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace ObjectStorage.IntegrationTests;

/// <summary>
/// Endpoint-agnostic S3 target for the 01.8.1-T1 / T18 presigned-URL parity
/// assertions. Nothing in this type knows about SeaweedFS: the local fixture
/// points it at its container endpoint, and 10.6.1-T2 points the same assertions
/// at the real provider through <c>EVIDGATE_S3PARITY__*</c>.
/// </summary>
internal sealed class S3ParityTarget
{
    /// <summary>
    /// Checksum header the presigned PUT binds. 06.1.2 §3 assumes the
    /// provider-neutral <c>x-amz-checksum-sha256</c> and defers the confirmation
    /// to this story's parity check.
    /// </summary>
    public const string ChecksumHeaderName = "x-amz-checksum-sha256";

    /// <summary>06.1.2 §3: staging keys are minted under this prefix (T18).</summary>
    public const string PendingPrefix = "pending/";

    private const string ContentLengthHeaderName = "Content-Length";
    private const string DefaultRegion = "us-east-1";

    /// <summary>S3 service endpoint, e.g. <c>http://127.0.0.1:8333</c>.</summary>
    public required string ServiceUrl { get; init; }

    /// <summary>SigV4 signing region; the store must accept it in the credential scope.</summary>
    public required string Region { get; init; }

    public required string AccessKey { get; init; }

    public required string SecretKey { get; init; }

    public required string Bucket { get; init; }

    /// <summary>Path-style addressing; every S3-compatible emulator needs it.</summary>
    public bool ForcePathStyle { get; init; } = true;

    /// <summary>
    /// Builds the target from <c>EVIDGATE_S3PARITY__ENDPOINT</c>,
    /// <c>__REGION</c>, <c>__ACCESSKEY</c>, <c>__SECRETKEY</c>, <c>__BUCKET</c>
    /// and the optional <c>__FORCEPATHSTYLE</c> — the configuration 10.6.1-T2
    /// uses for the provider smoke test.
    /// </summary>
    public static S3ParityTarget FromEnvironment()
    {
        var forcePathStyle = Environment.GetEnvironmentVariable("EVIDGATE_S3PARITY__FORCEPATHSTYLE");
        return new S3ParityTarget
        {
            ServiceUrl = Required("EVIDGATE_S3PARITY__ENDPOINT"),
            Region = Optional("EVIDGATE_S3PARITY__REGION") ?? DefaultRegion,
            AccessKey = Required("EVIDGATE_S3PARITY__ACCESSKEY"),
            SecretKey = Required("EVIDGATE_S3PARITY__SECRETKEY"),
            Bucket = Required("EVIDGATE_S3PARITY__BUCKET"),
            ForcePathStyle = forcePathStyle is null || !bool.TryParse(forcePathStyle, out var parsed) || parsed,
        };

        static string Required(string name) =>
            Optional(name) ?? throw new InvalidOperationException(
                $"Environment variable {name} is required to run the S3 parity assertions against a non-container endpoint.");

        static string? Optional(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
    }

    /// <summary>
    /// The scheme the presigned URL must carry: the SDK defaults to HTTPS, but a
    /// local emulator endpoint is plain HTTP (a real provider endpoint stays HTTPS).
    /// </summary>
    private Protocol PresignProtocol =>
        ServiceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTPS : Protocol.HTTP;

    /// <summary>Signed-SDK client used for bucket setup and for evidence reads (HEAD/GET).</summary>
    public AmazonS3Client CreateClient() =>
        new(
            new BasicAWSCredentials(AccessKey, SecretKey),
            new AmazonS3Config
            {
                ServiceURL = ServiceUrl,
                ForcePathStyle = ForcePathStyle,
                AuthenticationRegion = Region,
            });

    /// <summary>
    /// Presigns a PUT that binds both the declared byte count and the SHA-256
    /// checksum: <c>Content-Length</c> and <see cref="ChecksumHeaderName"/> are
    /// part of the SigV4 signature, so a client cannot upload a different
    /// length or different bytes without invalidating the signature (T18).
    /// </summary>
    public PresignedRequest PresignPut(string key, long contentLength, string checksumSha256Base64, TimeSpan expiresIn)
    {
        using var client = CreateClient();
        var request = new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.Add(expiresIn),
            Protocol = PresignProtocol,
        };
        var contentLengthValue = contentLength.ToString(CultureInfo.InvariantCulture);
        request.Headers[ContentLengthHeaderName] = contentLengthValue;
        request.Headers[ChecksumHeaderName] = checksumSha256Base64;
        var url = client.GetPreSignedURL(request);
        return PresignedRequest.Parse(
            url,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ContentLengthHeaderName] = contentLengthValue,
                [ChecksumHeaderName] = checksumSha256Base64,
            });
    }

    /// <summary>Presigns a GET for the same object — the download half of the round trip.</summary>
    public PresignedRequest PresignGet(string key, TimeSpan expiresIn)
    {
        using var client = CreateClient();
        var request = new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expiresIn),
            Protocol = PresignProtocol,
        };
        return PresignedRequest.Parse(client.GetPreSignedURL(request));
    }
}

/// <summary>
/// A presigned URL plus the headers the holder must send verbatim: every signed
/// header that the presigner did not hoist into the query string (a real client
/// sends <c>required_headers</c> from the upload-url response the same way).
/// </summary>
internal sealed record PresignedRequest(
    Uri Url,
    IReadOnlyList<string> SignedHeaders,
    IReadOnlyDictionary<string, string> RequiredHeaders,
    IReadOnlyDictionary<string, string> Query)
{
    public const string HostHeaderName = "host";

    /// <summary>
    /// True when the presigned URL binds <paramref name="name"/> — either as a
    /// signed request header (whose value the holder must send back) or as a
    /// signed query parameter. Either form is part of the SigV4 signature.
    /// </summary>
    public bool Binds(string name) =>
        SignedHeaders.Contains(name, StringComparer.OrdinalIgnoreCase) || Query.ContainsKey(name);

    public static PresignedRequest Parse(string url) =>
        Parse(url, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Splits the signed header list into the ones sent as request headers
    /// (values taken from <paramref name="signedValues"/>, using the value that
    /// was signed) and the ones the presigner hoisted into the query string.
    /// </summary>
    public static PresignedRequest Parse(string url, IReadOnlyDictionary<string, string> signedValues)
    {
        var uri = new Uri(url, UriKind.Absolute);
        var query = ParseQuery(uri.Query);
        var signedHeaders = (query.TryGetValue("X-Amz-SignedHeaders", out var signed) ? signed : string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var required = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in signedHeaders)
        {
            if (string.Equals(header, HostHeaderName, StringComparison.OrdinalIgnoreCase)
                || query.ContainsKey(header))
            {
                // Host is implied by the URL; hoisted values already ride in the query.
                continue;
            }

            if (signedValues.TryGetValue(header, out var value))
            {
                required[header] = value;
            }
        }

        return new PresignedRequest(uri, signedHeaders, required, query);
    }

    private static Dictionary<string, string> ParseQuery(string rawQuery)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in rawQuery.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var name = WebUtility.UrlDecode(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? string.Empty : WebUtility.UrlDecode(pair[(separator + 1)..]);
            result[name] = value;
        }

        return result;
    }
}
