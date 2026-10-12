using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;
using Microsoft.AspNetCore.Http;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing;

/// <summary>
/// The parser profile's fixed caps (06.3.1 §4, 00 §5.7 S5). A cap that is per-kind (<c>max_bytes</c>,
/// <c>media_types</c>) travels in from <c>artifact_kind_policy</c>; only the caps that are the same for every
/// kind live here, so a regression in any guard is one constant to read and one test to fail.
/// </summary>
internal static class ParsingLimits
{
    /// <summary>The maximum element/array nesting the profile accepts (06.3.1 AC2); deeper input is rejected.</summary>
    public const int MaxDepth = 64;

    /// <summary>How long one parse may take before it is abandoned with <c>timeout</c> (06.3.1 AC2).</summary>
    public static readonly TimeSpan ParseTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The bounded prefix the media sniff reads (06.3.1-T1.2, 00 §5.7 P1): 4 KiB is enough for every magic
    /// byte the profile knows, so the <c>complete</c> hot path never pulls a whole object.
    /// </summary>
    public const int SniffPrefixBytes = 4096;
}

/// <summary>
/// The one place a profile failure becomes a 00 §9.2 <see cref="Error"/> (C8): the code from
/// <see cref="ParsingErrorCode"/>, a fixed secret-free message and the reason in <c>details</c>. It lives beside
/// the limits it reports and is shared by <c>SafeXml</c> and <c>SafeJson</c>, so the two profiles cannot drift
/// on codes, statuses or wording. No message or detail ever carries an artifact byte, element name or path
/// (00 §5.7 S3).
/// </summary>
internal static class ParsingFailure
{
    /// <summary>The failure for one reason: 422 <c>artifact_invalid</c>, or 413 <c>too_large</c> for the size cap.</summary>
    /// <param name="reason">One of <see cref="ParsingFailureReason"/>.</param>
    /// <returns>The 00 §9.2 failure the endpoint filter renders.</returns>
    public static Error For(string reason) =>
        string.Equals(reason, ParsingFailureReason.SizeExceeded, StringComparison.Ordinal)
            ? new Error(
                ParsingErrorCode.PayloadTooLarge,
                Message(reason),
                StatusCodes.Status413PayloadTooLarge,
                Retryable: false,
                Details: [new ErrorDetail("content", reason)])
            : Error.Unprocessable(
                ApiErrorCode.ArtifactInvalid,
                Message(reason),
                [new ErrorDetail("content", reason)]);

    private static string Message(string reason) => reason switch
    {
        ParsingFailureReason.DtdProhibited =>
            "the document declares a DTD, which the server-side parser profile prohibits",
        ParsingFailureReason.DepthExceeded =>
            "the document nests deeper than the server-side parser profile allows",
        ParsingFailureReason.SizeExceeded =>
            "the content is larger than the artifact kind's byte limit",
        ParsingFailureReason.Timeout =>
            "the document took longer to parse than the server-side parser profile allows",
        _ =>
            "the document is not well-formed",
    };
}
