using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Evidence.Application;

internal sealed partial class ParseArtifactTaskHandler
{
    /// <summary>
    /// The one line a rejection writes: the artifact and the reason it is now carrying, the child's exit code, and the
    /// runner's bounded, redacted stderr excerpt for the operator. Never a stored value and never a tenant-visible one.
    /// </summary>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="artifactId">The artifact that was rejected.</param>
    /// <param name="reason">The <c>parse_*</c> code the row now carries.</param>
    /// <param name="exitCode">The child's exit code, or <see langword="null"/> when it never started.</param>
    /// <param name="stderrExcerpt">The runner's redacted excerpt, or <see langword="null"/> when the child wrote nothing.</param>
    [LoggerMessage(
        EventId = 2440,
        Level = LogLevel.Warning,
        Message = "parse rejected artifact {ArtifactId} as {Reason} (child exit {ExitCode}); stderr: {StderrExcerpt}")]
    private static partial void LogRejected(
        ILogger logger,
        Guid artifactId,
        string reason,
        int? exitCode,
        string? stderrExcerpt);

    /// <summary>
    /// A payload naming an artifact this tenant cannot see: the attempt changed nothing, but a misfiled or crafted
    /// payload is worth a Warning rather than a silent completion.
    /// </summary>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="artifactId">The artifact the payload named.</param>
    [LoggerMessage(
        EventId = 2441,
        Level = LogLevel.Warning,
        Message = "parse found no artifact {ArtifactId} in this tenant: nothing parsed")]
    private static partial void LogArtifactNotFound(ILogger logger, Guid artifactId);

    /// <summary>
    /// The routine no-op: the artifact already holds an outcome (or is not parsable yet), so the child was never
    /// started and no statement was issued.
    /// </summary>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="artifactId">The artifact the payload named.</param>
    /// <param name="state">The state it holds, or <see langword="null"/> while the column default applies.</param>
    [LoggerMessage(
        EventId = 2442,
        Level = LogLevel.Debug,
        Message = "parse skipped artifact {ArtifactId} in state {State}: nothing parsed")]
    private static partial void LogAlreadyDecided(ILogger logger, Guid artifactId, string? state);

    /// <summary>
    /// The guarded <c>UPDATE</c> matched no row and the re-read found a stored rejection: the artifact was decided
    /// once — by this attempt's earlier write, a replay, or a concurrent attempt — so nothing was added.
    /// </summary>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="artifactId">The artifact the payload named.</param>
    /// <param name="reason">The reason this attempt derived; the stored row carries the verdict that stands.</param>
    [LoggerMessage(
        EventId = 2444,
        Level = LogLevel.Debug,
        Message = "parse verdict for artifact {ArtifactId} was already written as {Reason}: this attempt changed nothing")]
    private static partial void LogVerdictAlreadyWritten(ILogger logger, Guid artifactId, string reason);

    /// <summary>
    /// The verdict the child reported itself, on a run that exited 0: an operator wants to know that the rejected
    /// document was content, not a crash, so this line names the reason and the exit code and never a record's other
    /// text.
    /// </summary>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="artifactId">The artifact whose stream held the rejection.</param>
    /// <param name="reason">The failure code the record's reason became.</param>
    /// <param name="exitCode">The child's exit code, which is 0 on this shape.</param>
    [LoggerMessage(
        EventId = 2445,
        Level = LogLevel.Warning,
        Message = "parse rejected artifact {ArtifactId} as {Reason}: the child reported the rejection itself (exit {ExitCode})")]
    private static partial void LogChildRejected(
        ILogger logger,
        Guid artifactId,
        string reason,
        int? exitCode);

    /// <summary>
    /// The stored object is larger than the kind's cap, so it was never read and no child ran: the one rejection that
    /// happens before the runner.
    /// </summary>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="artifactId">The artifact whose object is over the cap.</param>
    /// <param name="maxBytes">The cap the kind policy resolved to for this artifact.</param>
    [LoggerMessage(
        EventId = 2446,
        Level = LogLevel.Warning,
        Message = "parse rejected artifact {ArtifactId} as too_large: the stored object is larger than the kind's {MaxBytes}-byte cap, so it was not read and no child started")]
    private static partial void LogOversizeRejected(ILogger logger, Guid artifactId, long maxBytes);

    /// <summary>
    /// A <c>raw_report</c> artifact whose media type has no profile arm: nothing is parsed, exactly as the in-process
    /// path treats the same bytes.
    /// </summary>
    /// <param name="logger">The handler's logger.</param>
    /// <param name="artifactId">The artifact the payload named.</param>
    /// <param name="kind">Its kind.</param>
    /// <param name="mediaType">Its declared media type.</param>
    [LoggerMessage(
        EventId = 2443,
        Level = LogLevel.Debug,
        Message = "parse skipped artifact {ArtifactId}: kind {Kind} and media type {MediaType} name no parse arm")]
    private static partial void LogNothingToParse(ILogger logger, Guid artifactId, string kind, string mediaType);
}
