using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Shouldly;
using Xunit;

namespace Parser.Tests;

/// <summary>
/// 06.3.6-T1.3 AC2 (06.3.6 §7, §9 <c>Child_AppliesConfiguredLimits</c>): the child applies the OS limits the
/// launcher asks for — <c>RLIMIT_CPU</c>, <c>RLIMIT_FSIZE</c> and <c>RLIMIT_CORE = 0</c> — to itself before it
/// reads a byte of stdin, and applies none when the launcher asks for none.
/// <para>
/// The effective values are read back from <c>/proc/&lt;pid&gt;/limits</c> while the child is blocked on its
/// stdin pipe, so this asserts what the kernel enforces rather than what the child intended; the run then finishes
/// normally, which proves the limits did not break the parse. <c>/proc</c> exists on Linux only, so the applied
/// half skips with the reason on any other host (<c>Assert.Skip</c>, xunit v3 dynamic skip — never a silent pass)
/// while the argument validation runs everywhere. The contract shape is the same on both hosts the child ships to:
/// <c>setrlimit(2)</c> with <c>rlim_t</c> fields and the resource numbers CPU = 0, FSIZE = 1, CORE = 4.
/// </para>
/// </summary>
public sealed class ChildLimitsTests
{
    /// <summary>The fixed word the child writes when a requested limit could not be applied (exit 3).</summary>
    private const string LimitWord = "evidgate-parser:";

    /// <summary>A generous cap: the fixtures here are bytes, and the cap is not what this class varies.</summary>
    private const string OneMebibyte = "1048576";

    /// <summary>
    /// A CPU budget far above this run's use. It is applied for real (the point of the test), so it must not be a
    /// value a loaded build host could reach: 60 CPU-seconds is minutes of wall clock on any parse this file ships.
    /// </summary>
    private const string PlentyOfCpuSeconds = "60";

    /// <summary>The CPU budget the applied-limits test pins in <c>/proc</c>; small enough to recognise at a glance.</summary>
    private const string SevenCpuSeconds = "7";

    /// <summary>A tiny valid document for the run that must still finish under the limits.</summary>
    private const string SampleJson = """{"report":"sample","rows":[1,2,3]}""";

    /// <summary>The kernel's row names, as <c>/proc/&lt;pid&gt;/limits</c> prints them.</summary>
    private const string CpuLimitName = "Max cpu time";
    private const string FileSizeLimitName = "Max file size";
    private const string CoreLimitName = "Max core file size";

    /// <summary>The three rows this class reads back; the others in the file are the host's business.</summary>
    private static readonly string[] LimitNames = [CpuLimitName, FileSizeLimitName, CoreLimitName];

    private const int LimitPollAttempts = 200;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Every argument vector the resource switches must refuse.</summary>
    public static TheoryData<string[]> RefusedResourceArguments
    {
        get
        {
            var data = new TheoryData<string[]>();

            foreach (var arguments in RefusedResources)
            {
                data.Add(arguments);
            }

            return data;
        }
    }

    /// <summary>
    /// The refused resource vectors, held once. The child parses strictly and totally, so a switch that carries a
    /// value outside its range, a repeated switch, a missing value or a value that is not a decimal integer is a
    /// usage error like any other — never a run with a different limit. <c>--cpu-seconds 0</c> is refused even
    /// though <c>--max-file-bytes 0</c> is legal: zero CPU seconds would kill the child on its first tick.
    /// </summary>
    private static readonly string[][] RefusedResources =
    [
        ["--cpu-seconds", "0"],
        ["--cpu-seconds", "-1"],
        ["--cpu-seconds"],
        ["--cpu-seconds", "abc"],
        ["--cpu-seconds", "1.5"],
        ["--cpu-seconds", "1", "--cpu-seconds", "2"],
        ["--cpu-seconds", "--max-file-bytes", "1"],
        ["--max-file-bytes", "-1"],
        ["--max-file-bytes"],
        ["--max-file-bytes", "abc"],
        ["--max-file-bytes", "1", "--max-file-bytes", "2"],
        ["--max-file-bytes", "--cpu-seconds", "1"],
    ];

