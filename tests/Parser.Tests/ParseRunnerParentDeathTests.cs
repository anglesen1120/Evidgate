using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Shouldly;
using Xunit;

namespace Parser.Tests;

/// <summary>
/// 06.3.6-T1.3 behaviour 10 (AC8): a worker that dies mid-parse leaves no orphan child.
/// <para>
/// The proof needs a second process to kill, so the outer test re-executes this assembly filtered to the inner test
/// (<c>-method</c>), lets that process start the real child over an input that never ends, SIGKILLs it, and asserts
/// the child is gone within the bound. What terminates the child is exactly what the design relies on: the parent's
/// end of the child's stdin pipe closes when the parent dies, so the child's read ends, its parse fails, and its
/// stderr/stdout writes to a closed peer cannot keep it alive. The inner test skips unless the outer test started it.
/// </para>
/// <para>
/// The bound is the child's own limits, not a kill from anyone: this test does not touch the child itself, which is
/// the point — <c>Process.Kill(entireProcessTree: true)</c> is unavailable to a dead parent.
/// </para>
/// </summary>
public sealed class ParseRunnerParentDeathTests
{
    private const string ProbeVariable = "EVIDGATE_PARSE_DEATH_PROBE";
    private const string RootVariable = "EVIDGATE_PARSE_DEATH_ROOT";
    private const string InnerTest = "Parser.Tests.ParseRunnerParentDeathTests.Parent_Process_Runs_Until_It_Is_Killed";

    [Fact]
    public async Task WorkerKilledMidParse_LeavesNoOrphanChild()
    {
        ParseRunnerHarness.RequirePosixShell("SIGKILL of a second process");

        var root = Path.Combine(
            Path.GetTempPath(),
            "evidgate-parse-parent-death-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var parent = StartParentProcess(InnerTest, root);

            _ = parent.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            _ = parent.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

            await ParseRunnerHarness.WaitUntilAsync(
                () => File.Exists(Path.Combine(root, "child.pid")),
                TimeSpan.FromSeconds(60),
                "the second process never reported the pid of the child it started");

            var childPid = int.Parse(
                (await File.ReadAllTextAsync(Path.Combine(root, "child.pid"), TestContext.Current.CancellationToken)).Trim(),
                CultureInfo.InvariantCulture);

            ParseRunnerHarness.IsAlive(childPid).ShouldBeTrue("the child must be running when its parent is killed");

            // The pid the other process reported is the parser child, not a helper of its own: the proof is about the
            // process the runner started.
            CommandLineOf(childPid).ShouldContain("Evidgate.Parser.dll", Case.Sensitive);

            parent.Kill();

            await parent.WaitForExitAsync(TestContext.Current.CancellationToken);

            await ParseRunnerHarness.WaitUntilAsync(
                () => !ParseRunnerHarness.IsAlive(childPid),
                TimeSpan.FromSeconds(30),
                $"the child (pid {childPid}) outlived the worker that was killed mid-parse");

            parent.ExitCode.ShouldNotBe(0);
        }
        finally
        {
            ParseRunTempDirectory.Delete(root);
        }
    }

    /// <summary>
    /// The second process: it starts the real child over an input that never ends and waits. Only a kill ends it, so
    /// the outer test controls when the parent dies.
    /// </summary>
    [Fact]
    public async Task Parent_Process_Runs_Until_It_Is_Killed()
    {
        var root = Environment.GetEnvironmentVariable(RootVariable);

        if (string.IsNullOrEmpty(root))
        {
            Assert.Skip(
                "Runs only as the second process of WorkerKilledMidParse_LeavesNoOrphanChild: the proof needs a "
                + "parent process that is SIGKILLed, which an in-process test cannot be. Skipped, not passed.");
        }

        var options = new ParseRunnerOptions
        {
            TempRoot = root,
            ChildPath = ChildRunner.ChildAssemblyPath(),
            WallClockSeconds = 600,

            // The child's own CPU cap stays the real default: this test kills the parent long before it matters.
        };

        using var runner = new ChildProcessParseRunner(options, ChildCommandLine.ForOptions);

        // 64 MiB is above any byte the child will ever see here; the stream trickles forever, so the child blocks on
        // its read and the run cannot complete on its own.
        var run = runner.RunAsync(
            new ParseRunRequest(ParseRunnerProfile.Xml, 64L * 1024 * 1024, null, new EndlessStream()),
            TestContext.Current.CancellationToken);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        var childPid = 0;

        while (childPid == 0 && DateTime.UtcNow < deadline)
        {
            childPid = ChildProcessIds(Environment.ProcessId).FirstOrDefault();

            if (childPid == 0)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }

        childPid.ShouldNotBe(0, "the second process never saw the child it started");
        await File.WriteAllTextAsync(
            Path.Combine(root, "child.pid"),
            childPid.ToString(CultureInfo.InvariantCulture) + "\n",
            TestContext.Current.CancellationToken);

        await run;

        // Reached only when the child completed without anyone killing the parent, which would make the proof empty.
        Assert.Fail("the child finished on its own before the parent was killed");
    }

