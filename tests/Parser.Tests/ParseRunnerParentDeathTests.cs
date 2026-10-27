using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Shouldly;
using Xunit;

namespace Parser.Tests;

/// <summary>
/// 06.3.6-T1.3 behaviour 10 (AC8): a worker that dies mid-parse leaves no orphan child.
/// <para>
/// The proof needs a second process to kill, so the outer test re-executes this assembly filtered to an inner test
/// (<c>-method</c>), lets that process start the real child over an artifact that never ends, SIGKILLs it, and
/// asserts the child is gone within the bound. What terminates the child is exactly what the design relies on: the
/// parent's end of the child's stdin pipe closes when the parent dies, so the child's read ends, its parse fails, and
/// its stderr/stdout writes to a closed peer cannot keep it alive.
/// </para>
/// <para>
/// Two inner shapes are covered. In the first the child is blocked reading stdin and has written nothing; in the
/// second it has accepted a well-formed document head and is producing output while the parent dies, so the child
/// loses the reader of its stdout and the writer of its stdin at the same instant.
/// </para>
/// <para>
/// What these tests do not cover, for a stated reason: a child blocked on a <em>full stdout pipe</em> cannot be
/// constructed through the runner, because the runner is the reader — a full pipe means the parent stopped reading,
/// which only happens once it is already gone, and then the read end is closed, which is the EPIPE path the second
/// shape exercises. And a child that is already CPU-bound inside the parse after stdin completed is bounded by its
/// own <c>RLIMIT_CPU</c> (20 s) and the profile's 30 s cap, not by pipe closure: those are child-side limits proven by
/// the child's own suites, so no pipe-close proof applies to that state.
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
    private const string ModeVariable = "EVIDGATE_PARSE_DEATH_MODE";

    /// <summary>The inner shape where the child is blocked on an artifact that never ends.</summary>
    private const string StdinMode = "stdin";

    /// <summary>The inner shape where the child is streaming output from a document head that never closes.</summary>
    private const string EmitMode = "emit";

    /// <summary>The file the streaming shape creates once the child has been fed its first record blocks.</summary>
    private const string FlowMarker = "flowing";

    private const string InnerStdinTest = "Parser.Tests.ParseRunnerParentDeathTests.Parent_Process_Runs_Until_It_Is_Killed";
    private const string InnerEmitTest = "Parser.Tests.ParseRunnerParentDeathTests.Parent_Process_Streams_Until_It_Is_Killed";

    /// <summary>How long the killed parent's child gets to disappear: its own RLIMIT_CPU and profile cap.</summary>
    private static readonly TimeSpan OrphanBound = TimeSpan.FromSeconds(30);

    [Fact]
    public Task WorkerKilledMidParse_LeavesNoOrphanChild() =>
        AssertChildDiesWithItsParentAsync(InnerStdinTest, StdinMode, inFlightMarker: null);

    /// <summary>
    /// The streaming shape: the parent is killed while the child is emitting records for a document head it accepted.
    /// </summary>
    [Fact]
    public Task WorkerKilledWhileTheChildStreams_LeavesNoOrphanChild() =>
        AssertChildDiesWithItsParentAsync(InnerEmitTest, EmitMode, FlowMarker);

    /// <summary>
    /// Runs one inner shape to the point where it reported its child's pid, kills that parent process, and asserts the
    /// child is gone within <see cref="OrphanBound"/> — where a zombie counts as gone, because a reparented child is
    /// reaped by whatever the host runs as pid 1 and this test cannot be the one that reaps it.
    /// </summary>
    /// <param name="innerTest">The fully qualified inner test that starts the child.</param>
    /// <param name="mode">Which inner shape to run.</param>
    /// <param name="inFlightMarker">
    /// A file the inner process creates once its child has been fed input, or null when the child is blocked by
    /// construction and no handshake is needed.
    /// </param>
    /// <returns>Nothing; the test fails on any surviving child.</returns>
    private static async Task AssertChildDiesWithItsParentAsync(string innerTest, string mode, string? inFlightMarker)
    {
        ParseRunnerHarness.RequirePosixShell("SIGKILL of a second process");

        var root = Path.Combine(
            Path.GetTempPath(),
            "evidgate-parse-parent-death-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var parent = StartParentProcess(innerTest, mode, root);

            _ = parent.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            _ = parent.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

            await ParseRunnerHarness.WaitUntilAsync(
                () => ReadReportedChildPid(root) is not null,
                TimeSpan.FromSeconds(60),
                "the second process never reported the pid of the child it started");

            var childPid = ReadReportedChildPid(root)
                ?? throw new InvalidOperationException("the reported child pid vanished between the wait and the read");

            ParseRunnerHarness.IsAlive(childPid).ShouldBeTrue("the child must be running when its parent is killed");

            // The pid the other process reported is the parser child, not a helper of its own: the proof is about the
            // process the runner started.
            CommandLineOf(childPid).ShouldContain("Evidgate.Parser.dll", Case.Sensitive);

            if (inFlightMarker is not null)
            {
                // Not a fixed pause: the other process reports that the child has actually been fed the head and the
                // first record blocks, and the document never closes, so the run stays in flight from here on.
                await ParseRunnerHarness.WaitUntilAsync(
                    () => File.Exists(Path.Combine(root, inFlightMarker)),
                    TimeSpan.FromSeconds(60),
                    $"the second process never reported that the child was consuming its input ({inFlightMarker})");
            }

            parent.Kill();

            await parent.WaitForExitAsync(TestContext.Current.CancellationToken);

            await ParseRunnerHarness.WaitUntilAsync(
                () => !ParseRunnerHarness.IsAlive(childPid),
                OrphanBound,
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

        if (string.IsNullOrEmpty(root) || Environment.GetEnvironmentVariable(ModeVariable) != StdinMode)
        {
            Assert.Skip(
                "Runs only as the second process of WorkerKilledMidParse_LeavesNoOrphanChild: the proof needs a "
                + "parent process that is SIGKILLed, which an in-process test cannot be. Skipped, not passed.");
        }

        using var runner = ChildRunnerFor(root);

        // 64 MiB is above any byte the child will ever see here; the stream trickles forever, so the child blocks on
        // its read and the run cannot complete on its own.
        var run = runner.RunAsync(
            new ParseRunRequest(ParseRunnerProfile.Xml, 64L * 1024 * 1024, null, new EndlessStream()),
            TestContext.Current.CancellationToken);

        await ReportChildPidAsync(root);

        await run;

        // Reached only when the child completed without anyone killing the parent, which would make the proof empty.
        Assert.Fail("the child finished on its own before the parent was killed");
    }

    /// <summary>
    /// The second process of the streaming shape: the child gets a well-formed document head followed by an endless
    /// run of records, so it is producing stdout while the parent dies. Only a kill ends the parent, so the outer test
    /// controls the moment; whether the child is mid-emit at that instant is the profile's streaming behaviour, and
    /// either way it must not survive its parent.
    /// </summary>
    [Fact]
    public async Task Parent_Process_Streams_Until_It_Is_Killed()
    {
        var root = Environment.GetEnvironmentVariable(RootVariable);

        if (string.IsNullOrEmpty(root) || Environment.GetEnvironmentVariable(ModeVariable) != EmitMode)
        {
            Assert.Skip(
                "Runs only as the second process of WorkerKilledWhileTheChildStreams_LeavesNoOrphanChild: the proof "
                + "needs a parent process that is SIGKILLed while its child streams. Skipped, not passed.");
        }

        using var runner = ChildRunnerFor(root);

        var reported = 0;

        var run = runner.RunAsync(
            new ParseRunRequest(
                ParseRunnerProfile.Xml,
                64L * 1024 * 1024,
                null,
                new PacedDocumentStream(count =>
                {
                    if (count >= 3 && Interlocked.Exchange(ref reported, 1) == 0)
                    {
                        // The child has taken the head and the first record blocks, and the document never closes, so
                        // the run is in flight from here on; the outer test kills this process off this signal.
                        File.WriteAllText(
                            Path.Combine(root, FlowMarker),
                            count.ToString(CultureInfo.InvariantCulture));
                    }
                })),
            TestContext.Current.CancellationToken);

        await ReportChildPidAsync(root);

        await run;

        Assert.Fail("the child finished on its own before the parent was killed");
    }

    /// <summary>The production runner over the real child, with a wall clock this test never reaches.</summary>
    /// <param name="root">The directory the two processes coordinate through, which is also the temp root.</param>
    /// <returns>The runner; the caller disposes it.</returns>
    private static ChildProcessParseRunner ChildRunnerFor(string root)
    {
        var options = new ParseRunnerOptions
        {
            TempRoot = root,
            ChildPath = ChildRunner.ChildAssemblyPath(),
            WallClockSeconds = 600,

            // The child's own CPU cap stays the real default: this test kills the parent long before it matters.
        };

        return new ChildProcessParseRunner(options, ChildCommandLine.ForOptions);
    }

    /// <summary>
    /// Waits until this process has the child it just started, and writes the pid to <c>child.pid</c> in the shared
    /// directory. The pid file is also the handshake: the outer test kills this process only after it exists.
    /// </summary>
    /// <param name="root">The shared directory.</param>
    /// <returns>Nothing; the process fails when no child appears.</returns>
    private static async Task ReportChildPidAsync(string root)
    {
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

        // Written beside the final name and moved into place: a reader that sees child.pid sees the whole number, so
        // the handshake cannot hand the other process a file that exists but is still empty.
        var target = Path.Combine(root, "child.pid");
        var staging = target + ".staging";

        await File.WriteAllTextAsync(
            staging,
            childPid.ToString(CultureInfo.InvariantCulture) + "\n",
            TestContext.Current.CancellationToken);
        File.Move(staging, target, overwrite: true);
    }

    /// <summary>Reads the pid the other process reported, or null while it has not reported a whole number yet.</summary>
    /// <param name="root">The shared directory.</param>
    /// <returns>The pid, or null.</returns>
    private static int? ReadReportedChildPid(string root)
    {
        var path = Path.Combine(root, "child.pid");

        string text;

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            text = File.ReadAllText(path).Trim();
        }
        catch (IOException)
        {
            return null;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) && pid > 0
            ? pid
            : null;
    }

    /// <summary>Re-executes this assembly, filtered to one test, with the death probe's variables set.</summary>
    /// <param name="test">The fully qualified test method to run.</param>
    /// <param name="mode">Which inner shape the re-executed process must run.</param>
    /// <param name="root">The directory the two processes coordinate through.</param>
    /// <returns>The started process; the caller kills it.</returns>
    private static Process StartParentProcess(string test, string mode, string root)
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
        startInfo.Environment[ModeVariable] = mode;

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

    /// <summary>
    /// A well-formed document head followed by an endless run of test cases: the child accepts the document and keeps
    /// producing stdout while the parent lives, so a parent killed now dies while its child is streaming rather than
    /// idle on its first read. The document never closes, so the run cannot complete on its own.
    /// </summary>
    private sealed class PacedDocumentStream : Stream
    {
        private const int DelayMilliseconds = 20;

        /// <summary>The opening of a JUnit report: the shape the XML profile is proven against.</summary>
        private const string Prologue = """<testsuites><testsuite name="stream">""";

        /// <summary>One block of test cases, repeated forever; the closing tags are never written.</summary>
        private const string Block =
            """<testcase name="a" classname="k"/><testcase name="b" classname="k"/>"""
            + """<testcase name="c" classname="k"/><testcase name="d" classname="k"/>""";

        private readonly byte[] prologue = Encoding.UTF8.GetBytes(Prologue);
        private readonly byte[] block = Encoding.UTF8.GetBytes(Block);
        private readonly Action<int>? onRead;
        private int prologueOffset;
        private int reads;

        /// <summary>Creates the stream.</summary>
        /// <param name="onRead">
        /// Called with the running count of completed reads, on the reader's thread, so a test can tell when the child
        /// has been fed.
        /// </param>
        public PacedDocumentStream(Action<int>? onRead = null) => this.onRead = onRead;

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

        public override int Read(byte[] buffer, int offset, int count) => Next(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => Next(buffer);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(DelayMilliseconds, cancellationToken).ConfigureAwait(false);

            return Next(buffer.Span);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <summary>The next bytes of the document: the prologue once, then one block per read.</summary>
        /// <param name="destination">The caller's buffer.</param>
        /// <returns>How many bytes were written; never 0 for a non-empty buffer, because the document never ends.</returns>
        private int Next(Span<byte> destination)
        {
            if (destination.IsEmpty)
            {
                return 0;
            }

            var written = 0;

            if (prologueOffset < prologue.Length)
            {
                var take = Math.Min(destination.Length, prologue.Length - prologueOffset);

                prologue.AsSpan(prologueOffset, take).CopyTo(destination);
                prologueOffset += take;
                written = take;
            }

            if (written < destination.Length)
            {
                var take = Math.Min(destination.Length - written, block.Length);

                block.AsSpan(0, take).CopyTo(destination[written..]);
                written += take;
            }

            onRead?.Invoke(++reads);

            return written;
        }
    }
}