    /// <summary>The accepted resource vectors, including the zero file cap the production run uses.</summary>
    public static TheoryData<string[]> AcceptedResourceArguments
    {
        get
        {
            var data = new TheoryData<string[]>();

            foreach (var arguments in AcceptedResources)
            {
                data.Add(arguments);
            }

            return data;
        }
    }

    private static readonly string[][] AcceptedResources =
    [
        ["--cpu-seconds", PlentyOfCpuSeconds],
        ["--max-file-bytes", "0"],
        ["--cpu-seconds", PlentyOfCpuSeconds, "--max-file-bytes", "0"],
        ["--max-file-bytes", "4096", "--cpu-seconds", PlentyOfCpuSeconds],
    ];

    [Theory]
    [MemberData(nameof(RefusedResourceArguments))]
    public async Task Resource_Switch_Errors_Exit_2_With_Nothing_On_Stdout(string[] resourceArguments)
    {
        var run = await ChildRunner.RunAsync(
            Encoding.UTF8.GetBytes(SampleJson),
            ["--profile", "json", "--max-bytes", OneMebibyte, .. resourceArguments]);

        // Same contract as every other refused vector: exit 2, no record, one fixed line. The child never says
        // which value it refused, so a hostile launcher cannot make it echo one.
        run.ExitCode.ShouldBe(2, $"stderr: {run.Stderr}");
        run.Stdout.ShouldBeEmpty();
        run.Stderr.ShouldStartWith(LimitWord);
        run.Stderr.TrimEnd('\n').ShouldNotContain("\n");
    }

    [Theory]
    [MemberData(nameof(AcceptedResourceArguments))]
    public async Task Accepted_Resource_Switches_Complete_The_Run(string[] resourceArguments)
    {
        var run = await ChildRunner.RunAsync(
            Encoding.UTF8.GetBytes(SampleJson),
            ["--profile", "json", "--max-bytes", OneMebibyte, .. resourceArguments]);

        // The limits are applied on the way in and the parse still finishes: a marker follows the one document
        // record, and nothing reached stderr. `--max-file-bytes 0` is the interesting one — the child may write no
        // file at all, and its stdout is a pipe, not a file.
        run.ExitCode.ShouldBe(0, $"stderr: {run.Stderr}");
        run.Stderr.ShouldBeEmpty();

        var lines = run.Lines;
        lines.Count.ShouldBe(2);
        lines[^1].ShouldBe("""{"done":true,"count":1}""");
    }

    /// <summary>
    /// AC2: with <c>--cpu-seconds 7 --max-file-bytes 0</c> the child's own process carries those exact limits —
    /// soft and hard — plus <c>RLIMIT_CORE = 0</c>, because a limited run must never dump the artifact it is about
    /// to read. Read from <c>/proc/&lt;pid&gt;/limits</c> while the child is blocked on stdin, so the kernel's
    /// enforced values are what is asserted, and then finished normally to prove the limits do not change the
    /// verdict.
    /// </summary>
    [Fact]
    public async Task Child_AppliesConfiguredLimits()
    {
        RequireLinux("the child's self-applied RLIMIT_CPU/RLIMIT_FSIZE/RLIMIT_CORE");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(RunTimeout);

        await using var child = new ChildSession(
            ["--profile", "json", "--max-bytes", OneMebibyte, "--cpu-seconds", SevenCpuSeconds, "--max-file-bytes", "0"]);

        var cpu = await WaitForLimitRowAsync(child.Pid, CpuLimitName, soft => soft == SevenCpuSeconds, timeout.Token);

        // Soft and hard are the same value: the child has no headroom to raise into, so what the launcher measured
        // is what the kernel enforces for the whole run.
        cpu.ShouldBe((SevenCpuSeconds, SevenCpuSeconds));
        (await WaitForLimitRowAsync(child.Pid, FileSizeLimitName, soft => soft == "0", timeout.Token))
            .ShouldBe(("0", "0"));
        (await WaitForLimitRowAsync(child.Pid, CoreLimitName, soft => soft == "0", timeout.Token))
            .ShouldBe(("0", "0"));

        // The run itself is unaffected: the same document, the same one-record stream and the same marker.
        await child.WriteStdinAsync(Encoding.UTF8.GetBytes(SampleJson), timeout.Token);

        var run = await child.FinishAsync(timeout.Token);

        run.ExitCode.ShouldBe(0, $"stderr: {run.Stderr}");
        run.Stderr.ShouldBeEmpty();
        run.Lines[^1].ShouldBe("""{"done":true,"count":1}""");
    }

