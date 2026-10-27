using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Xunit;

namespace Parser.Tests;

/// <summary>
/// 06.3.6-T1.1: measures the OS limits the parse child (06.3.6 §7, 00 §5.7 S5) depends on, on the machine that
/// runs the build: cgroup v2 controller delegation, <c>setrlimit(2)</c> through a launcher, the .NET GC heap
/// hard limit, and whether an unprivileged process can leave the network. DECISIONS §6 F14 records what the
/// design relies on and every measured value; these tests are the CI half of that row, so a runner that behaves
/// differently fails here instead of surfacing as a weak limit in production.
/// <para>
/// The probes are environment facts, not product behaviour, so a host that cannot express a fact skips with the
/// reason and the measured values in the message — never a silent pass (<c>Assert.Skip</c>, xunit v3 dynamic
/// skip). Everything is bounded (&lt; 10 s per probe), deterministic, and touches nothing but loopback.
/// </para>
/// <para>
/// The no-network convention guard (<c>tests/Shared/NoNetworkGuard.cs</c>, wired by <c>tests/Directory.Build.props</c>
/// for <c>*.UnitTests</c> only) does not apply to this project: it is not named <c>*.UnitTests</c> and it has to
/// open one loopback listener to prove that a network namespace cannot reach it.
/// </para>
/// </summary>
public sealed class SandboxLimitsProbeTests
{
    private const string CgroupRoot = "/sys/fs/cgroup";
    private const string ProcSelfCgroup = "/proc/self/cgroup";
    private const string ProcSelfLimits = "/proc/self/limits";

    /// <summary>The environment variable the runtime reads as the GC heap hard limit (hexadecimal bytes, 64-bit only).</summary>
    private const string HeapLimitVariable = "DOTNET_GCHeapHardLimit";

    /// <summary>128 MiB, hexadecimal: small enough to OOM in well under a second, far above what the test host needs to start.</summary>
    private const string HeapLimitHex = "0x8000000";
    private const long HeapLimitBytes = 134_217_728;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The per-cgroup interface files whose presence proves a controller is delegated to that cgroup.</summary>
    private static readonly string[] CgroupLimitFiles = ["memory.max", "cpu.max", "pids.max"];

    /// <summary>The controllers the child's ceilings are read from, so all three must be delegated to its cgroup.</summary>
    private static readonly string[] RequiredControllers = ["memory", "cpu", "pids"];

    /// <summary>The connect errnos a namespace child can report; any of them means the connect itself failed.</summary>
    private static readonly string[] ConnectFailureMarkers =
        ["Connection refused", "Network is unreachable", "No route to host", "Cannot assign requested address"];

    /// <summary>
    /// 06.3.6 §9 AC2 / DECISIONS F14: the parse child's own memory, CPU and PID ceilings come from the cgroup it
    /// runs in, so the cgroup v2 interface files must be visible *and readable* from inside that cgroup. In the
    /// worker container they are mounted read-only: the child can read its ceiling but cannot raise it.
    /// </summary>
    [Fact]
    public void CgroupV2_ExposesMemoryCpuAndPidsControllers_ToTheTestsOwnCgroup()
    {
        RequireLinux("cgroup v2 controller delegation");

        var controllersPath = Path.Combine(CgroupRoot, "cgroup.controllers");
        if (!File.Exists(controllersPath))
        {
            Assert.Skip(
                $"{controllersPath} does not exist on this host, so cgroup v2 is not mounted and the child's " +
                "memory ceiling must come from an RLIMIT_AS launcher (DECISIONS F14). Probe result: skipped, " +
                "not passed.");
        }

        var controllers = File.ReadAllText(controllersPath)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var missingControllers = RequiredControllers
            .Where(expected => !controllers.Contains(expected, StringComparer.Ordinal))
            .ToArray();

        if (missingControllers.Length > 0)
        {
            Assert.Skip(
                $"root '{controllersPath}' does not offer '{string.Join(", ", missingControllers)}' (it lists " +
                $"'{string.Join(' ', controllers)}'), so the child's ceiling cannot be read from a cgroup here. " +
                "DECISIONS F14 relies on the container case, where the container is its own cgroup namespace root " +
                "and the root of that namespace delegates the controllers. Probe result: skipped, not passed.");
        }

        var ownDirectory = OwnCgroupDirectory();
        if (!Directory.Exists(ownDirectory))
        {
            Assert.Skip(
                $"the test's own cgroup directory '{ownDirectory}' (resolved from '{ProcSelfCgroup}') is not " +
                "readable here, so no controller is delegated to this leaf and the child's effective ceiling " +
                "cannot be read from this host. Probe result: skipped, not passed.");
        }

        var missing = CgroupLimitFiles
            .Where(file => !File.Exists(Path.Combine(ownDirectory, file)))
            .ToArray();

        if (missing.Length > 0)
        {
            Assert.Skip(
                $"the test's own cgroup '{ownDirectory}' does not expose '{string.Join(", ", missing)}', so no " +
                $"controller is delegated to this leaf and the child's effective ceiling cannot be read from " +
                $"here (root '{controllersPath}' lists '{string.Join(' ', controllers)}'). DECISIONS F14 relies " +
                "on the container case, where the container is its own cgroup namespace root. Probe result: " +
                "skipped, not passed.");
        }

        AssertMaxValue(Path.Combine(ownDirectory, "memory.max"));
        AssertMaxValue(Path.Combine(ownDirectory, "pids.max"));
        AssertMaxOrBandwidth(Path.Combine(ownDirectory, "cpu.max"));
    }

