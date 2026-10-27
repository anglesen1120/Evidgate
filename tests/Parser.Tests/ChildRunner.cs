using System.Diagnostics;
using System.Text;

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
/// </summary>
internal static class ChildRunner
{
    /// <summary>A child run that has not finished in this long is a hang, not a slow parse.</summary>
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    private static readonly string ChildDirectory = LocateChildDirectory();

    private static readonly string ChildAssembly = LocateChildFile("Evidgate.Parser.dll");

    private static readonly string ChildRuntimeConfig = LocateChildFile("Evidgate.Parser.runtimeconfig.json");

    /// <summary>Runs the child over <paramref name="stdin"/> with the given arguments.</summary>
    /// <param name="stdin">The bytes the child reads as its artifact.</param>
    /// <param name="arguments">The child's argument vector.</param>
    /// <returns>The finished run: exit code and both streams.</returns>
    public static async Task<ChildRun> RunAsync(byte[] stdin, params string[] arguments)
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

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("could not start the parser child process");
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        using var timeout = new CancellationTokenSource(RunTimeout);

        // Both pipes are drained before stdin is written: a child that writes while the test writes could
        // otherwise fill a pipe buffer and deadlock.
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdout, timeout.Token);
        var stderrTask = process.StandardError.BaseStream.CopyToAsync(stderr, timeout.Token);

        try
        {
            await process.StandardInput.BaseStream.WriteAsync(stdin, timeout.Token);
            await process.StandardInput.BaseStream.FlushAsync(timeout.Token);
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

    private static string LocateChildFile(string fileName)
    {
        var path = Path.Combine(ChildDirectory, fileName);

        return File.Exists(path)
            ? path
            : throw new InvalidOperationException(
                $"'{path}' is missing: the parser child must be built in this configuration before these tests run");
    }

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
