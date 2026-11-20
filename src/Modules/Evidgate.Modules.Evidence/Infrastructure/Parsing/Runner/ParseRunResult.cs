namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;

/// <summary>
/// Why a child run ended without a usable record stream (06.3.6 AC2, AC8). The set is closed on purpose:
/// <see cref="ParseRunRejection.TryMap"/> projects each member onto exactly one <c>rejected</c> reason, so adding a
/// member here is a mapping decision that must be made there too.
/// </summary>
internal enum ParseRunFailureKind
{
    /// <summary>The child process could not be started at all (missing host or child assembly, OS refusal).</summary>
    SpawnFailed,

    /// <summary>The wall-clock limit expired; the child tree was killed.</summary>
    WallClockExceeded,

    /// <summary>The child wrote more stdout than the byte cap allows (a single oversized record line counts).</summary>
    OutputBytesExceeded,

    /// <summary>The child wrote more record lines than the record cap allows.</summary>
    OutputRecordsExceeded,

    /// <summary>
    /// The stdout stream was not the protocol: a blank or non-object line, a marker that is missing, duplicated,
    /// not last, miscounted or not <c>true</c>, a record after the marker, or a line without its final newline.
    /// </summary>
    MalformedOutput,

    /// <summary>
    /// The child exited non-zero. On Unix a signal death is reported as 128 + the signal number, so a killed child
    /// (<c>SIGKILL</c>) is 137. A valid marker does not overrule this: the exit code is the contract.
    /// </summary>
    ExitedAbnormally,
}

/// <summary>
/// One record of the child's NDJSON stream: the raw UTF-8 line without its terminating <c>\n</c>. The bytes are
/// kept, not a decoded string, so buffering the stream costs one copy of what the child wrote and not a second
/// UTF-16 copy (<see cref="ParseRunnerOptions"/> bounds that copy in bytes and records). Parsing a record is the
/// caller's step; the runner never interprets a record's content beyond the terminal marker.
/// </summary>
/// <param name="Utf8Json">The record's JSON object, exactly as the child wrote it.</param>
internal readonly record struct ParseRecord(ReadOnlyMemory<byte> Utf8Json);

/// <summary>
/// The outcome of one child run. Two shapes only: <see cref="ParseRunSucceeded"/> carries the records the child
/// streamed before its marker, <see cref="ParseRunFailed"/> carries the one failure kind and the child's exit code
/// (<see langword="null"/> when the child never started).
/// <para>
/// The child's stderr travels one way only. The runner drains that pipe without a bound so a flooding child cannot
/// block, keeps at most 4 KiB of it in memory, and exposes it as <see cref="ParseRunFailed.StderrExcerpt"/>: a
/// single-line, bounded, secret-masked excerpt for an operator log line, never on a success result, never stored
/// and never a tenant-visible value. The raw bytes reach no member of this type.
/// </para>
/// </summary>
/// <param name="ExitCode">The child's exit code, or <see langword="null"/> when no process existed.</param>
/// <param name="OutputBytes">How many stdout bytes the child wrote before the run ended.</param>
/// <param name="OutputRecords">How many record lines the runner accepted (the marker is not a record).</param>
internal abstract record ParseRunResult(int? ExitCode, long OutputBytes, int OutputRecords);

/// <summary>A run that streamed a complete, well-framed record set and exited 0.</summary>
/// <param name="Records">
/// Every record before the marker, in stream order, when the request named no sink; empty when the caller supplied
/// its own sink, because the records were delivered there instead.
/// </param>
/// <param name="ChildExitCode">The child's exit code, always 0 on this shape.</param>
/// <param name="OutputBytes">How many stdout bytes the child wrote, marker included.</param>
/// <param name="OutputRecords">How many record lines were accepted; the marker is not counted.</param>
internal sealed record ParseRunSucceeded(
    IReadOnlyList<ParseRecord> Records,
    int ChildExitCode,
    long OutputBytes,
    int OutputRecords)
    : ParseRunResult(ChildExitCode, OutputBytes, OutputRecords);

/// <summary>A run that ended without a usable record stream.</summary>
/// <param name="Failure">Which failure ended the run.</param>
/// <param name="ChildExitCode">The child's exit code, or <see langword="null"/> when it never started.</param>
/// <param name="OutputBytes">How many stdout bytes the child wrote before the run ended.</param>
/// <param name="OutputRecords">How many complete record lines the runner had accepted by then.</param>
/// <param name="StderrExcerpt">
/// The child's stderr for an operator log line, or <see langword="null"/> when it wrote nothing, when the run never
/// spawned a child, or when its stderr pipe was abandoned after the teardown grace. Already bounded to 512
/// characters, reduced to one printable line and passed through <c>SecretRedactor</c>, so a caller may log it but
/// must never store it, return it to a tenant or add it to a reason: the tenant-visible value is the
/// <c>parse_*</c> code that <see cref="ParseRunRejection"/> maps (<c>00 §5.7 S3</c>, 06.3.6 §8).
/// </param>
internal sealed record ParseRunFailed(
    ParseRunFailureKind Failure,
    int? ChildExitCode,
    long OutputBytes,
    int OutputRecords,
    string? StderrExcerpt = null)
    : ParseRunResult(ChildExitCode, OutputBytes, OutputRecords);