    /// <summary>
    /// 06.3.6 §7 "CPU via RLIMIT_CPU": a launcher sets both limits before exec and the child inherits them, which is
    /// how the runner applies CPU time and the coarse address-space bound where a launcher exists. The values are
    /// read back from <c>/proc/self/limits</c> *inside* the limited process, so this proves inheritance rather
    /// than the launcher's own state.
    /// </summary>
    [Fact]
    public async Task Prlimit_AppliesCpuAndAddressSpaceLimits_InheritedByTheLaunchedProcess()
    {
        RequireLinux("setrlimit(2) through prlimit");

        var prlimit = FindOnPath("prlimit");
        if (prlimit.Length == 0)
        {
            Assert.Skip(
                "'prlimit' (util-linux) is not on PATH, so RLIMIT_CPU/RLIMIT_AS cannot be applied by a launcher on " +
                "this host; the probe measures nothing (DECISIONS F14: the chiseled worker image carries no " +
                "prlimit at all, which is why the child sets its own rlimits). Probe result: skipped, not passed.");
        }

        var limitsReader = FindOnPath("cat");
        if (limitsReader.Length == 0)
        {
            Assert.Skip($"'cat' is not on PATH, so '{ProcSelfLimits}' cannot be read from inside the launched process");
        }

        // 4 GiB of address space: comfortably above the runtime's own floor (below ~3 GiB the .NET runtime fails to
        // start, DECISIONS F14) while still being a value the kernel rounds and reports exactly.
        const long addressSpaceBytes = 4_294_967_296;
        var (exitCode, standardOutput, standardError) = await RunBoundedAsync(
            prlimit,
            ["--cpu=5:5", $"--as={addressSpaceBytes.ToString(CultureInfo.InvariantCulture)}", "--", limitsReader, ProcSelfLimits],
            ProbeTimeout);

        exitCode.ShouldBe(0, $"'{prlimit} --cpu=5:5 --as={addressSpaceBytes}' must succeed; stderr: {Truncate(standardError)}");

        AssertLimitLine(standardOutput, "Max cpu time", 5);
        AssertLimitLine(standardOutput, "Max address space", addressSpaceBytes);
    }

