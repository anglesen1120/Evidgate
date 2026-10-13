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

    /// <summary>The most entries one archive may hold (06.3.1 AC3); more is <c>entry_count_exceeded</c>.</summary>
    public const int MaxZipEntries = 10_000;

    /// <summary>
    /// The most expanded bytes the profile counts across one archive, its nested archives included (06.3.1
    /// AC3). Counted from the bytes actually read, never from an entry's declared length, so a lying header
    /// cannot slip past it; exceeding it is the 413 <c>size_exceeded</c>.
    /// </summary>
    public const long MaxExpandedBytes = 512L * 1024 * 1024;

    /// <summary>
    /// The most a single entry may expand against its compressed bytes (06.3.1 AC3): a 100:1 entry is a
    /// decompression bomb; exceeding it is <c>compression_ratio_exceeded</c>.
    /// </summary>
    public const int MaxCompressionRatio = 100;

    /// <summary>
    /// The deepest a zip may nest another zip (06.3.1 AC3): a zip-in-zip is level 2, a zip inside that is
    /// refused with <c>nesting_exceeded</c>.
    /// </summary>
    public const int MaxNesting = 2;

    /// <summary>
    /// The longest one entry name may be, in <see cref="char"/>s (06.3.1 AC4). The bound is what keeps name
    /// normalization linear: without it a hostile central directory could hand the walk a single multi-megabyte
    /// name, and every separator in it would cost a substring.
    /// </summary>
    public const int MaxEntryNameLength = 1024;

    /// <summary>
    /// The most path segments one entry name may hold (06.3.1 AC4); together with
    /// <see cref="MaxEntryNameLength"/> it bounds the ancestor walk one name can ask for.
    /// </summary>
    public const int MaxEntryNameSegments = 64;
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
    /// <summary>The failure for one reason: 422 <c>artifact_invalid</c>, or 413 <c>payload_too_large</c> for the size cap.</summary>
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
        ParsingFailureReason.UnsafeEntryName =>
            "the archive contains an entry whose name is not safe to extract",
        ParsingFailureReason.DuplicateEntry =>
            "the archive contains two entries that resolve to the same path",
        ParsingFailureReason.EntryCountExceeded =>
            "the archive contains more entries than the server-side parser profile allows",
        ParsingFailureReason.CompressionRatioExceeded =>
            "an archive entry expands by more than the server-side parser profile allows",
        ParsingFailureReason.NestingExceeded =>
            "the archive nests archives deeper than the server-side parser profile allows",
        _ =>
            "the document is not well-formed",
    };
}
