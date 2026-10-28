using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Xunit;

namespace Parser.Tests;

/// <summary>One finished child run: its exit code, the exact stdout bytes and the decoded stderr.</summary>
/// <param name="ExitCode">The process exit code (0 streamed, 2 usage, 3 internal).</param>
/// <param name="StdoutBytes">The raw stdout bytes, kept so a test can assert byte identity across runs.</param>
/// <param name="Stderr">The decoded stderr text (fixed words only, on the child's contract).</param>
internal sealed record ChildRun(int ExitCode, byte[] StdoutBytes, string Stderr)
{
    /// <summary>The decoded stdout text.</summary>
    public string Stdout => Encoding.UTF8.GetString(StdoutBytes);

    /// <summary>
    /// The NDJSON lines, without the empty element a trailing <c>\n</c> produces. An interior empty line is
    /// dropped too — a blank line is itself a framing violation, and the tests that care assert on the raw text.
    /// </summary>
    public IReadOnlyList<string> Lines =>
        Stdout.Split('\n').Where(line => line.Length > 0).ToArray();
}

/// <summary>
/// Launches the built <c>Evidgate.Parser</c> child as a real process — <c>dotnet exec --runtimeconfig … dll …</c>,
/// the same way the Migrator tests start a host — and captures both pipes. The child is located from this project's
/// own output directory, so the configuration (Debug or Release) always matches the one the test build used.
/// <para>
/// Every run is bounded: the deadline, or the test's own cancellation, ends in
/// <see cref="Process.Kill(bool)"/> of the whole tree in a <see langword="finally"/>, so a wedged or cancelled
/// child can never outlive the suite holding its pipes open.
/// </para>
/// </summary>
internal static class ChildRunner
{
    /// <summary>A child run that has not finished in this long is a hang, not a slow parse.</summary>
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    private static readonly string ChildDirectory = LocateChildDirectory();

    private static readonly string ChildAssembly = LocateChildFile("Evidgate.Parser.dll");

    private static readonly string ChildRuntimeConfig = LocateChildFile("Evidgate.Parser.runtimeconfig.json");

    /// <summary>Runs the child over <paramref name="stdin"/> with the given arguments, writing stdin in one block.</summary>
    /// <param name="stdin">The bytes the child reads as its artifact.</param>
    /// <param name="arguments">The child's argument vector.</param>
    /// <returns>The finished run: exit code and both streams.</returns>
    public static Task<ChildRun> RunAsync(byte[] stdin, params string[] arguments) =>
        RunCoreAsync(stdin, 0, arguments);

    /// <summary>
    /// Runs the child over <paramref name="stdin"/> written in blocks of <paramref name="chunkBytes"/> bytes, one
    /// flush per block — the shape a parent pipe produces when it dribbles an object out, and the one the child's
    /// read must coalesce.
    /// </summary>
    /// <param name="chunkBytes">How many bytes each stdin write carries (positive).</param>
    /// <param name="stdin">The bytes the child reads as its artifact.</param>
    /// <param name="arguments">The child's argument vector.</param>
    /// <returns>The finished run: exit code and both streams.</returns>
    public static Task<ChildRun> RunFragmentedAsync(int chunkBytes, byte[] stdin, params string[] arguments)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkBytes);

        return RunCoreAsync(stdin, chunkBytes, arguments);
    }

    /// <summary>
    /// The command line that starts the built child with all three pipes redirected. A test that has to drive the
    /// process itself (to close a pipe mid-run) uses this, so the child's location never gets a second copy.
    /// </summary>
    /// <param name="arguments">The child's argument vector.</param>
    /// <returns>The start info, ready for <see cref="Process.Start(ProcessStartInfo)"/>.</returns>
    internal static ProcessStartInfo CreateStartInfo(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(ChildRuntimeConfig);
        startInfo.ArgumentList.Add(ChildAssembly);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task<ChildRun> RunCoreAsync(byte[] stdin, int chunkBytes, string[] arguments)
    {
        using var process = Process.Start(CreateStartInfo(arguments))
            ?? throw new InvalidOperationException("could not start the parser child process");
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(RunTimeout);

        try
        {
            // Both pipes are drained before stdin is written: a child that writes while the test writes could
            // otherwise fill a pipe buffer and deadlock.
            var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdout, timeout.Token);
            var stderrTask = process.StandardError.BaseStream.CopyToAsync(stderr, timeout.Token);

            try
            {
                await WriteStdinAsync(process.StandardInput.BaseStream, stdin, chunkBytes, timeout.Token);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The child may stop reading before the last byte — a usage error never reads stdin, and a size
                // rejection stops at the cap — so a broken pipe here is the child's verdict, not a test failure.
            }

            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdoutTask, stderrTask);

            return new ChildRun(process.ExitCode, stdout.ToArray(), Encoding.UTF8.GetString(stderr.ToArray()));
        }
        finally
        {
            // The deadline, a cancelled test, or an exception above: nothing of this child may survive the run.
            Kill(process);
        }
    }

    private static async Task WriteStdinAsync(Stream input, byte[] content, int chunkBytes, CancellationToken cancellationToken)
    {
        if (chunkBytes == 0)
        {
            await input.WriteAsync(content, cancellationToken);
            await input.FlushAsync(cancellationToken);

            return;
        }

        for (var offset = 0; offset < content.Length; offset += chunkBytes)
        {
            var length = Math.Min(chunkBytes, content.Length - offset);
            await input.WriteAsync(content.AsMemory(offset, length), cancellationToken);

            // One flush per block: the child must see each block as its own read, which is what makes the
            // coalescing in StdinBuffer observable.
            await input.FlushAsync(cancellationToken);
        }
    }

    /// <summary>Kills the process tree of a child that is still running; a child that already exited needs nothing.</summary>
    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or AggregateException)
        {
            // The child exited between the check and the kill (InvalidOperationException), the OS refused a kill
            // for a process that is already gone (Win32Exception), or a tree kill lost a descendant mid-walk
            // (AggregateException): nothing is left to kill on any of these paths.
        }
    }

    private static string LocateChildFile(string fileName)
    {
        var path = Path.Combine(ChildDirectory, fileName);

        return File.Exists(path)
            ? path
            : throw new InvalidOperationException(
                $"'{path}' is missing: the parser child must be built in this configuration before these tests run");
    }

    /// <summary>
    /// The built child assembly's absolute path, for a test that starts the child through the parent runner
    /// (06.3.6-T1.3) instead of driving the pipes itself. One location rule serves both.
    /// </summary>
    /// <returns>The absolute path of <c>Evidgate.Parser.dll</c> in this configuration.</returns>
    internal static string ChildAssemblyPath() => ChildAssembly;

    private static string LocateChildDirectory()
    {
        // tests/Parser.Tests/bin/<configuration>/net10.0/ → the child's own bin/<configuration>/net10.0.
        var separator = Path.DirectorySeparatorChar;
        var marker = $"{separator}bin{separator}";
        var output = AppContext.BaseDirectory;
        var index = output.IndexOf(marker, StringComparison.Ordinal);

        if (index < 0)
        {
            throw new InvalidOperationException(
                $"cannot derive the build configuration from '{output}' (no bin segment)");
        }

        var configuration = output[(index + marker.Length)..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])[0];

        // The framework segment is the last one the test output carries (net10.0), so a target-framework change
        // does not need an edit here.
        var framework = Path.GetFileName(Path.TrimEndingDirectorySeparator(output));

        return Path.Combine(
            RepoPaths.RepoRoot,
            "src",
            "Evidgate.Parser",
            "bin",
            configuration,
            framework);
    }
}