    /// <summary>
    /// 06.3.6 §7 "DOTNET_GCHeapHardLimit as the first line": the heap ceiling is a runtime setting the parent passes
    /// in the child's environment — no privileges, no launcher — and the runtime enforces it by refusing to commit
    /// past it. The parent process is unbounded here, so a passing test also proves the limit travelled to the child
    /// and not to the test host.
    /// </summary>
    [Fact]
    public async Task GcHeapHardLimit_StopsAChildProcessAllocatingPastTheLimit()
    {
        if (GcHeapLimitProbeChild.IsProbeChild())
        {
            Assert.Skip("this process is the GC heap-limit probe child: its module initializer measures the heap and exits, so no test may run here");
        }

        RequireLinux("the GC heap hard limit of the parser child");

        var heapLimitUnset = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        heapLimitUnset.ShouldBeGreaterThan(
            HeapLimitBytes,
            "the test host must not already run under a 128 MiB heap hard limit, otherwise the child probe proves nothing");

        var (fileName, arguments) = ProbeChildCommand();
        var temporaryDirectory = Directory.CreateTempSubdirectory("evg-gcprobe-");
        try
        {
            var marker = Path.Combine(temporaryDirectory.FullName, "marker.txt");
            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            // A minimal environment: the probe measures the heap ceiling this parent sets, so no inherited
            // DOTNET_* setting — another heap cap, a startup hook, a GC mode — may travel with it. Only what
            // locates the shared runtime and what any child needs to start at all is kept.
            startInfo.Environment.Clear();
            CopyEnvironment(startInfo, "PATH", "HOME", "TMPDIR", "TEMP", "TMP");
            CopyEnvironment(
                startInfo,
                "DOTNET_ROOT",
                "DOTNET_ROOT(x64)",
                "DOTNET_ROOT(x86)",
                "DOTNET_ROOT(arm64)");

            startInfo.Environment[HeapLimitVariable] = HeapLimitHex;
            startInfo.Environment[GcHeapLimitProbeChild.MarkerVariable] = marker;

            // The nonce binds the two variables: a marker alone is not enough for the second process to become
            // the probe, so an ambient or stale marker variable can never end an ordinary test run in the module
            // initializer.
            startInfo.Environment[GcHeapLimitProbeChild.NonceVariable] = GcHeapLimitProbeChild.NonceFor(marker);

            var (exitCode, standardOutput, standardError) = await RunBoundedAsync(startInfo, ProbeTimeout);
            exitCode.ShouldBe(
                0,
                $"the probe child must reach its heap limit and report it, not crash; stdout: {Truncate(standardOutput)}; " +
                $"stderr: {Truncate(standardError)}");

            File.Exists(marker).ShouldBeTrue(
                $"the probe child, started as '{fileName}', must write '{marker}' from its module initializer; " +
                $"stdout: {Truncate(standardOutput)}; stderr: {Truncate(standardError)}");

            var observed = ParseMetricLines(
                await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken));
            observed["limit"].ShouldBe(
                HeapLimitBytes,
                FormattableString.Invariant($"'{HeapLimitVariable}={HeapLimitHex}' must reach the child as TotalAvailableMemoryBytes, not the host's {heapLimitUnset}"));
            observed["oom"].ShouldBe(
                1,
                "allocating past the hard limit must end in OutOfMemoryException, not a silent success");
            observed["allocated"].ShouldBeGreaterThan(
                HeapLimitBytes - (8 * 1024 * 1024),
                "the child must have allocated up to the ceiling before the runtime refused");
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// 06.3.6 §9 AC4 / DECISIONS F14: a network namespace is the kernel-level "no network" the runner tries first,
    /// and a fresh namespace has no listener and its loopback device down, so a connect that works in the plain
    /// child must fail in the namespace. Any failure to create the namespace at all — EPERM without
    /// <c>CAP_SYS_ADMIN</c>, EINVAL, a container that forbids it — is a fact this host cannot express, so the
    /// probe skips with the helper's own text instead of failing; only a connect that *succeeds* inside the
    /// namespace is a defect.
    /// </summary>
    [Fact]
    public async Task NewNetworkNamespace_CannotReachALoopbackListenerOfTheParent()
    {
        RequireLinux("network namespaces");

        var bash = FindOnPath("bash");
        if (bash.Length == 0)
        {
            Assert.Skip("'bash' is not on PATH, so the loopback connect probe (/dev/tcp) cannot run on this host");
        }

        var unshare = FindOnPath("unshare");
        if (unshare.Length == 0)
        {
            Assert.Skip("'unshare' (util-linux) is not on PATH, so a network namespace cannot be created on this host");
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
            string[] connect = ["-c", $"exec 3<>/dev/tcp/127.0.0.1/{port}"];

            var (plainExitCode, _, plainError) = await RunBoundedAsync(bash, connect, ProbeTimeout);
            plainExitCode.ShouldBe(
                0,
                $"a plain child must reach the listener the test opened on 127.0.0.1:{port}, otherwise the namespace " +
                $"result proves nothing; stderr: {Truncate(plainError)}");

            string[] privilegedArguments = ["--net", bash, .. connect];
            string[] unprivilegedArguments = ["-rn", bash, .. connect];
            (string Label, string[] Arguments)[] attempts =
            [
                ("unshare --net", privilegedArguments),
                ("unshare -rn", unprivilegedArguments),
            ];

            var refused = new List<string>();
            foreach (var (label, arguments) in attempts)
            {
                var (namespaceExitCode, _, namespaceError) = await RunBoundedAsync(unshare, arguments, ProbeTimeout);

                // A success means the child reached the listener the parent opened: it never left the test's
                // namespace, which is the one outcome this probe must fail on.
                namespaceExitCode.ShouldNotBe(
                    0,
                    $"{label}: a child in a fresh network namespace must not reach 127.0.0.1:{port} — the namespace " +
                    "has no listener and its loopback device is down. A success means the child never left the " +
                    "test's namespace.");

                if (ConnectFailureMarkers.Any(marker => namespaceError.Contains(marker, StringComparison.Ordinal)))
                {
                    // The namespace ran the child and the connect failed inside it: the isolation holds.
                    return;
                }

                // Anything else is the host refusing to create the namespace at all (EPERM, EINVAL, a missing
                // helper, a container without CAP_SYS_ADMIN): a fact this host cannot express, not a product
                // defect, so the next route is tried and, if every route refuses, the probe skips with the text.
                refused.Add($"{label}: {Truncate(namespaceError)}");
            }

            Assert.Skip(
                $"this host could not create a network namespace through any route ({string.Join(" | ", refused)}); " +
                "it needs CAP_SYS_ADMIN or an unprivileged user namespace, so DECISIONS F14 falls back to a " +
                "container with no network. Probe result: skipped, not passed.");
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Skips the probe on a host the parse child never runs on, naming the fact that is unmeasurable there.</summary>
    private static void RequireLinux(string measuredFact)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip(
                $"{measuredFact} is only observable on Linux; this host is '{Environment.OSVersion.Platform}'. " +
                "The parse child ships on the Linux chiseled runtime (DECISIONS F14). Probe result: skipped, not passed.");
        }
    }

    /// <summary>The directory cgroup v2 exposes for the calling process, honouring the cgroup namespace root.</summary>
    private static string OwnCgroupDirectory()
    {
        var line = File.ReadAllLines(ProcSelfCgroup)
            .FirstOrDefault(entry => entry.StartsWith("0::", StringComparison.Ordinal));

        if (line is null)
        {
            throw new InvalidOperationException($"'{ProcSelfCgroup}' must carry one '0::' cgroup v2 line");
        }

        var path = line["0::".Length..].Trim();
        return path.Length <= 1
            ? CgroupRoot
            : Path.Combine(CgroupRoot, path.Trim('/'));
    }

    private static void AssertMaxValue(string path)
    {
        var value = File.ReadAllText(path).Trim();
        if (string.Equals(value, "max", StringComparison.Ordinal))
        {
            return;
        }

        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            .ShouldBeTrue(FormattableString.Invariant($"'{path}' must hold 'max' or a byte count, not '{value}'"));
        parsed.ShouldBeGreaterThan(0);
    }

    private static void AssertMaxOrBandwidth(string path)
    {
        var tokens = File.ReadAllText(path)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        tokens.Length.ShouldBe(2, FormattableString.Invariant($"'{path}' must hold '$MAX $PERIOD', not '{string.Join(' ', tokens)}'"));

        if (!string.Equals(tokens[0], "max", StringComparison.Ordinal))
        {
            long.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quota)
                .ShouldBeTrue(FormattableString.Invariant($"'{path}' quota must be 'max' or a number, not '{tokens[0]}'"));
            quota.ShouldBeGreaterThan(0);
        }

        long.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var period)
            .ShouldBeTrue(FormattableString.Invariant($"'{path}' period must be a number, not '{tokens[1]}'"));
        period.ShouldBeGreaterThan(0);
    }

    /// <summary>Asserts the soft and hard value of one <c>/proc/self/limits</c> row; the unit is the last token.</summary>
    private static void AssertLimitLine(string limits, string name, long expected)
    {
        var line = limits.Split('\n')
            .Select(entry => entry.TrimEnd('\r'))
            .FirstOrDefault(entry => entry.TrimStart().StartsWith(name, StringComparison.Ordinal));

        if (line is null)
        {
            throw new InvalidOperationException($"'{name}' must appear in '{ProcSelfLimits}': {Truncate(limits)}");
        }

        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        tokens.Length.ShouldBeGreaterThanOrEqualTo(
            4,
            FormattableString.Invariant($"'{name}' needs a soft value, a hard value and a unit, got '{line.Trim()}'"));

        long.TryParse(tokens[^3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var soft)
            .ShouldBeTrue($"the soft '{name}' value must be a number, got '{tokens[^3]}' in '{line.Trim()}'");
        long.TryParse(tokens[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hard)
            .ShouldBeTrue($"the hard '{name}' value must be a number, got '{tokens[^2]}' in '{line.Trim()}'");

        soft.ShouldBe(expected, FormattableString.Invariant($"the child must observe the requested soft '{name}'"));
        hard.ShouldBe(expected, FormattableString.Invariant($"the child must observe the requested hard '{name}'"));
    }

    /// <summary>
    /// The executable to start for the heap probe: this assembly itself. A module initializer in the same assembly
    /// (<see cref="GcHeapLimitProbeChild"/>) turns that second process into a one-shot allocation probe before the
    /// test runner starts, so the probe needs no extra project, no SDK and no build step.
    /// </summary>
    private static (string FileName, IReadOnlyList<string> Arguments) ProbeChildCommand()
    {
        var processPath = Environment.ProcessPath ?? string.Empty;
        if (processPath.Length == 0)
        {
            Assert.Skip("Environment.ProcessPath is empty, so the probe child cannot be started from this host");
        }

        // A framework-dependent MTP app may run under the muxer; then the assembly has to be named explicitly.
        var hostName = Path.GetFileNameWithoutExtension(processPath);
        if (!string.Equals(hostName, "dotnet", StringComparison.Ordinal)
            && !string.Equals(hostName, "testhost", StringComparison.Ordinal))
        {
            return (processPath, []);
        }

        var assembly = typeof(SandboxLimitsProbeTests).Assembly.Location;
        if (assembly.Length == 0)
        {
            Assert.Skip(
                $"'{processPath}' needs the test assembly path, which a single-file build does not expose; the " +
                "heap probe cannot start a second process");
        }

        return (processPath, [assembly]);
    }

    /// <summary>
    /// Copies the named variables of this process's environment into <paramref name="startInfo"/>, skipping the
    /// ones that are unset, so a probe child can be started with a minimal environment instead of inheriting one.
    /// </summary>
    /// <param name="startInfo">The start info whose environment is being built.</param>
    /// <param name="names">The variable names to copy.</param>
    private static void CopyEnvironment(ProcessStartInfo startInfo, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                startInfo.Environment[name] = value;
            }
        }
    }

    /// <summary>The absolute path of <paramref name="name"/> on <c>PATH</c>, or an empty string when it is absent.</summary>
    private static string FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static Task<(int ExitCode, string StandardOutput, string StandardError)> RunBoundedAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return RunBoundedAsync(startInfo, timeout);
    }

    /// <summary>
    /// Runs one child with a hard deadline: a probe that hangs — or a test that is cancelled — has its whole
    /// process tree killed in a <see langword="finally"/>, and a hang fails the test with the output it produced,
    /// so a wedged probe can never hold the suite (06.3.6 §7 "wall via parent timer").
    /// </summary>
    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunBoundedAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"could not start '{startInfo.FileName}' for the sandbox probe");

        try
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                Kill(process);

                if (cancellationToken.IsCancellationRequested)
                {
                    // The test itself was abandoned: the tree is gone, so the cancellation propagates as itself.
                    throw;
                }

                var deadlineMessage = FormattableString.Invariant(
                    $"'{startInfo.FileName}' did not exit within {timeout.TotalSeconds:0} s and was killed");

                throw new TimeoutException(
                    $"{deadlineMessage}; stdout: {Truncate(await standardOutput)}; stderr: {Truncate(await standardError)}");
            }

            return (process.ExitCode, await standardOutput, await standardError);
        }
        finally
        {
            // Success, timeout, cancellation or a fault in the pipes: nothing of this probe may outlive the test.
            Kill(process);
        }
    }

    /// <summary>Kills the whole process tree of a probe that is still running; an exited probe needs nothing.</summary>
    /// <param name="process">The probe process.</param>
    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The child exited between the deadline and the kill.
        }
    }

    /// <summary>Turns the child's <c>key=value</c> marker lines into numbers; the format is written by <see cref="GcHeapLimitProbeChild"/>.</summary>
    private static Dictionary<string, long> ParseMetricLines(string content)
    {
        var metrics = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            separator.ShouldBeGreaterThan(0, $"the probe child must write 'key=value' lines, got '{line}'");

            var key = line[..separator];
            var value = line[(separator + 1)..];

            // The child prints booleans as 0/1 so one parser covers every metric.
            long parsed = value switch
            {
                "True" => 1,
                "False" => 0,
                _ => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                    ? number
                    : throw new InvalidOperationException($"probe metric '{key}' is not a number: '{value}'"),
            };

            metrics[key] = parsed;
        }

        metrics.Count.ShouldBeGreaterThan(2, $"the probe child must report limit, allocated and oom; got: {Truncate(content)}");
        return metrics;
    }

    private static string Truncate(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 400 ? trimmed : $"{trimmed[..400]}…";
    }
}

