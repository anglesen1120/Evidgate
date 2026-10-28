using System.Diagnostics;
using System.Globalization;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Shouldly;
using Xunit;

namespace Parser.Tests;

/// <summary>
/// 06.3.6-T1.3 behaviours 8, 9 and 11(b): the parent's lifecycle. Every path — success, cap breach, malformed
/// stream, failed spawn, wall-clock kill, cancellation and the runner's own disposal — must leave no child, no temp
/// directory and no zombie, and the verdict each path produces is stated once here.
/// </summary>
public sealed class ParseRunnerLifecycleTests
{
    /// <summary>Reports its pid, then wedges: nothing but a kill can end it.</summary>
    private const string WedgeScript = """
        echo $$ > ../child.pid
        /bin/sleep 300
        """;

    /// <summary>Reports its pid, then reads stdin until EOF and never answers: only a kill ends this run.</summary>
    private const string ReadThenWedgeScript = """
        echo $$ > ../child.pid
        /bin/cat > /dev/null
        """;

    /// <summary>
    /// The input cap this file's real-child run uses. Its value is unique to the test on purpose: the runner renders
    /// it as the child's <c>--max-bytes</c>, so the argument vector identifies that run's child in <c>ps</c> even when
    /// the suite runs other children at the same time.
    /// </summary>
    private const long RealChildInputCap = 3_141_593;

    /// <summary>The child's argument for <see cref="RealChildInputCap"/>, as it appears on the command line.</summary>
    private const string RealChildArgumentMarker = "--max-bytes 3141593";

    [Fact]
    public async Task Wall_Clock_Limit_Kills_A_Wedged_Child_And_Removes_Its_Directory()
    {
        ParseRunnerHarness.RequirePosixShell("a wedged scripted child");
        using var harness = new ParseRunnerHarness();
        using var runner = harness.Script(WedgeScript, options => options.WallClockSeconds = 1);

        var stopwatch = Stopwatch.StartNew();
        var result = await ParseRunnerHarness.RunAsync(runner, ParseRunnerProfile.Xml, 1024, [1], cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBeOfType<ParseRunFailed>().Failure.ShouldBe(ParseRunFailureKind.WallClockExceeded);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));

        var pid = harness.ReadPid("child.pid");

