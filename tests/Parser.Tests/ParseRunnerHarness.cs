using System.Diagnostics;
using System.Globalization;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Xunit;

namespace Parser.Tests;

/// <summary>
/// Shared plumbing for the 06.3.6-T1.3 parent-runner tests: a private temp root per test, the two launch shapes —
/// the real built child through the production command, and a scripted <c>/bin/sh</c> child through the same seam —
/// and the small observations a process test needs (pid liveness, what is left in the temp root, the files a
/// scripted child writes beside its own run directory).
/// <para>
/// A scripted child writes to <c>../name</c>: its working directory is the run's private temp directory under
/// <see cref="TempRoot"/>, so the parent directory is the one place a test can read afterwards — the runner deletes
/// the run directory itself on every path, which the lifecycle tests assert.
/// </para>
/// </summary>
internal sealed class ParseRunnerHarness : IDisposable
{
    private const string PosixShell = "/bin/sh";

    public ParseRunnerHarness()
    {
        TempRoot = Path.Combine(
            Path.GetTempPath(),
            "evidgate-parse-runner-test-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(TempRoot);
    }

    /// <summary>The root every run's private directory is created under; the test's own cleanup scope.</summary>
    public string TempRoot { get; }

    /// <summary>Skips the calling test on Windows, with the reason the capability is missing.</summary>
    /// <param name="capability">What the test needs, named in the skip.</param>
    public static void RequirePosixShell(string capability)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(
                $"{capability} needs a POSIX shell and Unix signals; this harness starts scripted children as "
                + $"{PosixShell} -c …, which Windows has no equivalent of. Skipped, not passed.");
        }
    }

    /// <summary>The options a test runs under: this harness's temp root, plus whatever the test changes.</summary>
    /// <param name="configure">An optional mutation of the options.</param>
    /// <returns>The options instance.</returns>
    public ParseRunnerOptions Options(Action<ParseRunnerOptions>? configure = null)
    {
        var options = new ParseRunnerOptions { TempRoot = TempRoot };
        configure?.Invoke(options);

        return options;
    }

    /// <summary>A runner whose child is the given shell script.</summary>
    /// <param name="script">The script body, run as <c>/bin/sh -c script</c>.</param>
    /// <param name="configure">An optional mutation of the options.</param>
    /// <returns>The runner; the caller disposes it.</returns>
    public ChildProcessParseRunner Script(string script, Action<ParseRunnerOptions>? configure = null)
    {
        RequirePosixShell("a scripted child");

        return new ChildProcessParseRunner(
            Options(configure),
            _ => new ChildCommand(PosixShell, ["-c", script], string.Empty));
    }

    /// <summary>A runner whose child is an arbitrary command, for the paths a shell script cannot express.</summary>
    /// <param name="command">The executable and its leading arguments.</param>
    /// <param name="configure">An optional mutation of the options.</param>
    /// <returns>The runner; the caller disposes it.</returns>
    public ChildProcessParseRunner Spawn(ChildCommand command, Action<ParseRunnerOptions>? configure = null) =>
        new(Options(configure), _ => command);

    /// <summary>A runner whose child is the real built <c>Evidgate.Parser</c>, launched the production way.</summary>
    /// <param name="configure">An optional mutation of the options.</param>
    /// <returns>The runner; the caller disposes it.</returns>
    public ChildProcessParseRunner RealChild(Action<ParseRunnerOptions>? configure = null)
    {
        var assembly = ChildRunner.ChildAssemblyPath();

        return new ChildProcessParseRunner(
            Options(options =>
            {
                options.ChildPath = assembly;
                configure?.Invoke(options);
            }),
            ChildCommandLine.ForOptions);
    }

    /// <summary>Runs one request over an in-memory artifact.</summary>
    /// <param name="runner">The runner under test.</param>
    /// <param name="profile">Which arm of the profile the child runs.</param>
    /// <param name="maxBytes">The run's byte cap.</param>
    /// <param name="input">The artifact's bytes.</param>
    /// <param name="mediaType">The declared media type, when the test wants the sniff to run.</param>
    /// <param name="cancellationToken">The caller's cancellation.</param>
    /// <returns>The result.</returns>
    public static Task<ParseRunResult> RunAsync(
        ChildProcessParseRunner runner,
        ParseRunnerProfile profile,
        long maxBytes,
        byte[] input,
        string? mediaType = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(
            new ParseRunRequest(profile, maxBytes, mediaType, new MemoryStream(input, writable: false)),
            cancellationToken);

    /// <summary>The path a scripted child's <c>../name</c> write lands on.</summary>
    /// <param name="name">The file's name.</param>
    /// <returns>The absolute path under the temp root.</returns>
    public string Side(string name) => Path.Combine(TempRoot, name);

    /// <summary>The text a scripted child wrote beside its run directory, or <see langword="null"/>.</summary>
    /// <param name="name">The file's name.</param>
    /// <returns>The file's content.</returns>
    public string? ReadSide(string name) => File.Exists(Side(name)) ? File.ReadAllText(Side(name)) : null;

    /// <summary>The non-empty lines of a scripted child's side file, in the order it wrote them.</summary>
    /// <param name="name">The file's name.</param>
    /// <returns>The lines; empty when the file is absent.</returns>
    public IReadOnlyList<string> SideLines(string name) =>
        (ReadSide(name) ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Parses a <c>NAME=VALUE</c> dump into a dictionary.</summary>
    /// <param name="name">The dump's file name.</param>
    /// <returns>The variables, keyed by name.</returns>
    public Dictionary<string, string> ReadEnvironmentDump(string name)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in SideLines(name))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);

            if (separator > 0)
            {
                environment[line[..separator]] = line[(separator + 1)..];
            }
        }

        return environment;
    }

    /// <summary>The run directories the runner left behind under <see cref="TempRoot"/>.</summary>
    /// <returns>The directory paths; the lifecycle tests assert this is empty.</returns>
    public IReadOnlyList<string> LeftoverRunDirectories() => Directory.GetDirectories(TempRoot);

    /// <summary>Reads a pid a scripted child wrote to a side file.</summary>
    /// <param name="name">The side file's name.</param>
    /// <returns>The pid.</returns>
    public int ReadPid(string name) =>
        int.Parse((ReadSide(name) ?? string.Empty).Trim(), CultureInfo.InvariantCulture);

    /// <summary>Whether a process is still running (a zombie counts as gone once it is reaped).</summary>
    /// <param name="pid">The process id.</param>
    /// <returns>True when the process exists and has not exited.</returns>
    public static bool IsAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);

            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Waits until a condition holds, or fails the test naming what never happened.</summary>
    /// <param name="condition">The condition to poll.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="because">The failure message.</param>
    /// <returns>Nothing; the test fails when the timeout expires.</returns>
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        Assert.Fail($"{because} (waited {timeout.TotalSeconds:0.#} s)");
    }

    /// <summary>Waits for a scripted child to report its own pid, then returns it.</summary>
    /// <param name="name">The side file the child writes its pid to.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>The pid; the test fails when the file never appears.</returns>
    public async Task<int> WaitForPidAsync(string name, TimeSpan timeout)
    {
        await WaitUntilAsync(() => File.Exists(Side(name)), timeout, $"the child never wrote {name}");

        return ReadPid(name);
    }

    /// <summary>The side-file prefix a script uses for its own directory's parent.</summary>
    /// <returns><c>../</c>, spelled for the script.</returns>
    public static string SidePrefix => "../";

    /// <inheritdoc />
    public void Dispose() => ParseRunTempDirectory.Delete(TempRoot);
}
