using System.Net;
using Amazon.Runtime;
using Amazon.S3;

namespace Evidgate.BuildingBlocks.ObjectStorage;

internal sealed partial class S3ObjectStorage
{
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
    /// True when the store refused the requested byte range as unsatisfiable: a ranged <c>GET</c> of a
    /// zero-byte object answers 416 <c>InvalidRange</c>, which is an outcome (an empty prefix), not a
    /// failure (06.3.1-T1 F-C10).
    /// </summary>
    private static bool IsEmptyRange(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
        || string.Equals(exception.ErrorCode, "InvalidRange", StringComparison.Ordinal);

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
