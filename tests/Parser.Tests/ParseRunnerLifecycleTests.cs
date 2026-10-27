using System.Diagnostics;
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
        ParseRunnerHarness.IsAlive(harness.ReadPid("child.pid")).ShouldBeFalse();
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
        // this run. Both parent waits are bounded — the wall clock for Process.WaitForExitAsync (which itself waits for
        // the pipes) and the drain grace after the kill — so the run must end well before the helper does. The helper
        // is a bare sleep that expires on its own; a tree kill cannot reach it once its parent has exited.
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
