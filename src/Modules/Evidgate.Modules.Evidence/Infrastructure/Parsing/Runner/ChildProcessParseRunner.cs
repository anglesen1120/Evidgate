using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
/// </summary>
internal sealed class ChildProcessParseRunner : IParseRunner, IDisposable
{
    /// <summary>
    /// The stdin block the caller's stream is read in. At least 64 KiB, so a large artifact is not syscall-bound
    /// (plan Notes S117, <em>Parent stdin writes</em>) and a full pipe write matches a full pipe buffer.
    /// </summary>
    private const int StdinBlockBytes = 64 * 1024;

    /// <summary>
    /// The stderr capture size. The pipe is drained without a bound (a child that writes megabytes must not block)
    /// but nothing beyond this is kept anywhere: 06.3.6-T1.4 owns stderr and its redaction.
    /// </summary>
    private const int StderrDrainBytes = 4096;

    /// <summary>How long the runner waits for a killed child to be reaped before it stops waiting.</summary>
    private static readonly TimeSpan ExitAfterKillWait = TimeSpan.FromSeconds(10);

    private readonly ParseRunnerOptions options;
    private readonly Func<ParseRunnerOptions, ChildCommand> commandFactory;
    private readonly ConcurrentDictionary<Process, byte> running = new();

    private volatile bool disposed;

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
    /// shutting-down worker leaves no parse child behind; a run caught by the dispose ends as an abnormal exit.
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

