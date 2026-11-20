using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Evidgate.BuildingBlocks.Observability.Redaction;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;

/// <summary>
/// The one implementation of <see cref="IParseRunner"/> (06.3.6 T1.3): spawns the <c>Evidgate.Parser</c> child with
/// three redirected pipes, feeds it the artifact front to back under the byte cap, drains both output pipes
/// concurrently so neither can block the child, and turns the child's stdout into either the record stream or one
/// failure kind.
/// <para>
/// <b>Limits (AC2).</b> Three are the child's (<c>--cpu-seconds</c>, <c>--max-file-bytes 0</c>, and the
/// <c>DOTNET_GCHeapHardLimit</c> in its environment) and one is the parent's: the wall clock, enforced by
/// <see cref="Process.Kill(bool)"/> of the whole tree and, on Unix, always <c>SIGKILL</c>. The child applies its
/// own rlimits before it reads a byte, so a run that cannot honour them exits 3 with no marker and the parent maps
/// it as an abnormal exit.
/// </para>
/// <para>
/// <b>No orphan (AC8).</b> The child never forks, <c>RLIMIT_NPROC</c> is deliberately absent (DECISIONS F14) and the
/// chiseled image has neither <c>setsid</c> nor a way for .NET to <c>setpgid</c> before exec, so the runner kills
/// the process tree rather than a process group. A parent that dies without killing is covered by the protocols
/// themselves: the child's stdin ends at EOF (parent death closes the write end), the resulting write of the
/// marker hits a closed pipe (EPIPE, exit 3) and, should it still be blocked, <c>RLIMIT_CPU</c> and the profile's
/// own 30 s cap bound it.
/// </para>
/// <para>
/// <b>Memory.</b> The runner never buffers more than the caps in <see cref="ParseRunnerOptions"/>: the child's
/// stdout is read through one 64 KiB pipe buffer, a record line grows only toward the per-line cap, and the
/// accepted records are the only retained bytes.
/// </para>
/// <para>
/// <b>Stderr (06.3.6-T1.4, §8 S3).</b> The child's stderr is drained without a bound, so a child that floods it can
/// never block, and at most <see cref="StderrCaptureBytes"/> of it are kept in memory. A failed run carries
/// <see cref="ParseRunFailed.StderrExcerpt"/>: one printable line, at most <see cref="StderrExcerptChars"/>
/// characters, passed through <c>SecretRedactor</c>, for the caller's operator log and nothing else. This class
/// logs nothing — it holds no logger for a reason — and a success result carries no stderr at all.
/// </para>
/// </summary>
internal sealed class ChildProcessParseRunner : IParseRunner, IDisposable
{
    /// <summary>
    /// The stdin block the caller's stream is read in. At least 64 KiB, so a large artifact is not syscall-bound
    /// (plan Notes S117, <em>Parent stdin writes</em>) and a full pipe write matches a full pipe buffer.
    /// </summary>
    private const int StdinBlockBytes = 64 * 1024;

    /// <summary>
    /// How much of the child's stderr is kept. The pipe is drained without a bound (a child that writes gigabytes
    /// must not block) but nothing beyond this is retained anywhere, so the capture cannot become an amplification
    /// path of its own.
    /// </summary>
    private const int StderrCaptureBytes = 4096;

    /// <summary>
    /// The longest excerpt a failed run carries: one log line's worth. The redaction pass can shorten text but
    /// never lengthens it past a mask, so the cap is applied again after it.
    /// </summary>
    private const int StderrExcerptChars = 512;

    /// <summary>The block the stderr pipe is drained in; nothing of it survives past the capture's own limit.</summary>
    private const int StderrBlockBytes = 4096;

    /// <summary>
    /// The one redaction list (00 §5.7 S3, 01.1.2): the same instance shape the host's log providers use, so the
    /// excerpt is scrubbed by one pattern set and not a second copy of it.
    /// </summary>
    private static readonly SecretRedactor Redactor = new();

    /// <summary>
    /// The one grace a run gets once its child is gone (or being killed): the post-kill exit wait and all three
    /// pipe pumps share this single budget, so a descendant that leaked a pipe costs the run one grace and not one
    /// per step. Every wait of the teardown is taken against the same deadline.
    /// </summary>
    private static readonly TimeSpan ExitAfterKillWait = TimeSpan.FromSeconds(10);