        ParseRunnerHarness.IsAlive(pid).ShouldBeFalse();
        ParseRunnerHarness.StateOf(pid).Contains('Z').ShouldBeFalse(
            "the runner must return only after the killed child was reaped: an unreaped child stays a zombie, and a "
            + "pid the OS never freed is a leak even though it can no longer run");
        harness.LeftoverRunDirectories().ShouldBeEmpty();
    }

    /// <summary>
    /// The same wall-clock kill over the real <c>Evidgate.Parser</c> child: the parent's deadline is the only bound
    /// that can end a child blocked on an artifact that never ends, and the real child's pid must be gone and reaped
    /// when the run returns — a scripted child would prove the kill but not that the production child dies by it.
    /// </summary>
    [Fact]
    public async Task Wall_Clock_Limit_Kills_The_Real_Child_And_Reaps_It()
    {
        using var harness = new ParseRunnerHarness();
        using var runner = harness.RealChild(options => options.WallClockSeconds = 2);

        var request = new ParseRunRequest(ParseRunnerProfile.Xml, RealChildInputCap, null, new NeverEndingStream());

        var stopwatch = Stopwatch.StartNew();
        var run = runner.RunAsync(request, TestContext.Current.CancellationToken);

        // Found through the run's own argument vector, not through the process table's order: the suite may run
        // other children of this process at the same time.
        var pid = await ParseRunnerHarness.WaitForChildProcessAsync(
            RealChildArgumentMarker,
            TimeSpan.FromSeconds(20));

        var result = await run;

        result.ShouldBeOfType<ParseRunFailed>().Failure.ShouldBe(ParseRunFailureKind.WallClockExceeded);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));

        ParseRunnerHarness.IsAlive(pid).ShouldBeFalse("the wall-clock kill left the real child alive");
        ParseRunnerHarness.StateOf(pid).Contains('Z').ShouldBeFalse(
            "the runner must return only after the real child was reaped, not merely signalled");
        harness.LeftoverRunDirectories().ShouldBeEmpty();
    }

    [Fact]
    public async Task Caller_Cancellation_Kills_The_Child_And_Rethrows()
    {
        ParseRunnerHarness.RequirePosixShell("a wedged scripted child");
        using var harness = new ParseRunnerHarness();
        using var runner = harness.Script(WedgeScript);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await ParseRunnerHarness.RunAsync(
                runner,
                ParseRunnerProfile.Xml,
                1024,
                [1],
                cancellationToken: cancellation.Token));

        await ParseRunnerHarness.WaitUntilAsync(
            () => File.Exists(harness.Side("child.pid")),
            TimeSpan.FromSeconds(10),
            "the child never reported its pid");

        await ParseRunnerHarness.WaitUntilAsync(
            () => !ParseRunnerHarness.IsAlive(harness.ReadPid("child.pid")),
            TimeSpan.FromSeconds(10),
            "the cancelled run left its child alive");

        harness.LeftoverRunDirectories().ShouldBeEmpty();
    }

    /// <summary>
    /// A stream the parent killed because it was not the protocol stays the framing failure it was, and never
    /// becomes the exit code that kill produced. The old verdict read the child's 137 first, so this case was a race
    /// between the kill and the child's own exit; five runs are what turn "usually malformed" into a check.
    /// </summary>
    [Fact]
    public async Task A_Framing_Violation_Stays_Malformed_Even_When_The_Parent_Is_The_Killer()
    {
        ParseRunnerHarness.RequirePosixShell("a scripted child that writes a bad line and then blocks");

        using var harness = new ParseRunnerHarness();

        for (var iteration = 0; iteration < 5; iteration++)
        {
            // The child writes a line that is not a JSON object and then blocks, so it is still alive when the
            // parent's reader abandons the stream and kills it: exit 137 is the rule, not the exception.
            using var runner = harness.Script(
                """
                printf 'bad\n'
                /bin/sleep 5
                """);

            var result = await ParseRunnerHarness.RunAsync(
                runner,
                ParseRunnerProfile.Xml,
                1024,
                [1],
                cancellationToken: TestContext.Current.CancellationToken);

            result.ShouldBeOfType<ParseRunFailed>().Failure.ShouldBe(ParseRunFailureKind.MalformedOutput);
            harness.LeftoverRunDirectories().ShouldBeEmpty();
        }
    }

    /// <summary>
    /// A caller cancellation drains every pump before it rethrows, so the caller's own stream is not still being read
    /// once the run returned: the stream belongs to the caller and may be disposed or reused right after the await.
    /// </summary>
    [Fact]
    public async Task Caller_Cancellation_Stops_Reading_The_Input_Before_The_Run_Returns()
    {
        ParseRunnerHarness.RequirePosixShell("a scripted child that reads stdin and never answers");

        using var harness = new ParseRunnerHarness();
        using var runner = harness.Script(ReadThenWedgeScript);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var input = new WatchedInput();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await runner.RunAsync(
                new ParseRunRequest(ParseRunnerProfile.Xml, 1024, null, input),
                cancellation.Token));

        input.NoteRunReturned();

        // No read is in flight at the moment of the return, and none starts after it.
        input.ReadInFlightAtReturn.ShouldBeFalse(
            "the runner rethrew the cancellation while the stdin pump was still inside a read of the caller's stream");

        await Task.Delay(300, TestContext.Current.CancellationToken);

        input.ReadsAfterReturn.ShouldBe(0, "the stdin pump kept reading the caller's stream after the run returned");

        await ParseRunnerHarness.WaitUntilAsync(
            () => File.Exists(harness.Side("child.pid")),
            TimeSpan.FromSeconds(10),
            "the child never reported its pid");

        var pid = harness.ReadPid("child.pid");

        ParseRunnerHarness.IsAlive(pid).ShouldBeFalse("the cancelled run left its child alive");
        ParseRunnerHarness.StateOf(pid).Contains('Z').ShouldBeFalse("the cancelled run left its child unreaped");
        harness.LeftoverRunDirectories().ShouldBeEmpty();
    }

    /// <summary>
    /// A dispose that lands while a run is starting must not leave a child behind. The window is the instant between
    /// the child's start and the runner's registration of it — the one window where the dispose's own sweep of the
    /// running table cannot see the child yet — and a whole run can pass through it before a caller's thread gets
    /// another turn, so the runner's start hook puts the dispose exactly there. The run must then end as the abnormal
    /// exit a dispose causes, never at its own wall clock.
    /// </summary>
    [Fact]
    public async Task Disposing_The_Runner_While_A_Run_Starts_Leaves_No_Child()
    {
        ParseRunnerHarness.RequirePosixShell("a wedged scripted child");

        using var harness = new ParseRunnerHarness();

        const string marker = "child-race-unregistered";
        ChildProcessParseRunner? runner = null;

        runner = harness.Script(
            "echo $$ > ../" + marker + ".pid\n/bin/sleep 300",
            options => options.WallClockSeconds = 10,
            () => runner!.Dispose());

        var stopwatch = Stopwatch.StartNew();

        var failure = (await runner.RunAsync(
            new ParseRunRequest(ParseRunnerProfile.Xml, 1024, null, new MemoryStream([1], writable: false)),
            CancellationToken.None)).ShouldBeOfType<ParseRunFailed>();

        runner.Dispose();

        // 137 (SIGKILL) is the abnormal exit the interface documents for a dispose, and the run must return long
        // before the ten-second wall clock: a run that reached its own deadline would prove the re-check never ran.
        failure.Failure.ShouldBe(ParseRunFailureKind.ExitedAbnormally);
        failure.ChildExitCode.ShouldBe(137);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));

        ParseRunnerHarness.LiveChildProcess(marker).ShouldBe(
            0,
            "a dispose in the window between start and registration left its child alive");

        harness.LeftoverRunDirectories().ShouldBeEmpty();
    }

    [Fact]
    public async Task Disposing_The_Runner_Ends_An_In_Flight_Run_And_Kills_The_Child()
    {
        ParseRunnerHarness.RequirePosixShell("a wedged scripted child");
        using var harness = new ParseRunnerHarness();
        var runner = harness.Script(WedgeScript);

        var run = ParseRunnerHarness.RunAsync(runner, ParseRunnerProfile.Xml, 1024, [1], cancellationToken: TestContext.Current.CancellationToken);
        var pid = await harness.WaitForPidAsync("child.pid", TimeSpan.FromSeconds(10));

        runner.Dispose();

        var result = await run;

        // The child was killed by the runner, so the run has no marker: the failure carries the signal's 128+9.
        var failure = result.ShouldBeOfType<ParseRunFailed>();
        failure.Failure.ShouldBe(ParseRunFailureKind.ExitedAbnormally);
        failure.ChildExitCode.ShouldBe(137);

        await ParseRunnerHarness.WaitUntilAsync(
            () => !ParseRunnerHarness.IsAlive(pid),
            TimeSpan.FromSeconds(10),
            "the disposed runner left its child alive");

        harness.LeftoverRunDirectories().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Failed_Spawn_Is_A_SpawnFailed_And_Removes_Its_Directory()
    {
        using var harness = new ParseRunnerHarness();
        using var runner = harness.Spawn(
            new ChildCommand("/nonexistent/evidgate-parse-child", [], string.Empty));

        var result = await ParseRunnerHarness.RunAsync(runner, ParseRunnerProfile.Xml, 1024, [1], cancellationToken: TestContext.Current.CancellationToken);

        var failure = result.ShouldBeOfType<ParseRunFailed>();
        failure.Failure.ShouldBe(ParseRunFailureKind.SpawnFailed);
        failure.ChildExitCode.ShouldBeNull();
        harness.LeftoverRunDirectories().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Descendant_Holding_The_Pipes_Cannot_Hang_The_Run()
    {
        ParseRunnerHarness.RequirePosixShell("a scripted child that leaves a pipe holder behind");

        using var harness = new ParseRunnerHarness();

        // The helper inherits the child's stdout and stderr and outlives it by far, so the pipes never reach EOF for
        // this run. The child's own exit is all Process.WaitForExitAsync waits for — it does not wait for redirected
        // pipes — and the pump drain after it is bounded by the runner's one teardown grace, so the run must end well
        // before the helper does. The helper is a bare sleep that expires on its own; a tree kill cannot reach it once
        // its parent has exited.
        using var runner = harness.Script(
            """
            /bin/sleep 30 &
            exit 0
            """);

        var stopwatch = Stopwatch.StartNew();
        var result = await ParseRunnerHarness.RunAsync(
            runner,
            ParseRunnerProfile.Xml,
            1024,
            [1],
            cancellationToken: TestContext.Current.CancellationToken);

        // The child itself exited 0 without a word, so the only honest verdict is that its stream never closed. What
        // this test buys is the timing: one drain grace, not the helper's 30 s and not one grace per pipe.
        result.ShouldBeOfType<ParseRunFailed>().Failure.ShouldBe(ParseRunFailureKind.MalformedOutput);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(18));
        harness.LeftoverRunDirectories().ShouldBeEmpty();
    }

    [Fact]
    public async Task Temp_Directory_Is_Removed_On_Every_Path()
    {
        ParseRunnerHarness.RequirePosixShell("scripted children");
        using var harness = new ParseRunnerHarness();

        using (var success = harness.Script("""printf '{"done":true,"count":0}\n'"""))
        {
            (await ParseRunnerHarness.RunAsync(success, ParseRunnerProfile.Xml, 1024, [1], cancellationToken: TestContext.Current.CancellationToken))
                .ShouldBeOfType<ParseRunSucceeded>();
            harness.LeftoverRunDirectories().ShouldBeEmpty();
        }

        using (var breach = harness.Script(
            """printf '{"v":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}\n{"done":true,"count":1}\n'""",
            options => options.MaxOutputBytes = 16))
        {
            (await ParseRunnerHarness.RunAsync(breach, ParseRunnerProfile.Xml, 1024, [1], cancellationToken: TestContext.Current.CancellationToken))
                .ShouldBeOfType<ParseRunFailed>().Failure.ShouldBe(ParseRunFailureKind.OutputBytesExceeded);
            harness.LeftoverRunDirectories().ShouldBeEmpty();
        }

        using (var malformed = harness.Script(""))
        {
            (await ParseRunnerHarness.RunAsync(malformed, ParseRunnerProfile.Xml, 1024, [1], cancellationToken: TestContext.Current.CancellationToken))
                .ShouldBeOfType<ParseRunFailed>().Failure.ShouldBe(ParseRunFailureKind.MalformedOutput);
            harness.LeftoverRunDirectories().ShouldBeEmpty();
        }

        using (var wedged = harness.Script(WedgeScript, options => options.WallClockSeconds = 1))
        {
            (await ParseRunnerHarness.RunAsync(wedged, ParseRunnerProfile.Xml, 1024, [1], cancellationToken: TestContext.Current.CancellationToken))
                .ShouldBeOfType<ParseRunFailed>().Failure.ShouldBe(ParseRunFailureKind.WallClockExceeded);
            harness.LeftoverRunDirectories().ShouldBeEmpty();
        }
    }

    /// <summary>
    /// A child that locks a directory inside its own run directory does not keep that directory alive: the recursive
    /// delete cannot walk a mode-000 directory at all, so cleanup restores the owner's bits below the root and
    /// retries, and the run still leaves nothing under the temp root.
    /// </summary>
    [Fact]
    public async Task A_Run_Directory_The_Child_Made_Undeletable_Is_Still_Removed()
    {
        ParseRunnerHarness.RequirePosixShell("a scripted child that locks a directory");

        using var harness = new ParseRunnerHarness();
        using var runner = harness.Script(
            """
            mkdir -p locked/deeper
            printf 'x' > locked/deeper/file
            chmod 000 locked/deeper
            chmod 000 locked
            printf '{"done":true,"count":0}\n'
            """);

        var result = await ParseRunnerHarness.RunAsync(
            runner,
            ParseRunnerProfile.Xml,
            1024,
            [1],
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBeOfType<ParseRunSucceeded>();
        harness.LeftoverRunDirectories().ShouldBeEmpty();
    }

    /// <summary>
    /// A configured root is resolved to an absolute path before the directory is created: the child receives it as
    /// its <c>TMPDIR</c> and as its working directory, so a relative root must not resolve against whatever
    /// directory the host happened to be started from.
    /// </summary>
    [Fact]
    public void A_Configured_Temp_Root_Is_Resolved_To_An_Absolute_Path()
    {
        var relativeRoot = "evgt-runner-relative-" + Guid.NewGuid().ToString("N");
        var path = ParseRunTempDirectory.Create(relativeRoot);

        try
        {
            Path.IsPathFullyQualified(path).ShouldBeTrue($"{path} must be absolute");
            Path.GetDirectoryName(path).ShouldBe(Path.GetFullPath(relativeRoot));
            Directory.Exists(path).ShouldBeTrue();
        }
        finally
        {
            ParseRunTempDirectory.Delete(path).ShouldBeTrue("the test's own directory could not be removed");
        }
    }

    [Fact]
    public async Task Every_Run_Gets_Its_Own_Private_Directory_As_Tmpdir_And_Working_Directory()
    {
        ParseRunnerHarness.RequirePosixShell("a scripted child that prints its directory and mode");

        // Written once per run: `stat` spells the permission bits differently on BSD and GNU, so both are tried.
        static string Report(string name) =>
            "printf '%s\\n' \"$(pwd)\" > ../" + name + "-cwd.txt\n"
            + "printf '%s\\n' \"$TMPDIR\" > ../" + name + "-tmpdir.txt\n"
            + "{ /usr/bin/stat -c '%a' . 2>/dev/null || /usr/bin/stat -f '%Lp' . ; } > ../" + name + "-mode.txt\n"
            + "printf '{\"done\":true,\"count\":0}\\n'";

        using var harness = new ParseRunnerHarness();

        using (var first = harness.Script(Report("first")))
        {
            (await ParseRunnerHarness.RunAsync(first, ParseRunnerProfile.Xml, 1024, [1], cancellationToken: TestContext.Current.CancellationToken)).ShouldBeOfType<ParseRunSucceeded>();
        }

        using (var second = harness.Script(Report("second")))
        {
            (await ParseRunnerHarness.RunAsync(second, ParseRunnerProfile.Xml, 1024, [1], cancellationToken: TestContext.Current.CancellationToken)).ShouldBeOfType<ParseRunSucceeded>();
        }

        var firstDirectory = (harness.ReadSide("first-cwd.txt") ?? string.Empty).Trim();
        var secondDirectory = (harness.ReadSide("second-cwd.txt") ?? string.Empty).Trim();

        firstDirectory.ShouldNotBe(secondDirectory);

        // Both directories are direct children of the temp root. Compared by name, because a child that reports `pwd`
        // sees the resolved path and macOS resolves /var to /private/var while the temp root was written unresolved.
        var rootName = new DirectoryInfo(harness.TempRoot).Name;
        new DirectoryInfo(firstDirectory).Parent!.Name.ShouldBe(rootName);
        new DirectoryInfo(secondDirectory).Parent!.Name.ShouldBe(rootName);
        // TMPDIR is the same directory the child is in. Compared by leaf, because a child that reports `pwd` sees the
        // resolved path (macOS resolves /var to /private/var) while TMPDIR keeps the form the runner was configured with.
        Path.GetFileName((harness.ReadSide("first-tmpdir.txt") ?? string.Empty).Trim())
            .ShouldBe(Path.GetFileName(firstDirectory));
        Path.GetFileName((harness.ReadSide("second-tmpdir.txt") ?? string.Empty).Trim())
            .ShouldBe(Path.GetFileName(secondDirectory));

        // 0700: one run's directory is not readable by another account sharing the temp root.
        (harness.ReadSide("first-mode.txt") ?? string.Empty).Trim().ShouldBe("700");
        (harness.ReadSide("second-mode.txt") ?? string.Empty).Trim().ShouldBe("700");

        harness.LeftoverRunDirectories().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Valid_Marker_With_A_Non_Zero_Exit_Is_An_Abnormal_Exit()
    {
        ParseRunnerHarness.RequirePosixShell("a scripted child that exits non-zero");
        using var harness = new ParseRunnerHarness();
        using var runner = harness.Script("""printf '{"done":true,"count":0}\n'; exit 3""");

        var result = await ParseRunnerHarness.RunAsync(runner, ParseRunnerProfile.Xml, 1024, [1], cancellationToken: TestContext.Current.CancellationToken);

        var failure = result.ShouldBeOfType<ParseRunFailed>();
        failure.Failure.ShouldBe(ParseRunFailureKind.ExitedAbnormally);
        failure.ChildExitCode.ShouldBe(3);
    }

    [Fact]
    public async Task A_Signal_Death_Is_Carried_As_128_Plus_The_Signal()
    {
        ParseRunnerHarness.RequirePosixShell("a scripted child that kills itself");
        using var harness = new ParseRunnerHarness();
        using var runner = harness.Script("""printf '{"a":1}\n'; /bin/kill -9 $$""");

        var result = await ParseRunnerHarness.RunAsync(runner, ParseRunnerProfile.Xml, 1024, [1], cancellationToken: TestContext.Current.CancellationToken);

        var failure = result.ShouldBeOfType<ParseRunFailed>();
        failure.Failure.ShouldBe(ParseRunFailureKind.ExitedAbnormally);
        failure.ChildExitCode.ShouldBe(137);
    }

    /// <summary>
    /// Behaviour 9's tree kill: a child that forks is killed with its descendants, so a runaway helper cannot keep a
    /// pipe open past the run. The tree walk is a runtime capability — the probe states whether this host has it.
    /// </summary>
    [Fact]
    public async Task Grandchild_Is_Killed_With_The_Process_Tree()
    {
        ParseRunnerHarness.RequirePosixShell("a scripted child that forks");

        if (!TreeKillIsSupported())
        {
            Assert.Skip(
                "Process.Kill(entireProcessTree: true) is not implemented on this platform (the probe threw "
                + "PlatformNotSupportedException): the runner falls back to killing the child alone, which a forked "
                + "helper survives. The product target is the Linux worker image, where the tree walk exists. "
                + "Skipped, not passed.");
        }

        using var harness = new ParseRunnerHarness();
        using var runner = harness.Script(
            """
            /bin/sleep 300 &
            echo $! > ../grandchild.pid
            echo $$ > ../child.pid
            /bin/sleep 300
            """,
            options => options.WallClockSeconds = 1);

        var result = await ParseRunnerHarness.RunAsync(runner, ParseRunnerProfile.Xml, 1024, [1], cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBeOfType<ParseRunFailed>().Failure.ShouldBe(ParseRunFailureKind.WallClockExceeded);

        var grandchild = harness.ReadPid("grandchild.pid");

        await ParseRunnerHarness.WaitUntilAsync(
            () => !ParseRunnerHarness.IsAlive(grandchild),
            TimeSpan.FromSeconds(15),
            "the wall-clock kill left the child's forked helper alive");

        harness.LeftoverRunDirectories().ShouldBeEmpty();
    }

    /// <summary>
    /// An artifact that never ends: one byte every few milliseconds, so the child stays blocked on its read and only
    /// the run's own deadline can end it.
    /// </summary>
    private sealed class NeverEndingStream : Stream
    {
        private const int DelayMilliseconds = 20;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            await Task.Delay(DelayMilliseconds, cancellationToken).ConfigureAwait(false);

            buffer.Span[0] = (byte)'<';

            return 1;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// An input stream that records how it is read, so a test can prove the runner stopped reading it before it
    /// returned: the stream belongs to the caller, and a pump still inside a read after the await would be reading
    /// memory the caller may have freed or reused.
    /// </summary>
    private sealed class WatchedInput : Stream
    {
        private const int DelayMilliseconds = 20;

        private bool returned;
        private int inFlight;
        private int readsAfterReturn;
        private bool readInFlightAtReturn;

        /// <summary>How many reads started after the run returned.</summary>
        public int ReadsAfterReturn => Volatile.Read(ref readsAfterReturn);

        /// <summary>Whether a read was still in flight at the moment the run returned.</summary>
        public bool ReadInFlightAtReturn => Volatile.Read(ref readInFlightAtReturn);

        /// <summary>Marks the moment <c>RunAsync</c> returned, so every later read is a violation.</summary>
        public void NoteRunReturned()
        {
            returned = true;

            if (Volatile.Read(ref inFlight) != 0)
            {
                Volatile.Write(ref readInFlightAtReturn, true);
            }
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref returned))
            {
                Interlocked.Increment(ref readsAfterReturn);
            }

            Interlocked.Increment(ref inFlight);

            try
            {
                await Task.Delay(DelayMilliseconds, cancellationToken).ConfigureAwait(false);

                if (buffer.IsEmpty)
                {
                    return 0;
                }

                buffer.Span[0] = (byte)'<';

                return 1;
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static bool TreeKillIsSupported()
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        using var probe = Process.Start(new ProcessStartInfo("/bin/sleep")
        {
            ArgumentList = { "5" },
            UseShellExecute = false,
        });

        if (probe is null)
        {
            return false;
        }

        try
        {
            probe.Kill(entireProcessTree: true);
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
        finally
        {
            probe.WaitForExit(5000);
        }

        return true;
    }
}
