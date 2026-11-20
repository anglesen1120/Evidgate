using System.Diagnostics;
using System.Globalization;
using Evidgate.Testing;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Samples the managed bytes this process holds over its baseline while a phase runs, so the parse's in-flight cost
/// is a measured number rather than an end-state one. Each sample forces a full collection first: garbage queued for
/// finalisation would otherwise read as retention.
/// <para>
/// The interval is a trade: a shorter one sees more of the peak but spends the measured phase in collections it
/// caused. The sample count is printed with the result, so a reader can tell how much of a wall time was the sampler's.
/// </para>
/// </summary>
internal sealed class ParentRetainedSampler : IAsyncDisposable
{
    private readonly long _baseline;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private long _peak;
    private int _samples;

    private ParentRetainedSampler(long baseline, TimeSpan interval)
    {
        _baseline = baseline;
        _interval = interval;

        // A dedicated thread keeps the sampling off the thread pool, so a collection never competes with the phase's
        // own continuations for a worker.
        _thread = new Thread(Loop) { IsBackground = true, Name = "budget-parent-retained" };
        _thread.Start();
    }

    /// <summary>The retained bytes before the phase, taken through the same forced collection as every sample.</summary>
    /// <returns><see cref="GC.GetTotalMemory(bool)"/> after a full, blocking collection.</returns>
    internal static long Baseline() => Retained();

    /// <summary>Starts sampling; the caller disposes it once the phase returns.</summary>
    /// <param name="baseline">The value <see cref="Baseline"/> returned before the phase.</param>
    /// <param name="interval">How often a sample runs.</param>
    /// <returns>The running sampler.</returns>
    internal static ParentRetainedSampler Start(long baseline, TimeSpan interval) => new(baseline, interval);

    /// <summary>The largest retained delta any sample saw.</summary>
    internal long PeakRetainedBytes => Interlocked.Read(ref _peak);

    /// <summary>How many samples ran, including the one taken as the phase ended.</summary>
    internal int Samples => Volatile.Read(ref _samples);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _ = await Task.Run(() => _thread.Join(TimeSpan.FromSeconds(30))).ConfigureAwait(false);

        Record(Retained() - _baseline);
        _stop.Dispose();
    }

    private static long Retained()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private void Loop()
    {
        while (!_stop.Token.WaitHandle.WaitOne(_interval))
        {
            Record(Retained() - _baseline);
        }
    }

    private void Record(long retained)
    {
        Interlocked.Increment(ref _samples);

        var peak = Interlocked.Read(ref _peak);
        while (retained > peak)
        {
            var seen = Interlocked.CompareExchange(ref _peak, retained, peak);
            if (seen == peak)
            {
                return;
            }

            peak = seen;
        }
    }
}

/// <summary>
/// Finds the parse child by its command line and samples its resident set. The runner exposes no process handle, so
/// the child is recognised by the assembly path the production command line carries: <c>/proc</c> where the host has it
/// (an exact <c>VmHWM</c> peak), otherwise <c>ps</c> row sampling of the current RSS, which is a floor.
/// </summary>
internal sealed class ChildWorkingSetSampler : IAsyncDisposable
{
    private readonly string _marker;
    private readonly string _method;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private long _peak;
    private int _samples;

    private ChildWorkingSetSampler(string childAssemblyPath, TimeSpan interval)
    {
        _marker = childAssemblyPath;
        _interval = interval;
        _method = Directory.Exists(ProcRoot)
            ? "proc-vmhwm (exact peak resident set)"
            : "ps-rss (sampled resident set, a floor)";
        _thread = new Thread(Loop) { IsBackground = true, Name = "budget-child-rss" };
        _thread.Start();
    }

    /// <summary>The largest resident set any sample saw, in bytes; 0 when the child was never seen.</summary>
    internal long PeakWorkingSetBytes => Interlocked.Read(ref _peak);

    /// <summary>How many child samples ran.</summary>
    internal int Samples => Volatile.Read(ref _samples);

    /// <summary>How the number was obtained, for the log line that prints it.</summary>
    internal string Method => _method;

    /// <summary>Starts sampling; the caller disposes it once the parse returns.</summary>
    /// <param name="childAssemblyPath">The absolute assembly path the child is launched with.</param>
    /// <param name="interval">How often a sample runs.</param>
    /// <returns>The running sampler.</returns>
    internal static ChildWorkingSetSampler Start(string childAssemblyPath, TimeSpan interval) =>
        new(childAssemblyPath, interval);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _ = await Task.Run(() => _thread.Join(TimeSpan.FromSeconds(30))).ConfigureAwait(false);

        _stop.Dispose();
    }

    private static string ProcRoot => Path.DirectorySeparatorChar + "proc";

    private void Loop()
    {
        while (!_stop.Token.WaitHandle.WaitOne(_interval))
        {
            if (Directory.Exists(ProcRoot))
            {
                RecordProc();
            }
            else
            {
                RecordPs();
            }
        }
    }

    private void RecordProc()
    {
        foreach (var directory in Directory.EnumerateDirectories(ProcRoot))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                continue;
            }

            try
            {
                if (!File.ReadAllText(Path.Combine(directory, "cmdline")).Contains(_marker, StringComparison.Ordinal))
                {
                    continue;
                }

                // VmHWM is the kernel's own high-water mark, so a peak between two samples is still observed.
                foreach (var line in File.ReadAllLines(Path.Combine(directory, "status")))
                {
                    if (!line.StartsWith("VmHWM:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var value = line["VmHWM:".Length..].Trim().Split(' ')[0];
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kilobytes))
                    {
                        Record(kilobytes * 1024);
                    }

                    break;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A process that exited mid-read (or is not ours to read) is not a sample; the next tick retries.
            }
        }
    }

    private void RecordPs()
    {
        using var process = Process.Start(new ProcessStartInfo("ps")
        {
            RedirectStandardOutput = true,
            ArgumentList = { "-axo", "pid=,rss=,command=" },
        });
        if (process is null)
        {
            return;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains(_marker, StringComparison.Ordinal))
            {
                continue;
            }

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2
                && long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kilobytes))
            {
                Record(kilobytes * 1024);
            }
        }
    }

    private void Record(long resident)
    {
        Interlocked.Increment(ref _samples);

        var peak = Interlocked.Read(ref _peak);
        while (resident > peak)
        {
            var seen = Interlocked.CompareExchange(ref _peak, resident, peak);
            if (seen == peak)
            {
                return;
            }

            peak = seen;
        }
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

/// <summary>A directory under the system temp path that removes itself, so a budget run leaves no fixture behind.</summary>
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
    public void Dispose() => Directory.Delete(Path, recursive: true);
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
