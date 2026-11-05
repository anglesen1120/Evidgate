namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;

/// <summary>
/// The profile arm one child run applies (06.3.6 §5). The parent carries its own copy of the three arm names
/// instead of reusing <c>Evidgate.Parser</c>'s <c>ParseProfile</c>: the child is launched as a process with no
/// project reference either way (06.3.6-T1.2 Notes, §10 T1.3), so a shared enum would be a compile-time tie for
/// nothing but the three strings below.
/// </summary>
internal enum ParseRunnerProfile
{
    /// <summary>One XML report, validated by the 06.3.1 profile, streamed as start/text/end records.</summary>
    Xml,

    /// <summary>One JSON payload, parsed by the 06.3.1 profile, emitted as one document record.</summary>
    Json,

    /// <summary>One zip archive, walked by the 06.3.1 profile, emitted as one record per entry.</summary>
    Archive,
}

/// <summary>
/// One parse request: the bytes to hand the child, the profile arm and the per-kind caps that travel as the
/// child's arguments (06.3.6 §5, §7). The caller keeps ownership of <see cref="Input"/>; the runner reads it once,
/// front to back, and never seeks.
/// </summary>
internal sealed class ParseRunRequest
{
    /// <summary>Creates a request.</summary>
    /// <param name="profile">Which arm of the profile the child runs.</param>
    /// <param name="maxBytes">The per-kind byte cap from <c>artifact_kind_policy.max_bytes</c> (positive).</param>
    /// <param name="mediaType">The declared media type, or <see langword="null"/> when the caller already sniffed it.</param>
    /// <param name="input">The artifact's bytes; the caller keeps ownership.</param>
    /// <param name="maxCases">The most report entries the child may emit (positive).</param>
    public ParseRunRequest(
        ParseRunnerProfile profile,
        long maxBytes,
        string? mediaType,
        Stream input,
        long maxCases = ParsingService.DefaultMaxReportCases)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCases);
        ArgumentNullException.ThrowIfNull(input);

        Profile = profile;
        MaxBytes = maxBytes;
        MaxCases = maxCases;
        MediaType = mediaType;
        Input = input;
    }

    /// <summary>Which arm of the profile the child runs.</summary>
    public ParseRunnerProfile Profile { get; }

    /// <summary>The per-kind byte cap, handed to the child as <c>--max-bytes</c>.</summary>
    public long MaxBytes { get; }

    /// <summary>The report entry cap, handed to the child as <c>--max-cases</c>.</summary>
    public long MaxCases { get; }

    /// <summary>The declared media type, or <see langword="null"/> when the caller already sniffed it.</summary>
    public string? MediaType { get; }

    /// <summary>The artifact's bytes; the caller keeps ownership and the runner never disposes it.</summary>
    public Stream Input { get; }
}

/// <summary>
/// The parent side of 06.3.6 §7: runs the 06.3.1 profile in the <c>Evidgate.Parser</c> child process and returns
/// either the buffered record stream or the one failure kind that ended the run.
/// <para>
/// It is registered only when <c>EVIDGATE__PARSE__OUTOFPROCESS</c> is on (06.3.6 §11 rollout); the
/// <c>artifact_parse</c> task handler (06.3.6-T1.4) is the one caller and the only place a run becomes a
/// <c>rejected</c> artifact — a failed run, or a successful one whose <c>rejected</c> record is the verdict
/// (D-S125-Q4), or an object the kind cap refuses. The interface itself carries no <c>parse_*</c> vocabulary:
/// the projection from <see cref="ParseRunFailureKind"/> to a reason is <see cref="ParseRunRejection.TryMap"/>, so a
/// mapping decision cannot be made twice.
/// </para>
/// </summary>
internal interface IParseRunner
{
    /// <summary>Runs one parse in the child process, bounded by every limit the runner's options state.</summary>
    /// <param name="request">The profile arm, the caps and the input stream.</param>
    /// <param name="cancellationToken">
    /// The caller's cancellation. A cancelled run tree-kills the child, removes its temp directory and rethrows
    /// <see cref="OperationCanceledException"/>: a caller cancellation is not a parse verdict. The runner's own
    /// disposal rethrows the same exception for the same reason, so a shutdown never becomes a verdict either
    /// (06.3.6-T1.4, S125 F3).
    /// </param>
    /// <returns>The buffered records on success, otherwise the failure kind with the child's exit code.</returns>
    Task<ParseRunResult> RunAsync(ParseRunRequest request, CancellationToken cancellationToken);
}
