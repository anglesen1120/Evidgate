using Evidgate.BuildingBlocks.Observability;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Ingestion.Application;

/// <summary>
/// The single place a 04.1.1-T3 limit refusal is counted and logged (04.1.1 §5/§8, DECISIONS §8.1 D-A8).
/// Every refusal of the events route passes through <see cref="Record"/> exactly once, so the counter and
/// the log line stay in step and no rejection is counted twice.
/// <para>
/// The reason vocabulary is closed: <see cref="BodyTooLarge"/>, <see cref="EventCount"/>,
/// <see cref="ExpandedTooLarge"/>, <see cref="RatioExceeded"/>, <see cref="EncodingUnsupported"/> and
/// <see cref="GzipInvalid"/>. Neither the counter nor the log ever carries the body, a header value or any
/// other tenant-controlled text — only the echoed request id, the route's run id and byte counts.
/// </para>
/// </summary>
internal static partial class IngestBatchRejection
{
    /// <summary>The as-sent body exceeded the route's 1 MiB transport cap (00 §9.3).</summary>
    public const string BodyTooLarge = "body_too_large";

    /// <summary>The batch carried more than the parser's 500-event cap (AC7).</summary>
    public const string EventCount = "event_count";

    /// <summary>A gzip body inflated past <c>IngestionLimits.MaxExpandedBytes</c> (the bomb guard).</summary>
    public const string ExpandedTooLarge = "expanded_too_large";

    /// <summary>A gzip body inflated past <c>IngestionLimits.MaxExpansionRatio</c> × its compressed size.</summary>
    public const string RatioExceeded = "ratio_exceeded";

    /// <summary><c>Content-Encoding</c> named something other than a single <c>gzip</c> or <c>identity</c>.</summary>
    public const string EncodingUnsupported = "encoding_unsupported";

    /// <summary>The <c>Content-Encoding: gzip</c> body is not one well-formed gzip member (bad header, CRC, truncation, multi-member or trailing bytes).</summary>
    public const string GzipInvalid = "gzip_invalid";

    /// <summary>
    /// Counts and logs one refusal. <paramref name="requestId"/> is the effective <c>X-Request-Id</c>
    /// (<c>HttpContext.TraceIdentifier</c>), <paramref name="bytesIn"/> the as-sent body size and
    /// <paramref name="bytesExpanded"/> the decoded size reached when the decode aborted (0 when the body
    /// was never decoded).
    /// </summary>
    public static void Record(
        ILogger logger,
        string reason,
        string requestId,
        Guid runId,
        long bytesIn,
        int bytesExpanded)
    {
        EvidgateTelemetry.RecordIngestBatchRejected(reason);

        LogRejected(logger, requestId, runId, reason, bytesIn, bytesExpanded);
    }

    [LoggerMessage(
        EventId = 4110,
        Level = LogLevel.Warning,
        Message = "event batch rejected request_id={RequestId} run_id={RunId} reason={Reason} bytes_in={BytesIn} bytes_expanded_at_abort={BytesExpanded}")]
    private static partial void LogRejected(
        ILogger logger,
        string requestId,
        Guid runId,
        string reason,
        long bytesIn,
        int bytesExpanded);
}
