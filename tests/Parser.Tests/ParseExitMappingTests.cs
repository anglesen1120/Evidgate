using System.Text;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Shouldly;
using Xunit;

namespace Parser.Tests;

/// <summary>
/// 06.3.6 AC3, the exit-mapping half of 06.3.6-T1.4: every failed child run becomes exactly one
/// <c>rejected</c> reason, or (a spawn failure) no reason at all, and every non-verdict death is
/// <c>parse_crashed</c> rather than a limit that was never applied.
/// <para>
/// The mapping is a pure function of the failed run, so most of it is a table here; the four real-process shapes
/// below prove the codes the child and the OS actually produce (4, 152, 137, and the EPIPE/internal 3) reach that
/// table through the runner. One measured fact fixes the ambiguous case: a parent cannot read its child's CPU time
/// after the child was reaped (<c>Process.TotalProcessorTime</c> throws on Linux — measured in
/// <c>mcr.microsoft.com/dotnet/sdk:10.0.100</c>). The child's <c>RLIMIT_CPU</c> soft value sits one second below
/// its hard value, so the CPU limit arrives as <c>152</c> (<c>parse_timeout</c>; measured against the real child
/// in the same container, and asserted in <c>ChildLimitsTests</c> through <c>/proc/&lt;pid&gt;/limits</c>); <c>137</c>
/// stays for a kernel OOM kill, the escalation one second later, and any other <c>SIGKILL</c>, all of which read as
/// <c>parse_crashed</c>.
/// </para>
/// </summary>
public sealed class ParseExitMappingTests
{
    /// <summary>
    /// The whole table: every <see cref="ParseRunFailureKind"/> and every exit code that has a documented meaning,
    /// with the answer the handler depends on. A spawn failure is the only row with no reason.
    /// </summary>
    /// <remarks>
    /// The kind travels as its name because this member is public (xUnit needs a public data source) while the enum
    /// is internal to the module: a public member may not expose a less accessible type argument.
    /// </remarks>
    public static TheoryData<string, int?, bool, string> MappingTable => new()
    {
        // No child, no verdict: the queue retries instead of rejecting the artifact (06.3.6 §6).
        { nameof(ParseRunFailureKind.SpawnFailed), null, false, string.Empty },
        { nameof(ParseRunFailureKind.SpawnFailed), 0, false, string.Empty },
        { nameof(ParseRunFailureKind.SpawnFailed), 4, false, string.Empty },
        { nameof(ParseRunFailureKind.SpawnFailed), 137, false, string.Empty },

        // The wall clock is the parent's own decision, so the exit code the kill produced cannot change it.
        { nameof(ParseRunFailureKind.WallClockExceeded), null, true, ParseRunRejection.Timeout },
        { nameof(ParseRunFailureKind.WallClockExceeded), 137, true, ParseRunRejection.Timeout },
        { nameof(ParseRunFailureKind.WallClockExceeded), 4, true, ParseRunRejection.Timeout },

        // The child reported its own heap ceiling.
        { nameof(ParseRunFailureKind.ExitedAbnormally), 4, true, ParseRunRejection.Oom },

        // The CPU limit's signal. Measured against the real child in mcr.microsoft.com/dotnet/sdk:10.0.100: with
        // --cpu-seconds 1 (soft 1 s, hard 2 s) and an endless JSON array, the child dies at 152 with empty stderr,
        // and /proc/<pid>/limits reads back "7 8 seconds" for the --cpu-seconds 7 pair.
        { nameof(ParseRunFailureKind.ExitedAbnormally), 152, true, ParseRunRejection.Timeout },

        // Everything else the child or the OS can produce: internal error and EPIPE (3), SIGKILL (137, ambiguous),
        // an uncaught runtime abort (134), an ordinary non-zero exit, and no code at all.
        { nameof(ParseRunFailureKind.ExitedAbnormally), 3, true, ParseRunRejection.Crashed },
        { nameof(ParseRunFailureKind.ExitedAbnormally), 137, true, ParseRunRejection.Crashed },
        { nameof(ParseRunFailureKind.ExitedAbnormally), 134, true, ParseRunRejection.Crashed },
        { nameof(ParseRunFailureKind.ExitedAbnormally), 1, true, ParseRunRejection.Crashed },
        { nameof(ParseRunFailureKind.ExitedAbnormally), null, true, ParseRunRejection.Crashed },

        // The parent's own caps: the artifact's output, so the artifact crashed producing it.
        { nameof(ParseRunFailureKind.OutputBytesExceeded), 0, true, ParseRunRejection.Crashed },
        { nameof(ParseRunFailureKind.OutputBytesExceeded), 137, true, ParseRunRejection.Crashed },
        { nameof(ParseRunFailureKind.OutputRecordsExceeded), 0, true, ParseRunRejection.Crashed },

        // A stream that is not the protocol: a missing, duplicated, non-last or miscounted marker.
        { nameof(ParseRunFailureKind.MalformedOutput), 0, true, ParseRunRejection.Crashed },
        { nameof(ParseRunFailureKind.MalformedOutput), 3, true, ParseRunRejection.Crashed },
    };

