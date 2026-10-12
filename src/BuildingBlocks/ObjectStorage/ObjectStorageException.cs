using System.Net;

namespace Evidgate.BuildingBlocks.ObjectStorage;

/// <summary>
/// A storage call failed (06.1.2-T2). The message carries only the operation and the non-secret S3
/// error code/HTTP status: the key, the presigned URL, the checksum and the credentials never appear,
/// and the cause is never chained because the SDK message and its response body may carry the key and
/// the request URL (00 §5.7 S3). The structured <see cref="Operation"/>, <see cref="S3ErrorCode"/> and
/// <see cref="HttpStatus"/> members keep the diagnosis available without the secrets.
/// </summary>
public sealed class ObjectStorageException : Exception
{
    /// <summary>Creates the failure from the operation that failed and the store's non-secret answer.</summary>
    /// <param name="operation">The adapter operation, e.g. <c>head</c>, <c>copy</c> or <c>delete</c>.</param>
    /// <param name="s3ErrorCode">The S3 error code the store reported, or <c>null</c> when it reported none.</param>
    /// <param name="httpStatus">The HTTP status the store answered with, or <c>null</c> for a transport failure.</param>
    public ObjectStorageException(string operation, string? s3ErrorCode, HttpStatusCode? httpStatus)
        : base(Describe(operation, s3ErrorCode, httpStatus))
    {
        Operation = operation;
        S3ErrorCode = s3ErrorCode;
        HttpStatus = httpStatus;
    }

    /// <summary>The adapter operation that failed: <c>head</c>, <c>copy</c> or <c>delete</c>.</summary>
    public string Operation { get; }

    /// <summary>The S3 error code the store reported, e.g. <c>NoSuchKey</c>, or <c>null</c>.</summary>
    public string? S3ErrorCode { get; }

    /// <summary>The HTTP status the store answered with, or <c>null</c> when the request never reached it.</summary>
    public HttpStatusCode? HttpStatus { get; }

    private static string Describe(string operation, string? s3ErrorCode, HttpStatusCode? httpStatus) =>
        $"The object store '{operation}' request failed: "
        + (httpStatus is null ? "the store could not be reached" : $"HTTP {(int)httpStatus.Value}")
        + (s3ErrorCode is null ? "." : $" ({s3ErrorCode}).");
}
