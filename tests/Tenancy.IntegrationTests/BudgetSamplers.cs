using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Evidgate.Testing;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Samples managed bytes retained above baseline during the parse. Each sample finalises queued objects and forces one
/// full collection, so dead records do not read as retention; the sample count and GC pause time expose how much of the
/// measured parse wall time the sampler itself caused.
/// </summary>
internal sealed class ParentRetainedSampler : IAsyncDisposable
{
    private readonly long _baseline;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private readonly object _gate = new();
    private ExceptionDispatchInfo? _failure;
    private long _peak;
    private long _gcPauseTicks;
    private int _samples;

    private ParentRetainedSampler(long baseline, TimeSpan interval)
    {
        _baseline = baseline;
        _interval = interval;
        _thread = new Thread(Loop) { IsBackground = true, Name = "budget-parent-retained" };
        _thread.Start();
    }

    /// <summary>The retained bytes before the phase, measured with one full collection.</summary>
    /// <returns>The managed bytes remaining after the collection.</returns>
    internal static long Baseline() => Retained();

    /// <summary>Starts sampling; the caller takes a final sample while the parse handler is still rooted.</summary>
    /// <param name="baseline">The retained bytes before the phase.</param>
    /// <param name="interval">How often a sample runs.</param>
    /// <returns>The running sampler.</returns>
    internal static ParentRetainedSampler Start(long baseline, TimeSpan interval) => new(baseline, interval);

    /// <summary>The largest retained delta any sample saw.</summary>
    internal long PeakRetainedBytes => Interlocked.Read(ref _peak);

    /// <summary>How many samples ran, including the explicit final parse sample.</summary>
    internal int Samples => Volatile.Read(ref _samples);

    /// <summary>The cumulative runtime GC pause time incurred while taking retained samples.</summary>
    internal TimeSpan GcPauseTime => TimeSpan.FromTicks(Interlocked.Read(ref _gcPauseTicks));

    /// <summary>Samples before the caller releases the parse handler and its result.</summary>
    internal void SampleFinal() => GuardSample();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (!await Task.Run(() => _thread.Join(TimeSpan.FromSeconds(30))).ConfigureAwait(false))
        {
            throw new TimeoutException("The parent retained sampler did not stop within 30 seconds.");
        }

        _stop.Dispose();
        _failure?.Throw();
    }

    private static long Retained()
    {
        // Finalise first: the single forced collection then frees the previous sample's finalisable garbage, so the
        // number is live retention. A second forced collection per sample would double the pause it adds to the parse.
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        return GC.GetTotalMemory(forceFullCollection: false);
    }

    private void Loop()
    {
        while (!_stop.Token.WaitHandle.WaitOne(_interval))
        {
            GuardSample();
        }
    }

    // A sampler thread that dies unobserved would take the host down, and a silent one would record nothing.
    // The first failure is kept and rethrown by DisposeAsync, so a broken sampler fails the test.
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A background sampler may not throw on its thread; the failure is stored and rethrown at disposal.")]
    private void GuardSample()
    {
        try
        {
            Sample();
        }
        catch (Exception exception)
        {
            Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(exception), null);
        }
    }

    private void Sample()
    {
        lock (_gate)
        {
            var before = GC.GetTotalPauseDuration();
            var retained = Retained() - _baseline;
            Interlocked.Add(ref _gcPauseTicks, (GC.GetTotalPauseDuration() - before).Ticks);
            Interlocked.Increment(ref _samples);
            Interlocked.Exchange(ref _peak, Math.Max(_peak, retained));
        }
    }
}

/// <summary>
/// Samples the parse child's RSS. Linux VmHWM includes peaks between observations, but the child may allocate
/// after the last observation and exit before the next one: either platform's reported number is a peak floor.
/// </summary>
internal sealed class ChildWorkingSetSampler : IAsyncDisposable
{
    private readonly string _marker;
    private readonly string _method;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private readonly object _gate = new();
    private ExceptionDispatchInfo? _failure;
    private long _peak;
    private int _samples;