    private readonly ParseRunnerOptions options;
    private readonly Func<ParseRunnerOptions, ChildCommand> commandFactory;
    private readonly ConcurrentDictionary<Process, byte> running = new();

    private volatile bool disposed;

    /// <summary>
    /// The test seam at the one instant a dispose cannot otherwise be aimed at: between the child's start and the
    /// moment the running table holds it. Invoked synchronously on the run's own thread; null in production.
    /// </summary>
    internal Action? ChildStarted { get; init; }

    /// <summary>Creates the runner for the host's bound options.</summary>
    /// <param name="options">The bound <c>EVIDGATE__PARSE__*</c> options.</param>
    public ChildProcessParseRunner(IOptions<ParseRunnerOptions> options)
        : this(ValueOf(options), ChildCommandLine.ForOptions)
    {
    }

    /// <summary>
    /// The test seam: the same behaviour over an options instance and a command factory, so a scripted child can
    /// stand in for the real assembly without a second launch path.
    /// </summary>
    /// <param name="options">The options to run under.</param>
    /// <param name="commandFactory">Builds the executable and its leading arguments for one run.</param>
    internal ChildProcessParseRunner(ParseRunnerOptions options, Func<ParseRunnerOptions, ChildCommand> commandFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(commandFactory);

        this.options = options;
        this.commandFactory = commandFactory;
    }

    /// <inheritdoc />
    public async Task<ParseRunResult> RunAsync(ParseRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(disposed, this);

        var tempDirectory = ParseRunTempDirectory.Create(options.TempRoot);

        try
        {
            return await RunCoreAsync(request, tempDirectory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ParseRunTempDirectory.Delete(tempDirectory);
        }
    }

    /// <summary>
    /// Kills every run still in flight and refuses new ones. The host disposes this singleton at shutdown, so a
    /// shutting-down worker leaves no parse child behind; a run caught by the dispose ends as an
    /// <see cref="OperationCanceledException"/> and never as a verdict — the kill is the parent's own, so its exit
    /// code says nothing about the artifact (06.3.6-T1.4, S125 security review F3).
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        foreach (var process in running.Keys)
        {
            KillTree(process);
        }
    }

    private static ParseRunnerOptions ValueOf(IOptions<ParseRunnerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Value;
    }

    private static void AddArgument(ProcessStartInfo startInfo, string value) => startInfo.ArgumentList.Add(value);

    private static async Task<StderrCapture> CaptureStderrAsync(Stream stderr)
    {
        var buffer = new byte[StderrBlockBytes];
        var capture = new StderrCapture();

        try
        {
            // The read is unbounded, the capture is not: a child that fills its stderr pipe must never block, and
            // every byte past the capture's limit is dropped as it arrives.
            while (true)
            {
                var read = await stderr.ReadAsync(buffer).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                capture.Append(buffer.AsSpan(0, read));
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The child closed stderr first: what the capture already holds is the whole answer.
        }

        return capture;
    }

    /// <summary>
    /// Writes the caller's stream to the child's stdin, at most one byte past the cap. A broken pipe is normal:
    /// the child stops reading the moment it can answer — a size rejection never drains stdin (plan Notes S117,
    /// <em>Stdin overflow</em>) — and the verdict is always the stdout marker, never the stdin write.
    /// </summary>
    /// <param name="stdin">The child's stdin; closed when this returns, on every path.</param>
    /// <param name="input">The artifact's bytes; the caller keeps ownership.</param>
    /// <param name="maxBytes">The run's cap; the child needs to see the cap broken to reject the object.</param>
    /// <param name="stop">
    /// Stops the copy once nothing reads the pipe any more — the child exited, or the run is being cancelled. The
    /// stream belongs to the caller, so the run must not keep reading it after it returned.
    /// </param>
    /// <returns>Nothing: a stdin fault never is a verdict by itself.</returns>
    private static async Task WriteStdinAsync(
        Stream stdin,
        Stream input,
        long maxBytes,
        CancellationToken stop)
    {
        var buffer = new byte[StdinBlockBytes];

        try
        {
            var budget = maxBytes >= long.MaxValue ? long.MaxValue : maxBytes + 1;
            long written = 0;

            while (written < budget)
            {
                var want = (int)Math.Min(buffer.Length, budget - written);
                var read = await input.ReadAsync(buffer.AsMemory(0, want), stop).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                await stdin.WriteAsync(buffer.AsMemory(0, read), stop).ConfigureAwait(false);
                written += read;
            }

            await stdin.FlushAsync(stop).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // EPIPE after the marker, or after the child exited: the child's verdict stands.
        }
        catch (OperationCanceledException)
        {
            // The child is gone, so the copy stops here instead of waiting the grace for a stream the caller owns.
        }
        finally
        {
            // Always: the child's read must end even when the caller's stream was short or the pipe broke.
            try
            {
                await stdin.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // Already gone: closing a broken pipe twice is not a fault.
            }
        }
    }

    private static int ExitCodeOf(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            // No exit status was reported: the run has no code to carry (a failed spawn never reaches here).
            return -1;
        }
    }