/// <summary>
/// 06.3.6-T1.1: the probe child for <see cref="SandboxLimitsProbeTests.GcHeapHardLimit_StopsAChildProcessAllocatingPastTheLimit"/>.
/// The parent starts this test assembly again with <c>EVG_PARSER_PROBE_MARKER</c>, the matching
/// <c>EVG_PARSER_PROBE_NONCE</c> and a <c>DOTNET_GCHeapHardLimit</c>; the module initializer runs before the test
/// runner starts, allocates 1 MiB blocks until the runtime refuses, writes the observed ceiling and allocation
/// count, and exits. Without the marker, or with a marker whose nonce does not match, it does nothing, so an
/// ordinary test run is unaffected — including one that inherited a stale marker variable from its environment.
/// </summary>
// CA2255 warns about module initializers in *libraries*; this is a deliberate one-shot probe in a test
// executable, which is the only place a second process with an inherited heap limit can be produced without a
// second project or a build step inside a test.
#pragma warning disable CA2255
internal static class GcHeapLimitProbeChild
{
    /// <summary>Set by the parent probe and read here; absent in a normal test run.</summary>
    internal const string MarkerVariable = "EVG_PARSER_PROBE_MARKER";

    /// <summary>
    /// Set beside the marker, to the value <see cref="NonceFor"/> derives from the marker path. The marker alone
    /// is not a licence to exit the process: the pair is what the parent sets, so an inherited or stale marker
    /// variable can never turn a normal test run into the probe.
    /// </summary>
    internal const string NonceVariable = "EVG_PARSER_PROBE_NONCE";