    private ChildWorkingSetSampler(string childAssemblyPath, TimeSpan interval)
    {
        _marker = childAssemblyPath;
        _interval = interval;
        _method = Directory.Exists(ProcRoot)
            ? "proc-vmhwm (last observed kernel high-water mark, peak floor)"
            : "ps-rss (sampled resident set, peak floor)";
        _thread = new Thread(Loop) { IsBackground = true, Name = "budget-child-rss" };
        _thread.Start();
    }

    /// <summary>The largest observed resident set in bytes; zero means no usable sample.</summary>
    internal long PeakWorkingSetBytes => Interlocked.Read(ref _peak);

    /// <summary>How many positive child samples ran.</summary>
    internal int Samples => Volatile.Read(ref _samples);

    /// <summary>How the number was obtained.</summary>
    internal string Method => _method;

    /// <summary>Whether the child remained observable immediately after the parse returned.</summary>
    internal string FinalObservation { get; private set; } = "final sample not attempted";

    /// <summary>Starts sampling until the caller disposes it.</summary>
    /// <param name="childAssemblyPath">The absolute assembly path the child is launched with.</param>
    /// <param name="interval">How often a sample runs.</param>
    /// <returns>The running sampler.</returns>
    internal static ChildWorkingSetSampler Start(string childAssemblyPath, TimeSpan interval) =>
        new(childAssemblyPath, interval);

    /// <summary>Takes the latest available high-water mark before the caller does other work.</summary>
    internal void SampleAfterParse()
    {
        lock (_gate)
        {
            var observed = GuardSample();
            FinalObservation = observed
                ? "child alive for final post-parse sample"
                : "child already exited before final post-parse sample";
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (!await Task.Run(() => _thread.Join(TimeSpan.FromSeconds(30))).ConfigureAwait(false))
        {
            throw new TimeoutException("The child working-set sampler did not stop within 30 seconds.");
        }

        _stop.Dispose();
        _failure?.Throw();
    }

    private static string ProcRoot => Path.DirectorySeparatorChar + "proc";

    private void Loop()
    {
        while (!_stop.Token.WaitHandle.WaitOne(_interval))
        {
            lock (_gate)
            {
                _ = GuardSample();
            }
        }
    }

    // A sampler thread that dies unobserved would take the host down, and a silent one would record nothing.
    // The first failure is kept and rethrown by DisposeAsync, so a broken sampler fails the test.
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A background sampler may not throw on its thread; the failure is stored and rethrown at disposal.")]
    private bool GuardSample()
    {
        try
        {
            return Directory.Exists(ProcRoot) ? RecordProc() : RecordPs();
        }
        catch (Exception exception)
        {
            Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(exception), null);
            return false;
        }
    }

    private bool RecordProc()
    {
        var observed = false;
        foreach (var directory in Directory.EnumerateDirectories(ProcRoot))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            try
            {
                if (!File.ReadAllText(Path.Combine(directory, "cmdline")).Contains(_marker, StringComparison.Ordinal))
                {
                    continue;
                }

                observed |= RecordProcPeak(pid);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A process that exited mid-read, or is not ours to read, is not a sample; the next tick retries.
            }
        }

        return observed;
    }

    private bool RecordProcPeak(int pid)
    {
        // VmHWM is the kernel's own high-water mark, so a peak between two observations is still observed.
        foreach (var line in File.ReadAllLines(Path.Combine(ProcRoot,
            pid.ToString(CultureInfo.InvariantCulture), "status")))
        {
            if (!line.StartsWith("VmHWM:", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line["VmHWM:".Length..].Trim().Split(' ')[0];
            Record(checked(long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) * 1024));

            return true;
        }

        return false;
    }

    private bool RecordPs()
    {
        using var process = Process.Start(new ProcessStartInfo("ps")
        {
            RedirectStandardOutput = true,
            ArgumentList = { "-axww", "-o", "pid=,rss=,command=" },
        }) ?? throw new InvalidOperationException("The child RSS sampler could not start ps.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"The child RSS sampler's ps exited with {process.ExitCode}.");
        }

        var observed = false;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains(_marker, StringComparison.Ordinal))
            {
                continue;
            }

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2)
            {
                throw new InvalidOperationException($"The child RSS sampler read a ps row without a pid and rss: '{line}'");
            }

            Record(checked(long.Parse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture) * 1024));
            observed = true;
        }

        return observed;
    }

    private void Record(long resident)
    {
        if (resident <= 0)
        {
            throw new InvalidOperationException("The child RSS sampler produced a non-positive measurement.");
        }

        Interlocked.Increment(ref _samples);
        Interlocked.Exchange(ref _peak, Math.Max(_peak, resident));
    }
}

