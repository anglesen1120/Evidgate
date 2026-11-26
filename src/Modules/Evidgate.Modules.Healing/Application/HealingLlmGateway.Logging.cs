using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Healing.Application;

internal sealed partial class HealingLlmGateway
{
    /// <summary>The structured record of a reservation that could not be released (the project only).</summary>
    /// <param name="exception">The release failure.</param>
    /// <param name="projectId">The project whose estimate stays counted until the period resets.</param>
    [LoggerMessage(
        EventId = 8610,
        Level = LogLevel.Error,
        Message = "Healing LLM budget reservation could not be released; it stays counted until the period resets (project={ProjectId})")]
    private partial void LogReleaseFailed(Exception exception, Guid projectId);

    /// <summary>The structured record of a settlement whose outcome is unknown; the hold stays counted.</summary>
    /// <param name="exception">The connection-level failure.</param>
    /// <param name="projectId">The project whose estimate stays counted until the period resets.</param>
    [LoggerMessage(
        EventId = 8611,
        Level = LogLevel.Error,
        Message = "Healing LLM settlement outcome unknown; the reservation is kept counted until the period resets (project={ProjectId})")]
    private partial void LogSettlementUnknown(Exception exception, Guid projectId);

    /// <summary>The structured record of a payload withheld because masking left a secret-shaped value (counts only, never values).</summary>
    /// <param name="projectId">The project whose call was not sent.</param>
    /// <param name="findings">How many rule findings the report holds.</param>
    [LoggerMessage(
        EventId = 8612,
        Level = LogLevel.Warning,
        Message = "Healing LLM payload withheld after redaction left a residual (project={ProjectId}, findings={Findings})")]
    private partial void LogLeakBlocked(Guid projectId, int findings);

    /// <summary>The masked payload of one call, computed on the first attempt and reused by a schema retry.</summary>
    private sealed class RedactedPayload
    {
        internal RedactionOutcome? Outcome { get; set; }
    }
}