    /// <summary>
    /// The negative half of AC2: without either switch the child applies nothing at all. The values a child reads
    /// back must therefore be exactly the ones this test's own process carries — the limits it inherited — which is
    /// what the in-process tests and a developer run rely on. Comparing against <c>/proc/self/limits</c> rather
    /// than against "unlimited" is what makes the assertion true on a host (or CI image) that starts with a
    /// finite cap already.
    /// </summary>
    [Fact]
    public async Task Child_Without_Switches_Applies_No_Limit()
    {
        RequireLinux("the inherited rlimits a child without the resource switches reads back");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(RunTimeout);

        var inherited = LimitRows(
            ReadLimitsFile("/proc/self/limits")
            ?? throw new InvalidOperationException("this process must expose its own limits to prove inheritance"));

        await using var child = new ChildSession(["--profile", "json", "--max-bytes", OneMebibyte]);

        foreach (var name in LimitNames)
        {
            inherited.ShouldContainKey(name);

            var observed = await WaitForLimitRowAsync(child.Pid, name, _ => true, timeout.Token);

            observed.ShouldBe(inherited[name], $"'{name}' must be inherited unchanged when no switch asks for a limit");
        }

        // A run with no switches is not accidentally a limited one: every row above already equals what this
        // process carries, and a 7-second CPU budget would not survive that comparison on an unlimited host.
        await child.WriteStdinAsync(Encoding.UTF8.GetBytes(SampleJson), timeout.Token);

        var run = await child.FinishAsync(timeout.Token);

        run.ExitCode.ShouldBe(0, $"stderr: {run.Stderr}");
        run.Lines[^1].ShouldBe("""{"done":true,"count":1}""");
    }

    /// <summary>
    /// The environment half of the launcher contract (hand-over Notes <c>Environment</c>): the child adds no
    /// variable of its own, so a launcher that hands it a scrubbed environment and reads
    /// <c>/proc/&lt;pid&gt;/environ</c> sees exactly what it passed — which is how 06.3.6-T1.3 proves the
    /// allow-list (only <c>DOTNET_GCHeapHardLimit</c> plus what the runtime needs) reached the child and nothing
    /// else did.
    /// </summary>
    [Fact]
    public async Task Child_SeesOnlyTheEnvironmentTheLauncherPassed()
    {
        RequireLinux("a process's environment read back from /proc/<pid>/environ");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(RunTimeout);

        // The two variables the runtime host itself needs to find the shared framework, and one marker of our own:
        // nothing else is passed, so anything else in the child's environ came from the runtime rather than from
        // the launcher.
        var passed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["HOME"] = Environment.GetEnvironmentVariable("HOME") ?? "/tmp",
            ["EVIDGATE_LIMITS_PROBE"] = "1",
        };

        await using var child = new ChildSession(
            ["--profile", "json", "--max-bytes", OneMebibyte],
            startInfo =>
            {
                startInfo.Environment.Clear();
                foreach (var (name, value) in passed)
                {
                    startInfo.Environment[name] = value;
                }
            });

        // The limits row proves the process is up and its /proc entry readable; the environ file then holds the
        // environment as it was at exec time.
        await WaitForLimitRowAsync(child.Pid, CpuLimitName, _ => true, timeout.Token);