    /// <summary>
    /// AC3 through the real runner: one shape per reason, each driven by a child that produces that exit code, so
    /// the table above is proven to be reachable and not merely consistent.
    /// </summary>
    [Fact]
    public async Task ExitMapping_Oom_Timeout_Crash_Garbage_AllReject()
    {
        ParseRunnerHarness.RequirePosixShell("scripted children for the timeout, crash, signal and garbage shapes");

        using var harness = new ParseRunnerHarness();

        // The heap limit's protocol: the child catches its own OutOfMemoryException and exits 4 (the real child's
        // end-to-end proof is Heap_Limit_Death_Exits_Four_And_Is_Parse_Oom).
        var oom = await FailAsync(harness, "printf 'evidgate-parser: memory\\n' >&2; exit 4");
        oom.Failure.ShouldBe(ParseRunFailureKind.ExitedAbnormally);
        oom.ChildExitCode.ShouldBe(4);
        ReasonOf(oom).ShouldBe(ParseRunRejection.Oom);

        // The wall clock: the parent kills the tree, and that is a timeout whatever code the kill produced.
        var timeout = await FailAsync(
            harness,
            "/bin/sleep 300",
            options => options.WallClockSeconds = 1);
        timeout.Failure.ShouldBe(ParseRunFailureKind.WallClockExceeded);
        ReasonOf(timeout).ShouldBe(ParseRunRejection.Timeout);

        // The CPU limit's signal, 128 + SIGXCPU.
        var cpu = await FailAsync(harness, "printf 'x' >&2; /bin/kill -24 $$");
        cpu.Failure.ShouldBe(ParseRunFailureKind.ExitedAbnormally);
        cpu.ChildExitCode.ShouldBe(152);
        ReasonOf(cpu).ShouldBe(ParseRunRejection.Timeout);

        // Exit 3: the internal-error and closed-stdout class, which must stay a crash and never a limit. The real
        // EPIPE death and this same exit 3 are proven child-side by
        // ChildProtocolTests.A_Closed_Stdout_Ends_The_Run_With_Exit_3_And_No_Marker; this case proves the parent's
        // half of the same contract.
        var internalError = await FailAsync(harness, "printf 'evidgate-parser: internal error\\n' >&2; exit 3");
        internalError.ChildExitCode.ShouldBe(3);
        ReasonOf(internalError).ShouldBe(ParseRunRejection.Crashed);

        // SIGKILL, 137: ambiguous between the CPU limit and a kernel OOM kill, so it is a crash (see the type
        // remarks). The wall clock never shows this code, because that death is attributed before the exit decides.
        var killed = await FailAsync(harness, "printf '{\"a\":1}\\n'; /bin/kill -9 $$");
        killed.Failure.ShouldBe(ParseRunFailureKind.ExitedAbnormally);
        killed.ChildExitCode.ShouldBe(137);
        ReasonOf(killed).ShouldBe(ParseRunRejection.Crashed);

        // A stdout stream that is not the protocol.
        var garbage = await FailAsync(harness, "printf 'not-json\\n'");
        garbage.Failure.ShouldBe(ParseRunFailureKind.MalformedOutput);
        ReasonOf(garbage).ShouldBe(ParseRunRejection.Crashed);

        // A marker that miscounts: framing is a contract, so the counter decides even though every line parses.
        var miscounted = await FailAsync(harness, "printf '{\"a\":1}\\n{\"done\":true,\"count\":2}\\n'");
        miscounted.Failure.ShouldBe(ParseRunFailureKind.MalformedOutput);
        ReasonOf(miscounted).ShouldBe(ParseRunRejection.Crashed);

        // One line past the per-line cap: the parent's own byte cap, still a rejected artifact.
        var oversized = await FailAsync(
            harness,
            "/usr/bin/head -c 2000000 /dev/zero | /usr/bin/tr '\\0' a");
        oversized.Failure.ShouldBe(ParseRunFailureKind.OutputBytesExceeded);
        ReasonOf(oversized).ShouldBe(ParseRunRejection.Crashed);

        // A host that cannot exec the child at all is the one failure with no tenant-visible reason.
        using var unspawnable = harness.Spawn(new ChildCommand("/nonexistent/evidgate-parse-child", [], string.Empty));
        var spawn = (await ParseRunnerHarness.RunAsync(
            unspawnable,
            ParseRunnerProfile.Xml,
            1024,
            [1],
            cancellationToken: TestContext.Current.CancellationToken)).ShouldBeOfType<ParseRunFailed>();

        spawn.Failure.ShouldBe(ParseRunFailureKind.SpawnFailed);
        ParseRunRejection.TryMap(spawn, out var noReason).ShouldBeFalse();
        noReason.ShouldBeEmpty("a spawn failure must not leave a limit reason behind for a caller that ignores it");
    }

