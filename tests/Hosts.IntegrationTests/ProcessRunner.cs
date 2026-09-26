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
            process.Kill();
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
            process.Kill();
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
}