    private static async Task DrainStderrAsync(Stream stderr)
    {
        var buffer = new byte[StderrDrainBytes];

        try
        {
            // The read is unbounded, the capture is not: a child that fills its stderr pipe must never block, and
            // the bytes are dropped because 06.3.6-T1.4 owns the capture and its redaction.
            while (await stderr.ReadAsync(buffer.AsMemory(0, StderrDrainBytes)).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The child closed stderr first: there is nothing left to drain.
        }
    }

    /// <summary>
    /// Writes the caller's stream to the child's stdin, at most one byte past the cap. A broken pipe is normal:
    /// the child stops reading the moment it can answer — a size rejection never drains stdin (plan Notes S117,
    /// <em>Stdin overflow</em>) — and the verdict is always the stdout marker, never the stdin write.
    /// </summary>
    /// <param name="stdin">The child's stdin; closed when this returns, on every path.</param>
    /// <param name="input">The artifact's bytes; the caller keeps ownership.</param>
    /// <param name="maxBytes">The run's cap; the child needs to see the cap broken to reject the object.</param>
    /// <returns>Nothing: a stdin fault is never a verdict by itself.</returns>
    private static async Task WriteStdinAsync(Stream stdin, Stream input, long maxBytes)
    {
        var buffer = new byte[StdinBlockBytes];

        try
        {
            var budget = maxBytes >= long.MaxValue ? long.MaxValue : maxBytes + 1;
            long written = 0;

            while (written < budget)
            {
                var want = (int)Math.Min(buffer.Length, budget - written);
                var read = await input.ReadAsync(buffer.AsMemory(0, want)).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                await stdin.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                written += read;
            }

            await stdin.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // EPIPE after the marker, or after the child exited: the child's verdict stands.
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
            InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // Exited between the check and the kill, refused by the OS for a gone process, or a tree walk that lost
            // a descendant mid-way: the whole-process kill is the fallback and either way nothing is left to do.
            try
            {
                process.Kill();
            }
            catch (Exception fallback) when (fallback is InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }
        }
    }

    /// <summary>Waits for the stdout pump, yielding null after the grace when a leaked pipe holds it open.</summary>
    /// <param name="stdout">The pump.</param>
    /// <returns>The capture, or null when the pump was abandoned.</returns>
    private static async Task<ChildOutputCapture?> WithinDrainBoundAsync(Task<ChildOutputCapture> stdout)
    {
        try
        {
            return await stdout.WaitAsync(ExitAfterKillWait, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Waits for a pipe pump, giving up after the normal exit grace so a leaked pipe cannot hang a run.</summary>
    /// <param name="task">The pump.</param>
    /// <returns>Nothing; a pump still running after the grace is abandoned with its stream.</returns>
    private static async Task WithinDrainBoundAsync(Task task)
    {
        try
        {
            await task.WaitAsync(ExitAfterKillWait, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A descendant holds the pipe: the stream belongs to a process the caller is about to dispose.
        }
    }

    private static async Task WaitForExitQuietlyAsync(Process process)
    {
        try
        {
            using var timeout = new CancellationTokenSource(ExitAfterKillWait);

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
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

        running[process] = 0;

        try
        {
            return await RunStartedAsync(process, request, limits, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            running.TryRemove(process, out _);

            // Every path, including a cancellation that is about to propagate: no child outlives its run.
            KillTree(process);
            await WaitForExitQuietlyAsync(process).ConfigureAwait(false);
        }
    }

    private async Task<ParseRunResult> RunStartedAsync(
        Process process,
        ParseRunRequest request,
        RunLimits limits,
        CancellationToken cancellationToken)
    {
        var protocol = new ChildOutputProtocol(limits.MaxOutputBytes, limits.MaxOutputRecords, limits.MaxRecordBytes);

        // The three pipes are served concurrently: a child that writes more than one pipe buffer must never be
        // blocked by the parent, and the parent must never block on stdin while the child answers (behaviour 2).
        var stdoutTask = protocol.ReadAsync(process.StandardOutput.BaseStream, () => KillTree(process));
        var stderrTask = DrainStderrAsync(process.StandardError.BaseStream);
        var stdinTask = WriteStdinAsync(process.StandardInput.BaseStream, request.Input, request.MaxBytes);

        var wallClockExceeded = false;

        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(options.EffectiveWallClock);

            try
            {
                // Completes when the child exited and both output pipes reached EOF; a descendant that keeps a
                // pipe open is exactly what the deadline (and then the tree kill) is for.
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                wallClockExceeded = true;
                KillTree(process);
                await WaitForExitQuietlyAsync(process).ConfigureAwait(false);
            }
        }

        // Every drain is bounded. A descendant that inherited one of the child's pipes keeps it open past the child's
        // death, so an unbounded await would hang the run forever (the kill in the caller's finally comes after this
        // point). An abandoned drain leaves no credible stream: the run ends malformed, never as a success.
        var stdoutBound = WithinDrainBoundAsync(stdoutTask);
        var stderrBound = WithinDrainBoundAsync(stderrTask);
        var stdinBound = WithinDrainBoundAsync(stdinTask);

        // One grace for all three pumps, together: a leaked pipe must cost the run one grace, not one per pipe.
        await Task.WhenAll(stdoutBound, stderrBound, stdinBound).ConfigureAwait(false);

        var capture = await stdoutBound.ConfigureAwait(false) ?? protocol.Abandoned();

        var exitCode = ExitCodeOf(process);

        // The verdict order is deliberate. A cap breach is the parent's own decision (it killed the child, so the
        // exit code is a consequence, not evidence); the wall clock is likewise the parent's; the child's exit code
        // then outranks a bad stream, so a signal death reads as ExitedAbnormally with 128+signal rather than as a
        // framing problem; and only a child that exited 0 with an unusable stream is MalformedOutput.
        if (capture.Failure is { } failure && failure != ParseRunFailureKind.MalformedOutput)
        {
            return new ParseRunFailed(failure, exitCode, capture.OutputBytes, capture.RecordCount);
        }

        if (wallClockExceeded)
        {
            return new ParseRunFailed(
                ParseRunFailureKind.WallClockExceeded,
                exitCode,
                capture.OutputBytes,
                capture.RecordCount);
        }

        if (exitCode != 0)
        {
            // A valid marker does not overrule the exit code: the exit code is the contract (behaviour 5).
            return new ParseRunFailed(
                ParseRunFailureKind.ExitedAbnormally,
                exitCode,
                capture.OutputBytes,
                capture.RecordCount);
        }

        if (capture.Failure is not null || capture.Records is not { } records)
        {
            return new ParseRunFailed(
                ParseRunFailureKind.MalformedOutput,
                exitCode,
                capture.OutputBytes,
                capture.RecordCount);
        }

        return new ParseRunSucceeded(records, exitCode, capture.OutputBytes, records.Count);
    }

    private static ParseRunFailed SpawnFailed() =>
        new(ParseRunFailureKind.SpawnFailed, null, 0, 0);

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

    /// <summary>One run's effective output caps, resolved once so a config change cannot move them mid-stream.</summary>
    /// <param name="MaxOutputBytes">The stdout byte cap.</param>
    /// <param name="MaxOutputRecords">The line cap, the marker included.</param>
    /// <param name="MaxRecordBytes">The longest single line.</param>
    private readonly record struct RunLimits(long MaxOutputBytes, int MaxOutputRecords, int MaxRecordBytes);
}
