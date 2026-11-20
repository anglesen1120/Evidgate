namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;

/// <summary>
/// The end of a run whose exit code was zero: the records belong to the caller only when this run created the sink.
/// </summary>
internal sealed partial class ChildProcessParseRunner
{
    /// <summary>
    /// Turns a finished stdout stream into the run's verdict once the parent's own checks and the exit code passed.
    /// </summary>
    /// <param name="request">The run's request, whose sink decides who already holds the records.</param>
    /// <param name="sink">The sink this run wrote to, created here when the request carried none.</param>
    /// <param name="capture">What the stdout stream yielded.</param>
    /// <param name="exitCode">The child's exit code, zero by the time this is called.</param>
    /// <param name="stderrExcerpt">The bounded stderr excerpt, if a pump finished.</param>
    /// <returns>The success, or the malformed-output failure the stream ended with.</returns>
    private static ParseRunResult CompleteOutput(
        ParseRunRequest request,
        IParseRecordSink sink,
        ChildOutputCapture capture,
        int exitCode,
        string? stderrExcerpt)
    {
        // Only a list this run created is handed back: a caller that supplies any sink, including its own
        // BufferingParseRecordSink, has already taken delivery and must not also receive a second copy.
        if (capture.Failure is not null)
        {
            return new ParseRunFailed(
                ParseRunFailureKind.MalformedOutput,
                exitCode,
                capture.OutputBytes,
                capture.RecordCount,
                stderrExcerpt);
        }

        var records = request.Sink is null && sink is BufferingParseRecordSink buffering
            ? buffering.Records
            : Array.Empty<ParseRecord>();

        return new ParseRunSucceeded(records, exitCode, capture.OutputBytes, capture.RecordCount);
    }
}