    /// <summary>
    /// The real child against a real heap ceiling: an artifact that cannot fit its <c>DOTNET_GCHeapHardLimit</c>
    /// must end as exit 4 and therefore <c>parse_oom</c>, not as the crash an exit 3 would mean (AC3).
    /// </summary>
    /// <remarks>
    /// Linux only, and measured there: the numbers this test's shape rests on come from a Linux container
    /// (<c>mcr.microsoft.com/dotnet/sdk:10.0.100</c>, arm64 host, .NET 10): a caught heap-limit
    /// <see cref="OutOfMemoryException"/> followed by <see cref="Environment.Exit(int)"/> produces exit 4, while an
    /// uncaught one aborts with 134. Windows is skipped because the child refuses a requested rlimit there (exit 3,
    /// which is that platform's contract) and the heap behaviour is not the shipped one.
    /// </remarks>
    [Fact]
    public async Task Heap_Limit_Death_Exits_Four_And_Is_Parse_Oom()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(
                "The child refuses a requested rlimit on Windows (exit 3 is that platform's contract), so the "
                + "heap-limit exit cannot be produced here. Skipped, not passed.");
        }

        using var harness = new ParseRunnerHarness();
        using var runner = harness.RealChild(options => options.MemoryLimitBytes = 64L * 1024 * 1024);

        // Dense numbers: the profile materialises one document for the whole payload (measured ~19x its input), so
        // 8 MiB cannot fit a 64 MiB ceiling and the child reaches its own OOM path.
        var dense = DenseNumbers(8 * 1024 * 1024);
        var result = await ParseRunnerHarness.RunAsync(
            runner,
            ParseRunnerProfile.Json,
            dense.Length,
            dense,
            cancellationToken: TestContext.Current.CancellationToken);

        var failure = result.ShouldBeOfType<ParseRunFailed>();
        failure.Failure.ShouldBe(ParseRunFailureKind.ExitedAbnormally);
        failure.ChildExitCode.ShouldBe(4);
        ReasonOf(failure).ShouldBe(ParseRunRejection.Oom);
    }

    /// <summary>Checks the whole mapping table, one row at a time.</summary>
    /// <param name="failure">The failure kind's name, parsed back into the module's enum.</param>
    /// <param name="exitCode">The child's exit code, or <see langword="null"/>.</param>
    /// <param name="mapped">Whether the run is a verdict at all.</param>
    /// <param name="expectedReason">The expected <c>parse_*</c> code, or an empty string when it is not a verdict.</param>
    [Theory]
    [MemberData(nameof(MappingTable))]
    public void Maps_Every_Failure_Kind_And_Exit_Code(
        string failure,
        int? exitCode,
        bool mapped,
        string expectedReason)
    {
        var kind = Enum.Parse<ParseRunFailureKind>(failure);
        var failed = new ParseRunFailed(kind, exitCode, OutputBytes: 0, OutputRecords: 0);

        ParseRunRejection.TryMap(failed, out var reason).ShouldBe(mapped);
        reason.ShouldBe(expectedReason);
    }

    /// <summary>The three words are the artifact's failure codes, so their text is a contract (00 §9.2).</summary>
    [Fact]
    public void Reasons_Are_The_Registered_Failure_Codes()
    {
        ParseRunRejection.Oom.ShouldBe("parse_oom");
        ParseRunRejection.Timeout.ShouldBe("parse_timeout");
        ParseRunRejection.Crashed.ShouldBe("parse_crashed");
    }

    /// <summary>Runs one scripted child that must fail, and returns the failure.</summary>
    /// <param name="harness">The harness whose temp root the run uses.</param>
    /// <param name="script">The child's shell script.</param>
    /// <param name="configure">An optional mutation of the options.</param>
    /// <returns>The failed run.</returns>
    private static async Task<ParseRunFailed> FailAsync(
        ParseRunnerHarness harness,
        string script,
        Action<ParseRunnerOptions>? configure = null)
    {
        using var runner = harness.Script(script, configure);

        var result = await ParseRunnerHarness.RunAsync(
            runner,
            ParseRunnerProfile.Xml,
            1024,
            [1],
            cancellationToken: TestContext.Current.CancellationToken);

        return result.ShouldBeOfType<ParseRunFailed>();
    }

    /// <summary>Maps a failure, asserting that it is a verdict at all.</summary>
    /// <param name="failed">The failed run.</param>
    /// <returns>The reason.</returns>
    private static string ReasonOf(ParseRunFailed failed)
    {
        ParseRunRejection.TryMap(failed, out var reason).ShouldBeTrue(
            $"{failed.Failure} with exit {failed.ChildExitCode} must be a tenant-visible rejection");

        return reason;
    }

    /// <summary>A JSON array of single-digit numbers whose text is about <paramref name="bytes"/> bytes long.</summary>
    /// <param name="bytes">The approximate size.</param>
    /// <returns>The UTF-8 payload.</returns>
    private static byte[] DenseNumbers(int bytes) =>
        Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat("1", bytes / 2)) + "]");
}
