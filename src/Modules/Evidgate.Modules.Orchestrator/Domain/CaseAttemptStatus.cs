namespace Evidgate.Modules.Orchestrator.Domain;

/// <summary>
/// The server-side normalisation of one case attempt's raw status (00 §8.3, ruling T3; feature plan 05.3.3 §4/§6).
/// The raw value is stored as sent in <c>case_attempt.raw_status</c> and is never overwritten; the normalised value
/// goes to <c>case_attempt.normalized_status</c>, one of the six closed values below. Migration 0032's CHECK
/// constraint carries the same vocabulary, so a value this type would never produce cannot reach the column
/// (00 §5.5 C6).
/// <para>
/// The mapping is the exact, case-sensitive, ordinal table of <c>contracts/adapter/playwright-mapping.md</c>
/// §status — the raw vocabulary the Playwright reporter sends (<c>passed</c>, <c>failed</c>, <c>timedOut</c>,
/// <c>interrupted</c>, <c>skipped</c>) plus the already-normalised <c>timed_out</c>/<c>error</c> spellings a
/// non-Playwright producer may send: <c>passed</c>→<see cref="Passed"/>, <c>failed</c>→<see cref="Failed"/>,
/// <c>timedOut</c>/<c>timed_out</c>→<see cref="TimedOut"/>, <c>skipped</c>→<see cref="Skipped"/>,
/// <c>interrupted</c>/<c>error</c>→<see cref="Error"/>. Anything else — a future producer value, an empty or
/// whitespace string, a different case such as <c>PASSED</c>, or a prototype name such as <c>constructor</c> —
/// maps to <see cref="Error"/> and is never dropped (05.3.3-T1.2). The server recomputes this from the raw value
/// and never reads the adapter's <c>ext.normalized</c> copy (04.3.2-T2 hand-over, from #44).
/// </para>
/// <para>
/// The <c>xfail</c> fold is the one exception the server honours (coordinator decision S29, jev A 1.00;
/// <c>contracts/adapter/playwright-mapping.md</c> §status and the S18 owner-confirmed unexpected-pass decision of
/// 2026-10-10): an expected failure that fails maps to <see cref="Passed"/>, and an unexpected pass maps to
/// <see cref="Failed"/> (fail closed — an unexpected pass is never evidence of a PASS). It changes only those two
/// raw values; every other raw value maps by the plain table. The caller (04.1.1's event commit and 05.3.3-T2's
/// writer) passes the allow-listed <c>ext.xfail</c>; the adapter's own <c>ext.normalized</c> is never read.
/// </para>
/// <para>
/// <see cref="Blocked"/> is produced only by the server (05.3.3-T2's <c>IBlockedAttemptResolver</c>, FR-05-15): a
/// producer never sends it, so it is not an accepted raw value and normalises to <see cref="Error"/> like any other
/// unknown raw value.
/// </para>
/// </summary>
internal static class CaseAttemptStatus
{
    /// <summary>The attempt ran and its assertions held (00 §8.3).</summary>
    public const string Passed = "passed";

    /// <summary>The attempt ran and at least one assertion failed (00 §8.3).</summary>
    public const string Failed = "failed";

    /// <summary>The attempt was skipped by the suite (00 §8.3).</summary>
    public const string Skipped = "skipped";

    /// <summary>The attempt exceeded its time budget (00 §8.3).</summary>
    public const string TimedOut = "timed_out";

    /// <summary>The attempt has no verdict: the run was stopped, the producer sent an unknown status, or the attempt errored (00 §8.3).</summary>
    public const string Error = "error";

    /// <summary>Server-assigned only: a setup the attempt depends on failed, so the attempt never ran (FR-05-15).</summary>
    public const string Blocked = "blocked";

    /// <summary>The six normalised values of 00 §8.3, in contract order.</summary>
    internal static readonly IReadOnlyList<string> All = [Passed, Failed, Skipped, TimedOut, Error, Blocked];

    /// <summary>
    /// The normalised status of one attempt (00 §8.3). Pure and allocation-free: the result is one of the six
    /// constants of <see cref="All"/>, never the raw value, and the method never throws.
    /// </summary>
    /// <param name="raw">The raw status exactly as the producer sent it; null, empty or unknown maps to <see cref="Error"/>.</param>
    /// <param name="xfail">True when the caller's allow-listed <c>ext.xfail</c> is set (an expected failure).</param>
    internal static string Normalize(string? raw, bool xfail = false) => raw switch
    {
        "passed" => xfail ? Failed : Passed,
        "failed" => xfail ? Passed : Failed,
        "timedOut" or "timed_out" => TimedOut,
        "skipped" => Skipped,
        "interrupted" or "error" => Error,
        _ => Error,
    };
}
