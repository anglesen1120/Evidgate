using System.Diagnostics;
using System.Globalization;

namespace Hosts.IntegrationTests;

/// <summary>
/// Runs a host process and observes exit code, output and (when asked) how long
/// the process takes to stop after SIGTERM. One shared surface for the real-
/// process tests (Shutdown, OptionsValidation, SkeletonBoot).
/// </summary>
internal static class ProcessRunner
{
    /// <summary>Result of a process run.</summary>
    public sealed record Result(
        int ExitCode,
        string Stdout,
        string Stderr,
        TimeSpan ShutdownElapsed);

    /// <summary>The two redirected streams of a started process, read while it runs.</summary>
    /// <param name="Stdout">The child's standard output, completed when the pipe closes.</param>
    /// <param name="Stderr">The child's standard error, completed when the pipe closes.</param>
    public sealed record Output(Task<string> Stdout, Task<string> Stderr);

    /// <summary>
    /// Starts reading both redirected pipes of <paramref name="process"/> and returns the two reads. Every caller
    /// of <see cref="Start"/> that waits on a host without reading its output must call this: the pipes are
    /// redirected (<c>RedirectStandardOutput</c>/<c>RedirectStandardError</c>) and a child that fills one blocks on
    /// the write, so an undrained host can stop answering for reasons the test would otherwise never see. On failure
    /// a caller reports the captured text.
    /// </summary>
    /// <param name="process">A process returned by <see cref="Start"/>.</param>
    public static Output Drain(Process process) =>
        new(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());

    /// <summary>Starts a host process with env overrides applied and removals honoured.</summary>
    public static Process Start(
        string fileName,
        IReadOnlyDictionary<string, string>? env = null,
        IReadOnlyCollection<string>? removeEnvKeys = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (env is not null)
        {
            foreach (var (key, value) in env)
            {
                startInfo.EnvironmentVariables[key] = value;
            }
        }

        if (removeEnvKeys is not null)
        {
            foreach (var key in removeEnvKeys)
            {
                startInfo.EnvironmentVariables.Remove(key);
            }
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"cannot start {fileName}");
        return process;
    }

    /// <summary>Waits for a host to exit and captures its output.</summary>
    public static async Task<Result> WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        var timer = Stopwatch.StartNew();
        await process.WaitForExitAsync(cancellationToken);

        return new Result(
            process.ExitCode,
            await stdout,
            await stderr,
            timer.Elapsed);
    }

    /// <summary>
    /// Kills a host that outlived its test and waits (bounded) for it to exit, so
    /// teardown is observable before the test's database is dropped.
    /// </summary>
    public static async Task StopAsync(Process process)
    {
        KillIfRunning(process);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            // A child that ignores SIGKILL must not hang the suite; its database drop uses (force).
        }
    }

    /// <summary>
    /// Starts a host, waits for a probe URL to answer 200, sends SIGTERM and
    /// reports the time to exit (measured with a monotonic clock, not
    /// <see cref="Process.ExitTime"/>, whose wall-clock resolution is far too
    /// coarse on this host). The caller asserts on ExitCode and elapsed.
    /// </summary>
    public static async Task<Result> RunThenSigtermAsync(
        string fileName,
        IReadOnlyDictionary<string, string> env,
        string readyUrl,
        TimeSpan readyTimeout,
        CancellationToken cancellationToken)
    {
        var process = Start(fileName, env);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await WaitForHttpOkAsync(readyUrl, readyTimeout, cancellationToken);
        }
        catch
        {
            KillIfRunning(process);
            throw;
        }

        var timer = Stopwatch.StartNew();
        await SendSigtermAsync(process, cancellationToken);

        using var shutdownDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        shutdownDeadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(shutdownDeadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillIfRunning(process);
            throw new TimeoutException($"{fileName} did not exit within 30 s of SIGTERM");
        }

        timer.Stop();

        return new Result(
            process.ExitCode,
            await stdout,
            await stderr,
            timer.Elapsed);
    }

    private static async Task WaitForHttpOkAsync(string url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync(url, cancellationToken);
                if ((int)response.StatusCode == 200)
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                last = exception;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException($"the host did not answer {url} with 200 within {timeout} (last: {last?.Message})");
    }

    private static async Task SendSigtermAsync(Process process, CancellationToken cancellationToken)
    {
        using var kill = Process.Start("kill", ["-TERM", process.Id.ToString(CultureInfo.InvariantCulture)])!;
        await kill.WaitForExitAsync(cancellationToken);
    }

    /// <summary>Kills a host that is still running; one that exited before the kill needs nothing.</summary>
    /// <param name="process">The host process.</param>
    private static void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // The host exited between the check and the kill, so there is nothing to stop.
        }
    }
}