    /// <summary>Re-executes this assembly, filtered to one test, with the death probe's variables set.</summary>
    /// <param name="test">The fully qualified test method to run.</param>
    /// <param name="root">The directory the two processes coordinate through.</param>
    /// <returns>The started process; the caller kills it.</returns>
    private static Process StartParentProcess(string test, string root)
    {
        var host = Environment.ProcessPath
            ?? throw new InvalidOperationException("the test host's own executable path is unavailable");

        var startInfo = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // Restart this test host exactly as it was started: through the muxer (which needs `exec` and the assembly)
        // or as the project's own apphost (which needs the runtime root to find the shared framework).
        if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.Ordinal))
        {
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add(typeof(ParseRunnerParentDeathTests).Assembly.Location);
        }

        startInfo.Environment["DOTNET_ROOT"] = DotnetRoot(host);
        startInfo.ArgumentList.Add("-method");
        startInfo.ArgumentList.Add(test);
        startInfo.ArgumentList.Add("-noLogo");
        startInfo.Environment[ProbeVariable] = "1";
        startInfo.Environment[RootVariable] = root;

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("the second process did not start");
    }

    /// <summary>The runtime root the re-executed host needs to find the shared framework.</summary>
    /// <param name="host">The current host's executable path.</param>
    /// <returns>The DOTNET_ROOT directory.</returns>
    private static string DotnetRoot(string host)
    {
        if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.Ordinal))
        {
            return Path.GetDirectoryName(host)
                ?? throw new InvalidOperationException("the muxer's directory is unavailable");
        }

        // An apphost locates the runtime however this process was started: <root>/shared/Microsoft.NETCore.App/<ver>/.
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());

        return runtime.Parent?.Parent?.Parent?.FullName
            ?? throw new InvalidOperationException("the runtime directory has no root to derive DOTNET_ROOT from");
    }

    /// <summary>Reads a live process's command line through <c>ps</c>.</summary>
    /// <param name="pid">The process id.</param>
    /// <returns>The command line as <c>ps</c> prints it.</returns>
    private static string CommandLineOf(int pid)
    {
        var startInfo = new ProcessStartInfo("/bin/ps")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("command=");
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));

        using var ps = Process.Start(startInfo)
            ?? throw new InvalidOperationException("/bin/ps did not start");

        var command = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit(5000);

        return command;
    }

    /// <summary>Lists the direct children of a process, using <c>ps</c>, because .NET exposes no parent id.</summary>
    /// <param name="parentId">The parent's pid.</param>
    /// <returns>The children's pids, in the order <c>ps</c> printed them.</returns>
    private static List<int> ChildProcessIds(int parentId)
    {
        var startInfo = new ProcessStartInfo("/bin/ps")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("-ax");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("pid=,ppid=");

        using var ps = Process.Start(startInfo)
            ?? throw new InvalidOperationException("/bin/ps did not start");

        var children = new List<int>();

        while (ps.StandardOutput.ReadLine() is { } line)
        {
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (columns.Length == 2
                && int.TryParse(columns[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
                && int.TryParse(columns[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ppid)
                && ppid == parentId)
            {
                children.Add(pid);
            }
        }

        ps.WaitForExit(5000);

        return children;
    }

    /// <summary>
    /// An input that never ends: a block every few milliseconds, so the child keeps reading and the run stays in
    /// flight until the parent dies. The bytes are never a well-formed document, which is fine — the child is killed
    /// before it parses.
    /// </summary>
    private sealed class EndlessStream : Stream
    {
        private const int BlockBytes = 4096;
        private const int BlockDelayMilliseconds = 20;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = Math.Min(count, BlockBytes);

            buffer.AsSpan(offset, take).Fill((byte)'x');
            Thread.Sleep(BlockDelayMilliseconds);

            return take;
        }

        public override int Read(Span<byte> buffer)
        {
            var take = Math.Min(buffer.Length, BlockBytes);

            buffer[..take].Fill((byte)'x');
            Thread.Sleep(BlockDelayMilliseconds);

            return take;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var take = Math.Min(buffer.Length, BlockBytes);

            await Task.Delay(BlockDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            buffer.Span[..take].Fill((byte)'x');

            return take;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