/// <summary>This process's own working set, printed beside the budget metrics for diagnosis only.</summary>
internal static class ParentWorkingSet
{
    /// <summary>
    /// The process's peak resident set where the platform reports one: <c>PeakWorkingSet64</c>, else Linux's
    /// <c>VmHWM</c>, else the current working set with a source label that says it is not a peak.
    /// </summary>
    /// <returns>The bytes and where they came from.</returns>
    internal static (long Bytes, string Source) Peak()
    {
        using var self = Process.GetCurrentProcess();

        try
        {
            return (self.PeakWorkingSet64, "PeakWorkingSet64");
        }
        catch (PlatformNotSupportedException)
        {
            // Not every runtime/OS pair answers a process-wide peak; the fallbacks below say what they measured.
        }

        var status = Path.DirectorySeparatorChar + Path.Combine("proc", "self", "status");
        if (File.Exists(status))
        {
            foreach (var line in File.ReadAllLines(status))
            {
                if (!line.StartsWith("VmHWM:", StringComparison.Ordinal))
                {
                    continue;
                }

                var value = line["VmHWM:".Length..].Trim().Split(' ')[0];
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kilobytes))
                {
                    return (kilobytes * 1024, "proc-self VmHWM");
                }

                break;
            }
        }

        return (self.WorkingSet64, "WorkingSet64 (current, not a peak)");
    }
}

/// <summary>Locates the built parse child the way the parser's own test harness does.</summary>
internal static class ParseChildAssembly
{
    /// <summary>
    /// <c>tests/Tenancy.IntegrationTests/bin/&lt;configuration&gt;/&lt;framework&gt;/</c> → the child's own
    /// <c>src/Evidgate.Parser/bin/&lt;configuration&gt;/&lt;framework&gt;/Evidgate.Parser.dll</c>. The project
    /// reference builds it first; this assembly cannot reference the parser's test harness that normally does this.
    /// </summary>
    /// <returns>The absolute assembly path, or throws when this build did not produce it.</returns>
    internal static string Locate()
    {
        var marker = string.Concat(Path.DirectorySeparatorChar, "bin", Path.DirectorySeparatorChar);
        var output = AppContext.BaseDirectory;
        var index = output.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException($"cannot derive the build configuration from '{output}' (no bin segment)");
        }

        var configuration = output[(index + marker.Length)..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])[0];
        var framework = Path.GetFileName(Path.TrimEndingDirectorySeparator(output));
        var assembly = Path.Combine(FixtureLoader.RepoRoot, "src", "Evidgate.Parser", "bin", configuration, framework,
            "Evidgate.Parser.dll");

        return File.Exists(assembly)
            ? assembly
            : throw new InvalidOperationException(
                $"{assembly} is missing: build the project so the parse child is built beside it");
    }
}

/// <summary>
/// A directory under the system temp path that removes itself, so a budget run leaves no fixture behind. A directory
/// that is already gone, or that cannot be emptied, is left alone: cleanup may not replace the run's own verdict.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    /// <summary>Creates an empty directory whose name is unique to this run.</summary>
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "evidgate-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>The directory's absolute path.</summary>
    public string Path { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        // A child that outlived the run may already have removed files here; the cleanup must not replace the
        // failure the measurement is reporting, so a vanished or busy directory is not itself a test failure.
        if (!Directory.Exists(Path))
        {
            return;
        }

        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is worth less than the measurement failure this dispose would replace.
        }
    }
}

/// <summary>
/// One object this run wrote into the shared store, deleted when the run ends: the bucket is shared with every other
/// test, and an 11 MB report per run would outlive the run that produced it.
/// </summary>
internal sealed class StoredObject(SeaweedFsStore store, LargeReportBudgetTests.Artifact artifact) : IAsyncDisposable
{
    /// <summary>The artifact the seeded object belongs to.</summary>
    internal LargeReportBudgetTests.Artifact Artifact => artifact;

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await store.DeleteAsync(artifact.ObjectKey).ConfigureAwait(false);
}