        var environment = await ReadEnvironmentAsync(child.Pid, timeout.Token);
        var names = environment.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Split('=', 2)[0])
            .Order(StringComparer.Ordinal)
            .ToArray();

        names.ShouldBe(
            passed.Keys.Order(StringComparer.Ordinal).ToArray(),
            "the child must see the launcher's environment and no other variable");

        await child.WriteStdinAsync(Encoding.UTF8.GetBytes(SampleJson), timeout.Token);

        var run = await child.FinishAsync(timeout.Token);
        run.ExitCode.ShouldBe(0, $"stderr: {run.Stderr}");
    }

    /// <summary>Skips a Linux-only assertion with the reason and the host it was reached from.</summary>
    /// <param name="subject">What is being asserted, named in the skip reason.</param>
    private static void RequireLinux(string subject)
    {
        if (OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.Skip(
            $"{subject} can only be measured on Linux: this host is {RuntimeInformation.OSDescription}, which has "
            + "no /proc/<pid>/limits, so the child's applied rlimits cannot be read back here. The argument "
            + "validation in this class runs on every host; the applied values are asserted on the Linux container "
            + "run (06.3.6-T1.3). Probe result: skipped, not passed.");
    }

    /// <summary>
    /// The (soft, hard) values of one <c>/proc/&lt;pid&gt;/limits</c> row, polled until <paramref name="isReady"/>
    /// accepts the soft value or the bounded attempt count runs out.
    /// </summary>
    /// <param name="pid">The child's process id.</param>
    /// <param name="name">The row's name as the kernel prints it.</param>
    /// <param name="isReady">True when the row shows the value the test is waiting for.</param>
    /// <param name="cancellationToken">Ends the wait when the test is cancelled.</param>
    /// <returns>The observed soft and hard values.</returns>
    private static async Task<(string Soft, string Hard)> WaitForLimitRowAsync(
        int pid,
        string name,
        Func<string, bool> isReady,
        CancellationToken cancellationToken)
    {
        // The limits are applied a few milliseconds after start (the runtime has to come up first) and the child
        // then blocks on stdin, so the row appears in this window and stays. An unreadable or absent file is the
        // process not being there yet, which is what the attempt bound waits out.
        var observed = string.Empty;

        for (var attempt = 0; attempt < LimitPollAttempts; attempt++)
        {
            var text = ReadLimitsFile(string.Format(CultureInfo.InvariantCulture, "/proc/{0}/limits", pid));
            if (text is not null && LimitRows(text).TryGetValue(name, out var row))
            {
                observed = row.Soft;

                if (isReady(row.Soft))
                {
                    return row;
                }
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new Xunit.Sdk.XunitException(
            $"'{name}' did not reach the expected value within {LimitPollAttempts * PollInterval.TotalSeconds} s; "
            + $"last observed soft value: '{observed}'");
    }

    /// <summary>The file's text, or <see langword="null"/> when it does not exist (yet) or cannot be read.</summary>
    /// <param name="path">The absolute path.</param>
    private static string? ReadLimitsFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The (soft, hard) pair of every row in a <c>/proc/&lt;pid&gt;/limits</c> file, keyed by the row's name.
    /// The kernel prints the name, the two values and a unit in aligned columns; the name itself contains spaces,
    /// so each known name is matched as a prefix rather than by splitting on the first column.
    /// </summary>
    /// <param name="text">The file's text.</param>
    /// <returns>The rows, keyed by name.</returns>
    private static Dictionary<string, (string Soft, string Hard)> LimitRows(string text)
    {
        var rows = new Dictionary<string, (string Soft, string Hard)>(StringComparer.Ordinal);

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');

            foreach (var name in LimitNames)
            {
                if (!trimmed.StartsWith(name, StringComparison.Ordinal))
                {
                    continue;
                }

                var values = trimmed[name.Length..]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (values.Length >= 2)
                {
                    rows[name] = (values[0], values[1]);
                }
            }
        }

        return rows;
    }

    /// <summary>A process's environment exactly as the kernel recorded it (NUL separated), read via /proc.</summary>
    /// <param name="pid">The process id.</param>
    /// <param name="cancellationToken">Ends the wait when the test is cancelled.</param>
    /// <returns>The environment block's text.</returns>
    private static async Task<string> ReadEnvironmentAsync(int pid, CancellationToken cancellationToken)
    {
        var path = string.Format(CultureInfo.InvariantCulture, "/proc/{0}/environ", pid);

        for (var attempt = 0; attempt < LimitPollAttempts; attempt++)
        {
            try
            {
                return await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"'{path}' was not readable within {LimitPollAttempts * PollInterval.TotalSeconds} s, so the child's "
            + "environment cannot be asserted on this host");
    }

    /// <summary>
    /// One running child whose stdin stays open, so the test can read the process's <c>/proc</c> entries while it
    /// is blocked in its read, then finish the run and assert its verdict.
    /// </summary>
    private sealed class ChildSession : IAsyncDisposable
    {
        private readonly Process process;
        private readonly MemoryStream stdout = new();
        private readonly MemoryStream stderr = new();
        private readonly Thread stdoutDrain;
        private readonly Thread stderrDrain;

        /// <summary>Starts the child with all three pipes redirected.</summary>
        /// <param name="arguments">The child's argument vector.</param>
        /// <param name="configure">Changes the start info (the environment test scrubs it) before the start.</param>
        public ChildSession(string[] arguments, Action<ProcessStartInfo>? configure = null)
        {
            var startInfo = ChildRunner.CreateStartInfo(arguments);
            configure?.Invoke(startInfo);

            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("could not start the parser child process");

            // Both pipes are drained from the start: the child writes only when it is done, but a read started
            // later would race a fast exit. Each drain is its own thread rather than a Task, because a task handed
            // back to another method and awaited there is exactly the pattern VSTHRD003 forbids.
            stdoutDrain = StartDrain(process.StandardOutput.BaseStream, stdout);
            stderrDrain = StartDrain(process.StandardError.BaseStream, stderr);
        }

        /// <summary>The child's process id, the <c>/proc</c> key for its limits and environment.</summary>
        public int Pid => process.Id;

        /// <summary>Writes one artifact to the child's stdin without closing it.</summary>
        /// <param name="content">The bytes.</param>
        /// <param name="cancellationToken">Ends the write when the test is cancelled.</param>
        public async Task WriteStdinAsync(byte[] content, CancellationToken cancellationToken)
        {
            await process.StandardInput.BaseStream.WriteAsync(content, cancellationToken);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
        }

        /// <summary>Closes stdin, waits for the child and returns what crossed its pipes.</summary>
        /// <param name="cancellationToken">Ends the wait when the test is cancelled.</param>
        /// <returns>The finished run.</returns>
        public async Task<ChildRun> FinishAsync(CancellationToken cancellationToken)
        {
            process.StandardInput.Close();

            await process.WaitForExitAsync(cancellationToken);

            // The exit closed both pipes, so both drains are at their end and the joins return immediately.
            stdoutDrain.Join();
            stderrDrain.Join();

            return new ChildRun(process.ExitCode, stdout.ToArray(), Encoding.UTF8.GetString(stderr.ToArray()));
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // The child exited between the check and the kill: nothing is left to kill.
            }

            await process.WaitForExitAsync();

            stdoutDrain.Join();
            stderrDrain.Join();

            process.Dispose();
        }

        /// <summary>Copies one pipe into memory on its own thread until the pipe closes.</summary>
        /// <param name="source">The child's pipe.</param>
        /// <param name="sink">Where the bytes are kept for the assertions.</param>
        /// <returns>The started thread.</returns>
        private static Thread StartDrain(Stream source, Stream sink)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    source.CopyTo(sink);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                    // A closed pipe is the end of the stream, not a test failure: the child's verdict is its exit
                    // code and the bytes already copied.
                }
            })
            {
                IsBackground = true,
                Name = "parser-child-drain",
            };

            thread.Start();

            return thread;
        }
    }
}
