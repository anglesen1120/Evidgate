namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;

/// <summary>
/// The one projection from a failed child run to the <c>artifact.failure_code</c> a tenant sees (06.3.6 AC3,
/// 00 §9.2, contracts note O-06.3.6-1). It is a pure function of <see cref="ParseRunFailed"/> so the three
/// vocabulary words exist in exactly one place: the runner never says <c>parse_*</c>, and the <c>parse</c> task
/// handler never decides a reason itself.
/// <para>
/// <b>Not a verdict: the infrastructure failure.</b> <see cref="ParseRunFailureKind.SpawnFailed"/> means no child
/// existed to judge the artifact, so it has no tenant-visible reason at all — the caller throws and the queue
/// retries with backoff (06.3.6 §6). <see cref="TryMap"/> answers <see langword="false"/> for it and writes no
/// reason, so a caller cannot accidentally persist a limit verdict for a host that never ran.
/// </para>
/// <para>
/// <b>Exit codes (measured in a Linux arm64 container, .NET 10; a signal elsewhere is 128 + signal).</b>
/// The child's own contract is 0 streamed, 2 usage, 3 internal/EPIPE and 4 heap limit; a signal death arrives
/// as <c>128 + signal</c>. The mapping reads:
/// </para>
/// <list type="bullet">
/// <item>4 — the child caught its own <see cref="OutOfMemoryException"/> and exited without allocating again, so
/// the heap limit is <see cref="Oom"/>. An uncaught escape from that path is a <c>SIGABRT</c> (134) and stays
/// <see cref="Crashed"/>: a death the child could not report is never read as a limit that was applied.</item>
/// <item>152 — <c>128 + SIGXCPU</c>: the CPU limit, <see cref="Timeout"/>.</item>
/// <item>137 — <c>128 + SIGKILL</c>, <see cref="Crashed"/>, because it is not attributable. A kernel OOM kill
/// produces it, and so does <c>RLIMIT_CPU</c> once its hard value is reached (<c>ResourceLimits</c> sets the CPU
/// soft value one second below the hard one, so the limit itself arrives as <c>152</c> first; 137 remains for the
/// escalation and for a foreign kill). A parent cannot read the
/// child's CPU time after it exited (<c>Process.TotalProcessorTime</c> throws
/// <see cref="InvalidOperationException"/> on Linux once the child is reaped — measured), so the runner has no
/// evidence that would separate the two. The wall clock is not affected: the runner records its own expiry as
/// <see cref="ParseRunFailureKind.WallClockExceeded"/> before the exit code is consulted, and every other kill it
/// issues is likewise attributed before this point.</item>
/// <item>3 — the internal-error and closed-stdout (EPIPE) exit, <see cref="Crashed"/>.</item>
/// <item>anything else, and a run with no exit code at all — <see cref="Crashed"/>.</item>
/// </list>
/// <para>
/// <b>The failure kinds.</b> A parent-side cap breach (<see cref="ParseRunFailureKind.OutputBytesExceeded"/>,
/// <see cref="ParseRunFailureKind.OutputRecordsExceeded"/>) is the child's own output, so it is
/// <see cref="Crashed"/>; the wall clock is <see cref="Timeout"/>; a stream that is not the protocol
/// (<see cref="ParseRunFailureKind.MalformedOutput"/>) is <see cref="Crashed"/>, which covers a missing,
/// duplicated, non-last or miscounted marker.
/// </para>
/// </summary>
internal static class ParseRunRejection
{
    /// <summary>The reason for a child that died of its heap limit (AC3: exit by memory limit).</summary>
    public const string Oom = "parse_oom";

    /// <summary>The reason for a child that exceeded its wall clock or its CPU limit (AC3).</summary>
    public const string Timeout = "parse_timeout";

    /// <summary>
    /// The reason for every other failed run: a non-zero or signal exit, output over a cap, or a stdout stream
    /// that is not the NDJSON protocol (AC3).
    /// </summary>
    public const string Crashed = "parse_crashed";

    /// <summary>
    /// The child's heap-limit exit (<c>Program.ExitMemoryLimit</c>): the one non-zero code that carries a limit
    /// verdict rather than a crash.
    /// </summary>
    private const int ExitMemoryLimit = 4;

    /// <summary><c>128 + SIGXCPU</c> (24): the CPU limit, which the child's soft-below-hard rlimit raises before it escalates.</summary>
    private const int ExitCpuSignal = 128 + 24;

    /// <summary>
    /// Maps one failed run to the tenant-visible reason, or reports that the failure is not a verdict at all.
    /// </summary>
    /// <param name="failed">The failed run; its kind and exit code are the only inputs.</param>
    /// <param name="reason">
    /// The <c>artifact.failure_code</c> value, or an empty string when the answer is <see langword="false"/>: a
    /// spawn failure must not leave a limit reason behind for a caller that ignores the return value.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="reason"/> is the artifact's reason;
    /// <see langword="false"/> for <see cref="ParseRunFailureKind.SpawnFailed"/>, which the caller retries.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="failed"/> is <see langword="null"/>.</exception>
    public static bool TryMap(ParseRunFailed failed, out string reason)
    {
        ArgumentNullException.ThrowIfNull(failed);

        reason = string.Empty;

        switch (failed.Failure)
        {
            case ParseRunFailureKind.SpawnFailed:
                // No child, so no verdict: the queue retries instead of rejecting the artifact.
                return false;

            case ParseRunFailureKind.WallClockExceeded:
                reason = Timeout;
                return true;

            case ParseRunFailureKind.ExitedAbnormally:
                reason = failed.ChildExitCode switch
                {
                    ExitMemoryLimit => Oom,
                    ExitCpuSignal => Timeout,
                    _ => Crashed,
                };

                return true;

            case ParseRunFailureKind.OutputBytesExceeded:
            case ParseRunFailureKind.OutputRecordsExceeded:
            case ParseRunFailureKind.MalformedOutput:
            default:
                reason = Crashed;
                return true;
        }
    }
}