    /// <summary>
    /// The nonce for one marker path: a SHA-256 of the path the parent just created under its own temp
    /// directory, so only a parent that knows the path can authorise the probe.
    /// </summary>
    /// <param name="markerPath">The marker path the parent passes in <see cref="MarkerVariable"/>.</param>
    /// <returns>The hexadecimal nonce, as it travels in <see cref="NonceVariable"/>.</returns>
    internal static string NonceFor(string markerPath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(markerPath)));

    /// <summary>True when this process is the probe child the parent started (both variables present and bound).</summary>
    /// <returns>True for the probe child only.</returns>
    internal static bool IsProbeChild() => TryGetMarker(out _);

    [ModuleInitializer]
    internal static void RunIfProbeChild()
    {
        if (!TryGetMarker(out var marker))
        {
            return;
        }

        var limit = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var blocks = new List<byte[]>();
        long allocated = 0;
        var outOfMemory = false;

        // Bound the loop by the ceiling itself (+64 MiB): if the limit is not in force the probe still stops
        // instead of eating the runner's memory, and the parent fails on oom=False.
        var attemptLimit = (int)Math.Min(4096, Math.Max(8, (limit / (1024 * 1024)) + 64));
        try
        {
            for (var index = 0; index < attemptLimit; index++)
            {
                blocks.Add(new byte[1024 * 1024]);
                allocated += 1024 * 1024;
            }
        }
        catch (OutOfMemoryException)
        {
            outOfMemory = true;
        }

        File.WriteAllText(
            marker,
            FormattableString.Invariant($"limit={limit}\nallocated={allocated}\noom={outOfMemory}\n"));

        // Never let the probe process run a test suite: the marker process exists only to allocate and report.
        Environment.Exit(0);
    }

    /// <summary>
    /// Reads the marker path and accepts it only when the parent's nonce matches it, so the marker variable alone
    /// never authorises the probe.
    /// </summary>
    /// <param name="marker">The marker path when this process is the probe child; otherwise null.</param>
    /// <returns>True when both variables are present and bound to each other.</returns>
    private static bool TryGetMarker([NotNullWhen(true)] out string? marker)
    {
        marker = Environment.GetEnvironmentVariable(MarkerVariable);

        return !string.IsNullOrEmpty(marker)
            && string.Equals(
                Environment.GetEnvironmentVariable(NonceVariable),
                NonceFor(marker),
                StringComparison.Ordinal);
    }
}
#pragma warning restore CA2255