    /// <summary>
    /// Kills the child's whole process tree. The tree form is what the design relies on (the child never forks, so
    /// the tree is the child; a test child that does fork is still covered on Linux), and a platform whose runtime
    /// cannot walk a tree falls back to killing the process itself.
    /// </summary>
    /// <param name="process">The child.</param>
    private static void KillTree(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or ObjectDisposedException or Win32Exception or NotSupportedException
            or AggregateException)
        {
            // Exited between the check and the kill, already disposed by the caller's own teardown, refused by the
            // OS for a gone process, or a tree walk that lost a descendant mid-way: the whole-process kill is the
            // fallback and either way nothing is left to do.
            try
            {
                process.Kill();
            }
            catch (Exception fallback) when (fallback is
                InvalidOperationException or ObjectDisposedException or Win32Exception)
            {
                // Already gone.
            }
        }
    }

    /// <summary>Waits for a pipe pump against the run's teardown budget, reporting an abandoned pump as null.</summary>
    /// <param name="pump">The pump.</param>
    /// <param name="budget">The run's single teardown deadline, shared with the other two pumps.</param>
    /// <typeparam name="T">What the pump returns: the stdout capture or the stderr capture.</typeparam>
    /// <returns>The pump's result, or <see langword="null"/> when a leaked pipe held it open past the budget.</returns>
    private static async Task<T?> WithinDrainBoundAsync<T>(Task<T> pump, CancellationToken budget)
        where T : class
    {
        try
        {
            return await pump.WaitAsync(budget).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Waits for a pipe pump against the same teardown budget the other pumps use.</summary>
    /// <param name="task">The pump.</param>
    /// <param name="budget">The run's single teardown deadline, shared with the other two pumps.</param>
    /// <returns>Nothing; a pump still running when the budget is spent is abandoned with its stream.</returns>
    private static async Task WithinDrainBoundAsync(Task task, CancellationToken budget)
    {
        try
        {
            await task.WaitAsync(budget).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A descendant holds the pipe: the stream belongs to a process the caller is about to dispose.
        }
    }

    /// <summary>Waits for a killed child to be reaped, against the run's teardown budget.</summary>
    /// <param name="process">The child.</param>
    /// <param name="budget">The run's single teardown deadline.</param>
    /// <returns>Nothing; a child that cannot be reaped is the OS's leftover, not a verdict.</returns>
    private static async Task WaitForExitQuietlyAsync(Process process, CancellationToken budget)
    {
        try
        {
            await process.WaitForExitAsync(budget).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            OperationCanceledException or InvalidOperationException or ObjectDisposedException)
        {
            // The kill was issued either way; a child that cannot be reaped is the OS's leftover, not a verdict.
        }
    }

    private async Task<ParseRunResult> RunCoreAsync(
        ParseRunRequest request,
        string tempDirectory,
        CancellationToken cancellationToken)
    {
        var command = commandFactory(options);
        var limits = new RunLimits(
            options.EffectiveMaxOutputBytes(request.MaxBytes),
            options.EffectiveMaxOutputRecords(request.MaxBytes),
            options.EffectiveMaxRecordBytes(request.MaxBytes));

        // A missing child assembly is a spawn failure, not the child's verdict: `dotnet <missing>.dll` would start a
        // process that exits non-zero and read as an abnormal exit of a child that never ran. A scripted test command
        // carries no assembly path, so this cannot see one.
        if (!string.IsNullOrEmpty(command.ChildAssemblyPath) && !File.Exists(command.ChildAssemblyPath))
        {
            return SpawnFailed();
        }

        using var process = new Process { StartInfo = CreateStartInfo(command, request, tempDirectory) };

        try
        {
            if (!process.Start())
            {
                return SpawnFailed();
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // The host could not exec the child at all: no process, no pipes, no verdict to read.
            return SpawnFailed();
        }

        try
        {
            // The start hook and the registration sit inside the protected region: a hook that throws must still
            // kill the child (the finally below is the only path that does), and the hook runs before the table
            // holds the child so a dispose aimed at that instant exercises the unregistered window.
            ChildStarted?.Invoke();

            running[process] = 0;

            // A Dispose that ran between the entry check and the registration above iterated a table that did not
            // hold this child yet, so nothing killed it: the run ends here, as the cancellation a dispose causes,
            // instead of starting a child no one is tracking. The finally below still kills and reaps the child, and
            // the run never returns the 137 of that kill as a verdict (S125 security review F3).
            if (disposed)
            {
                throw DisposedDuringRun();
            }

            return await RunStartedAsync(process, request, limits, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            running.TryRemove(process, out _);

            // Every path, including a cancellation that is about to propagate: no child outlives its run.
            KillTree(process);

            using var settle = new CancellationTokenSource(ExitAfterKillWait);

            await WaitForExitQuietlyAsync(process, settle.Token).ConfigureAwait(false);
        }
    }

    private async Task<ParseRunResult> RunStartedAsync(
        Process process,
        ParseRunRequest request,
        RunLimits limits,
        CancellationToken cancellationToken)
    {
        var sink = request.Sink ?? new BufferingParseRecordSink();
        var protocol = new ChildOutputProtocol(limits.MaxOutputBytes, limits.MaxOutputRecords, limits.MaxRecordBytes, sink);
        var streamStop = new StreamStop();

        // The stdin copy stops on its own token: nothing reads the pipe once the child is gone, so the run must not
        // keep reading the caller's stream past its return.
        using var stdinCopy = new CancellationTokenSource();

        // The three pipes are served concurrently: a child that writes more than one pipe buffer must never be
        // blocked by the parent, and the parent must never block on stdin while the child answers (behaviour 2).
        var stdoutTask = protocol.ReadAsync(
            process.StandardOutput.BaseStream,
            () =>
            {
                // The parent is the author of this death: the exit code that follows is its consequence, so the
                // verdict must be the kind that caused the kill and never 128+signal.
                streamStop.RecordProtocolKill();
                KillTree(process);
            });
        var stderrTask = CaptureStderrAsync(process.StandardError.BaseStream);
        var stdinTask = WriteStdinAsync(
            process.StandardInput.BaseStream,
            request.Input,
            request.MaxBytes,
            stdinCopy.Token);

        var wallClockExceeded = false;
        var callerCancelled = false;

        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(options.EffectiveWallClock);

            try
            {
                // Completes on the child's own exit and nothing else: the runtime's WaitForExitAsync does not wait
                // for redirected pipes, which is why the drains below are bounded — a descendant that inherited a
                // pipe keeps it open past the child, and an unbounded drain would hang the run.
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Two cancellations end the wait: the caller's and the run's own wall clock.
                callerCancelled = cancellationToken.IsCancellationRequested;
                wallClockExceeded = !callerCancelled;
            }
        }

        if (callerCancelled || wallClockExceeded)
        {
            KillTree(process);
        }

        // One budget for the whole teardown: the post-kill exit wait and all three pumps are taken against the same
        // deadline, so a leaked pipe costs the run one grace instead of one grace per step. An abandoned drain
        // leaves no credible stream: the run then ends malformed, never as a success.
        using var budget = new CancellationTokenSource(ExitAfterKillWait);

        if (callerCancelled || wallClockExceeded)
        {
            await WaitForExitQuietlyAsync(process, budget.Token).ConfigureAwait(false);
        }

        // The child has exited (or is being killed): nothing reads its stdin any more, so the copy is stopped
        // rather than given the grace to notice a broken pipe.
        await stdinCopy.CancelAsync().ConfigureAwait(false);

        var stdoutBound = WithinDrainBoundAsync(stdoutTask, budget.Token);
        var stderrBound = WithinDrainBoundAsync(stderrTask, budget.Token);
        var stdinBound = WithinDrainBoundAsync(stdinTask, budget.Token);

        await Task.WhenAll(stdoutBound, stderrBound, stdinBound).ConfigureAwait(false);

        var capture = await stdoutBound.ConfigureAwait(false) ?? protocol.Abandoned();

        // A stderr pump the teardown grace abandoned still owns its capture, so an abandoned pump yields no
        // excerpt: the bytes it was reading are not a finished record of what the child wrote.
        var stderrExcerpt = (await stderrBound.ConfigureAwait(false))?.Excerpt();

        // A caller cancellation is not a parse verdict, and the pumps above are already finished: no pipe is still
        // read once this throws, so the Process is never killed through a handle its caller already disposed.
        if (callerCancelled)
        {
            throw new OperationCanceledException(
                "The caller cancelled the parse run; the child was killed and its pipes drained.",
                cancellationToken);
        }

        // The runner was disposed while the run was in flight: the kill that ended this child is the parent's own, so
        // the 137 it produced is not evidence about the artifact and must never become a permanent parse_crashed
        // rejection of a benign report. The run therefore carries no verdict at all (S125 security review F3).
        if (disposed)
        {
            throw DisposedDuringRun();
        }

        var exitCode = ExitCodeOf(process);

        // The verdict order is deliberate. A cap breach is the parent's own decision (it killed the child, so the
        // exit code is a consequence, not evidence), and the wall clock is likewise the parent's. A stream the
        // parent killed because it was not the protocol is the parent's decision too: reporting the 137 that kill
        // produced would make a framing violation a race between the kill and the child's own exit. Only after
        // those three does the child's exit code decide, so a signal death of nobody's making reads as
        // ExitedAbnormally with 128+signal rather than as a framing problem; and a child that exited 0 with an
        // unusable stream — or whose stream never closed — is MalformedOutput.
        if (capture.Failure is { } failure && failure != ParseRunFailureKind.MalformedOutput)
        {
            return new ParseRunFailed(failure, exitCode, capture.OutputBytes, capture.RecordCount, stderrExcerpt);
        }

        if (wallClockExceeded)
        {
            return new ParseRunFailed(
                ParseRunFailureKind.WallClockExceeded,
                exitCode,
                capture.OutputBytes,
                capture.RecordCount,
                stderrExcerpt);
        }

        if (capture.Failure == ParseRunFailureKind.MalformedOutput && streamStop.ProtocolKillRecorded)
        {
            return new ParseRunFailed(
                ParseRunFailureKind.MalformedOutput,
                exitCode,
                capture.OutputBytes,
                capture.RecordCount,
                stderrExcerpt);
        }

        if (exitCode != 0)
        {
            // A valid marker does not overrule the exit code: the exit code is the contract (behaviour 5).
            return new ParseRunFailed(
                ParseRunFailureKind.ExitedAbnormally,
                exitCode,
                capture.OutputBytes,
                capture.RecordCount,
                stderrExcerpt);
        }

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

    private static ParseRunFailed SpawnFailed() =>
        new(ParseRunFailureKind.SpawnFailed, null, 0, 0);

    /// <summary>
    /// The exception a run caught by the runner's own disposal ends with. A cancellation is not a parse verdict
    /// (<see cref="IParseRunner"/>), and the child's death was the parent's decision, so no exit code is carried: the
    /// caller's handler persists nothing and the artifact stays <c>ready</c> for the next attempt (S125 F3). The
    /// token is nobody's own, because it is the shutdown that cancelled the run and not the caller.
    /// </summary>
    /// <returns>The cancellation to throw.</returns>
    private static OperationCanceledException DisposedDuringRun() =>
        new(
            "The parse runner was disposed while the run was in flight: the child was killed by the shutdown and the "
            + "run carries no verdict.");

    /// <summary>Builds the child's whole start information: no shell, one argument per entry, scrubbed environment.</summary>
    /// <param name="command">The executable and its leading arguments for this run.</param>
    /// <param name="request">The profile arm and caps.</param>
    /// <param name="tempDirectory">This run's private directory, used as the working directory.</param>
    /// <returns>The start information.</returns>
    private ProcessStartInfo CreateStartInfo(ChildCommand command, ParseRunRequest request, string tempDirectory)
    {
        var startInfo = new ProcessStartInfo(command.Executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = tempDirectory,
        };

        foreach (var argument in command.LeadingArguments)
        {
            AddArgument(startInfo, argument);
        }

        AddArgument(startInfo, "--profile");
        AddArgument(
            startInfo,
            request.Profile switch
            {
                ParseRunnerProfile.Xml => "xml",
                ParseRunnerProfile.Json => "json",
                _ => "archive",
            });

        AddArgument(startInfo, "--max-bytes");
        AddArgument(startInfo, request.MaxBytes.ToString(CultureInfo.InvariantCulture));
        AddArgument(startInfo, "--max-cases");
        AddArgument(startInfo, request.MaxCases.ToString(CultureInfo.InvariantCulture));

        if (request.MediaType is { } mediaType)
        {
            AddArgument(startInfo, "--media-type");
            AddArgument(startInfo, mediaType);
        }

        // The child's own rlimits (06.3.6-T1.3 slice B): CPU seconds, no file writes beyond 0 bytes, and
        // RLIMIT_CORE=0. Both switches are always sent, so a child that predates them is a usage error rather than
        // a silently unlimited run.
        AddArgument(startInfo, "--cpu-seconds");
        AddArgument(startInfo, options.EffectiveCpuSeconds.ToString(CultureInfo.InvariantCulture));
        AddArgument(startInfo, "--max-file-bytes");
        AddArgument(startInfo, "0");

        startInfo.Environment.Clear();

        foreach (var (name, value) in ChildEnvironment.Build(
            command.ChildAssemblyPath,
            options.EffectiveMemoryLimitBytes,
            tempDirectory))
        {
            startInfo.Environment[name] = value;
        }

        return startInfo;
    }

    /// <summary>
    /// The child's stderr as a bounded capture (06.3.6-T1.4): at most <see cref="StderrCaptureBytes"/> bytes are
    /// kept whatever the child writes, and <see cref="Excerpt"/> turns them into the one printable, secret-masked
    /// line a caller may log. It is never stored, never attached to a success result and never lowered into a
    /// reason — the tenant-visible value is only the <c>parse_*</c> code.
    /// </summary>
    private sealed class StderrCapture
    {
        private readonly byte[] buffer = new byte[StderrCaptureBytes];

        private int length;

        /// <summary>
        /// Keeps what still fits and drops the rest. The drain must never block or buffer on the capture's account,
        /// so a child that writes gigabytes of stderr costs this array and nothing more.
        /// </summary>
        /// <param name="block">The bytes just read from the pipe.</param>
        public void Append(ReadOnlySpan<byte> block)
        {
            var room = buffer.Length - length;

            if (room <= 0)
            {
                return;
            }

            var take = Math.Min(room, block.Length);

            block[..take].CopyTo(buffer.AsSpan(length));
            length += take;
        }

        /// <summary>
        /// The captured bytes as one line: decoded as UTF-8 (invalid sequences become the replacement character),
        /// reduced to printable characters with every run of line breaks or tabs folded into one space, then passed
        /// through the shared <see cref="SecretRedactor"/> and cut to <see cref="StderrExcerptChars"/>.
        /// </summary>
        /// <remarks>
        /// The order matters. The single-line pass runs first because the redaction patterns describe an assignment
        /// on one line, so a secret split across a line break would not be recognised after the fact; and the cut
        /// runs last so a mask can never be left half-replaced by a truncation that happened before it. A cut that
        /// lands inside a mask leaves <c>**</c>, which reveals nothing.
        /// </remarks>
        /// <returns>The excerpt, or <see langword="null"/> when the child wrote nothing printable.</returns>
        public string? Excerpt()
        {
            if (length == 0)
            {
                return null;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, length);

            // A capture that filled the buffer was cut in the middle of the child's stream, so its last token is only
            // a prefix of what the child wrote — and a secret the redaction patterns know in full is not recognised
            // in part. The fragment is dropped rather than shown, which changes nothing a caller normally sees (the
            // excerpt is the head of the capture) and stops a shrunk redaction from ever surfacing half a token
            // (S125 security review F8).
            if (length == buffer.Length)
            {
                text = DropPartialTrailingToken(text);
            }

            var line = Redactor.RedactText(SingleLine(text));

            if (line.Length == 0)
            {
                return null;
            }

            return line.Length > StderrExcerptChars ? line[..StderrExcerptChars] : line;
        }

        /// <summary>Removes the last token of a capture the buffer cut in half, with the separator before it.</summary>
        /// <param name="text">The decoded, truncated capture.</param>
        /// <returns>The capture up to its last whole token, or unchanged when it holds no separator.</returns>
        private static string DropPartialTrailingToken(string text)
        {
            for (var index = text.Length - 1; index >= 0; index--)
            {
                if (char.IsWhiteSpace(text[index]))
                {
                    return text[..index];
                }
            }

            // One token and nothing else: there is no whole token to fall back to, so the operator keeps the head the
            // excerpt would show anyway rather than losing the only evidence the child produced.
            return text;
        }

        /// <summary>Folds one bounded text into a single line of printable characters.</summary>
        /// <param name="text">The decoded capture; at most <see cref="StderrCaptureBytes"/> characters.</param>
        /// <returns>The line, trimmed: no control character survives, so a log line cannot be forged or moved.</returns>
        /// <remarks>
        /// The line break set is the one a reader honours and not only the ASCII one: U+2028 LINE SEPARATOR and
        /// U+2029 PARAGRAPH SEPARATOR are <c>char.IsControl</c>-false, so a child that writes them would otherwise
        /// hand a consumer a second record inside one excerpt (S125 security review F8). They fold into the same
        /// single space as <c>\r</c>, <c>\n</c> and <c>\t</c>.
        /// </remarks>
        private static string SingleLine(string text)
        {
            var builder = new StringBuilder(text.Length);
            var pendingSpace = false;

            foreach (var character in text)
            {
                if (character is '\r' or '\n' or '\t' or '\u2028' or '\u2029')
                {
                    pendingSpace = true;

                    continue;
                }

                // Everything else in the control range — an escape sequence, a bell, a form feed — is dropped
                // rather than shown: a terminal must not be able to act on what a child wrote.
                if (char.IsControl(character))
                {
                    continue;
                }

                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(character);
            }

            return builder.ToString().Trim();
        }
    }

    /// <summary>
    /// Whether the parent itself killed the child because its stdout was not the protocol. The stdout pump's failure
    /// hook writes it before the kill, and the verdict reads it once that pump is finished or abandoned, so the two
    /// sides meet through a volatile field rather than through an implicit happens-before the abandoned path does
    /// not have.
    /// </summary>
    private sealed class StreamStop
    {
        private int protocolKillRecorded;

        /// <summary>True once the stdout reader asked the runner to kill the child for a framing violation.</summary>
        public bool ProtocolKillRecorded => Volatile.Read(ref protocolKillRecorded) == 1;

        /// <summary>Records the parent's own kill of the child for a stream that is not the protocol.</summary>
        public void RecordProtocolKill() => Volatile.Write(ref protocolKillRecorded, 1);
    }

    /// <summary>One run's effective output caps, resolved once so a config change cannot move them mid-stream.</summary>
    /// <param name="MaxOutputBytes">The stdout byte cap.</param>
    /// <param name="MaxOutputRecords">The line cap, the marker included.</param>
    /// <param name="MaxRecordBytes">The longest single line.</param>
    private readonly record struct RunLimits(long MaxOutputBytes, int MaxOutputRecords, int MaxRecordBytes);
}
