using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Shouldly;
using Xunit;

namespace Parser.Tests;

/// <summary>
/// 06.3.6 §8 <em>Performance</em>: the spawn cost of the real parse child, measured, so the "[INFERENCE] 50 ms p95"
/// budget in the plan has a number of its own rather than a guess. Nothing here asserts a threshold: the plan's
/// budget is an inference, a CI runner is not the production host, and a threshold on a shared machine would fail
/// for reasons that have nothing to do with the runner. The figures are printed through
/// <see cref="ITestOutputHelper"/> and recorded in the plan Notes by the orchestrator.
/// <para>
/// <b>What is measured.</b> <see cref="ChildProcessParseRunner.RunAsync"/> end to end for one small JUnit report:
/// the run's private temp directory, the spawn, the byte-capped stdin pump, the child's runtime start-up, the
/// profile's parse, the NDJSON emit, the marker and the teardown. The child's own start-up is reported separately
/// from the <c>ChildStarted</c> seam the runner exposes — the instant <see cref="Process.Start()"/> returned — and a
/// direct child launch measures start-to-first-byte, which <see cref="IParseRunner"/> does not expose because the
/// parent buffers the whole stream before it answers.
/// </para>
/// <para>
/// <b>Why the child is not warmed up twice.</b> Each run is a new process, so a "warm-up" can only warm the page
/// cache and the JIT of the parent's own code path; three unscored runs are taken for exactly that. The child's
/// runtime settings are its own: it starts with the runner's scrubbed environment
/// (<c>DOTNET_GCHeapHardLimit</c>, <c>TMPDIR</c>, <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT</c>) and never inherits
/// this process's <c>DOTNET_*</c> switches, so tiered compilation and ReadyToRun behave as a deployed child's do
/// (published without <c>PublishReadyToRun</c>, 06.3.6 plan Notes :168).
/// </para>
/// <para>
/// Runs inside every <c>Parser.Tests</c> run: no gate excludes the <c>Category=Benchmark</c> trait, so nothing has
/// to be filtered for it. It is a measurement, not a pass/fail check, and asserts no timing threshold.
/// </para>
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class ParseSpawnBenchmarkTests(ITestOutputHelper output)
{
    /// <summary>Unscored runs: they warm the parent's own code path and the page cache, never the child process.</summary>
    private const int WarmupRuns = 3;

    /// <summary>Scored runs; 30 is enough for a stable p95 by nearest rank.</summary>
    private const int MeasuredRuns = 30;

    /// <summary>Direct child launches for the start-to-first-byte figure.</summary>
    private const int FirstByteRuns = 5;

    /// <summary>The shared small report every run parses.</summary>
    private const string JunitFixture = "reports/junit/sample/input.xml";

    /// <summary>The per-run input cap the harness uses; one mebibyte is far above this report's 1.1 KiB.</summary>
    private const long MaxBytes = 1024 * 1024;

    /// <summary>
    /// The parent's own runtime switches, printed with the figures: the child starts with the runner's allow-list
    /// environment and never inherits them, so a reader can tell which knobs shaped the <em>parent's</em> code path.
    /// </summary>
    private static readonly string[] RuntimeSwitches =
        ["DOTNET_TieredCompilation", "DOTNET_TieredPGO", "DOTNET_ReadyToRun"];

    /// <summary>
    /// Measures one small report end to end through the production runner and prints the distribution: total run
    /// wall time and the spawn portion of it, p50 and p95 by nearest rank, plus the child's start-to-first-byte.
    /// </summary>
    [Fact]
    public async Task Real_Child_Spawn_Overhead_Is_Measured_For_A_Small_Report()
    {
        var content = await File.ReadAllBytesAsync(
            RepoPaths.Fixtures(JunitFixture),
            TestContext.Current.CancellationToken);

        using var harness = new ParseRunnerHarness();
        var childStartedAt = 0L;
        using var runner = new ChildProcessParseRunner(
            harness.Options(options => options.ChildPath = ChildRunner.ChildAssemblyPath()),
            ChildCommandLine.ForOptions)
        {
            ChildStarted = () => childStartedAt = Stopwatch.GetTimestamp(),
        };

        var totals = new List<double>(MeasuredRuns);
        var spawns = new List<double>(MeasuredRuns);
        var records = 0;

        for (var run = 0; run < WarmupRuns + MeasuredRuns; run++)
        {
            childStartedAt = 0;
            var runStartedAt = Stopwatch.GetTimestamp();

            var result = await ParseRunnerHarness.RunAsync(
                runner,
                ParseRunnerProfile.Xml,
                maxBytes: MaxBytes,
                input: content,
                cancellationToken: TestContext.Current.CancellationToken);

            var total = Stopwatch.GetElapsedTime(runStartedAt).TotalMilliseconds;

            var succeeded = result.ShouldBeOfType<ParseRunSucceeded>();
            succeeded.OutputRecords.ShouldBeGreaterThan(0, "the benchmark report must produce records");
            childStartedAt.ShouldBeGreaterThan(
                runStartedAt,
                "ChildStarted must fire after the run began, or the spawn figure would be meaningless");

            records = succeeded.OutputRecords;

            if (run < WarmupRuns)
            {
                continue;
            }

            totals.Add(total);
            spawns.Add(Stopwatch.GetElapsedTime(runStartedAt, childStartedAt).TotalMilliseconds);
        }

        totals.Count.ShouldBe(MeasuredRuns);
        spawns.Count.ShouldBe(MeasuredRuns);

        var totalStats = Summarise(totals);
        var spawnStats = Summarise(spawns);
        var firstBytes = await MeasureFirstByteAsync(content, TestContext.Current.CancellationToken);
        var firstByteStats = Summarise(firstBytes);

        output.WriteLine(
            $"06.3.6-T1.4 spawn benchmark — {WarmupRuns} warm-ups, {MeasuredRuns} measured runs, XML report "
            + $"{content.Length} bytes, {records} accepted records per run");
        output.WriteLine(
            $"host: {RuntimeInformation.OSDescription} / {RuntimeInformation.OSArchitecture} / "
            + $"{Environment.ProcessorCount} logical CPUs / {RuntimeInformation.FrameworkDescription} / "
            + $"server GC {(System.Runtime.GCSettings.IsServerGC ? "on" : "off")}");
        output.WriteLine(
            "parent runtime switches (the child never inherits them): "
            + string.Join(
                ", ",
                RuntimeSwitches.Select(name => name + "=" + (Environment.GetEnvironmentVariable(name) ?? "(unset)"))));
        output.WriteLine(
            "method: Stopwatch.GetTimestamp around IParseRunner.RunAsync (temp dir + spawn + stdin pump + child "
            + "start-up + parse + NDJSON emit + teardown); spawn = run start → ChildStarted (Process.Start "
            + "returned); first byte = direct child launch, Process.Start return → first stdout byte");
        output.WriteLine(FormatLine("run wall ms", totalStats, totals));
        output.WriteLine(FormatLine("spawn ms", spawnStats, spawns));
        output.WriteLine(FormatLine("first byte ms", firstByteStats, firstBytes));
        output.WriteLine(
            "note: no threshold is asserted — the 50 ms p95 figure in 06.3.6 §8 is an [INFERENCE] budget and this "
            + "host is not the production runner");
    }

    /// <summary>
    /// Starts the built child directly (the same command line <see cref="ChildRunner"/> uses) and measures the time
    /// from <see cref="Process.Start()"/> returning to the first stdout byte: the child runtime's start-up plus the
    /// profile's first emitted record. The whole stream is then drained and the exit code asserted, so a measurement
    /// never leaves a wedged child behind.
    /// </summary>
    /// <param name="content">The report to feed the child.</param>
    /// <param name="cancellationToken">The test's cancellation.</param>
    /// <returns>One figure per launch, in milliseconds.</returns>
    private static async Task<List<double>> MeasureFirstByteAsync(byte[] content, CancellationToken cancellationToken)
    {
        var samples = new List<double>(FirstByteRuns);

        for (var run = 0; run < FirstByteRuns; run++)
        {
            var startInfo = ChildRunner.CreateStartInfo(
                "--profile",
                "xml",
                "--max-bytes",
                MaxBytes.ToString(CultureInfo.InvariantCulture),
                "--cpu-seconds",
                "20",
                "--max-file-bytes",
                "0");

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("could not start the parser child process");
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(60));

            try
            {
                var started = Stopwatch.GetTimestamp();
                var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, budget.Token);
                var stdin = WriteInputAsync(process, content, budget.Token);

                var buffer = new byte[8192];
                var firstRead = await process.StandardOutput.BaseStream.ReadAsync(buffer, budget.Token);
                var firstByte = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                while (await process.StandardOutput.BaseStream.ReadAsync(buffer, budget.Token) > 0)
                {
                    // Drain the rest: the child cannot finish while its stdout pipe is full.
                }

                await process.WaitForExitAsync(budget.Token);
                await stdin;
                await stderr;

                firstRead.ShouldBeGreaterThan(0, "the child must write its first record");
                process.ExitCode.ShouldBe(0, "a measured launch must be a successful run");

                samples.Add(firstByte);
            }
            finally
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
                    // The child exited between the check and the kill, or the OS refused a kill for a process
                    // that is already gone: nothing is left to kill on either path.
                }
            }
        }

        return samples;
    }

    /// <summary>Writes the artifact to the child's stdin and closes it; a broken pipe is the child's verdict.</summary>
    /// <param name="process">The running child.</param>
    /// <param name="content">The bytes to write.</param>
    /// <param name="cancellationToken">The launch's budget.</param>
    /// <returns>Nothing.</returns>
    private static async Task WriteInputAsync(Process process, byte[] content, CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(content, cancellationToken);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The child may stop reading before the last byte on a rejection path; that is its verdict, not ours.
        }
    }

    /// <summary>One line per distribution: min, p50, p95, max and mean, in milliseconds.</summary>
    /// <param name="label">What the figures measure.</param>
    /// <param name="stats">The summary of <paramref name="samples"/>.</param>
    /// <param name="samples">The per-run figures, printed in run order so a drift is visible.</param>
    /// <returns>The formatted line.</returns>
    private static string FormatLine(
        string label,
        (double Min, double P50, double P95, double Max, double Mean) stats,
        List<double> samples) =>
        $"{label}: min {F(stats.Min)} · p50 {F(stats.P50)} · p95 {F(stats.P95)} · max {F(stats.Max)} · "
        + $"mean {F(stats.Mean)} | samples: "
        + string.Join(" ", samples.Select(F));

    /// <summary>One figure, invariant and to one decimal, so the printed line is stable across locales.</summary>
    /// <param name="value">The milliseconds value.</param>
    /// <returns>The formatted value.</returns>
    private static string F(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Summarises the samples without reordering them: the caller prints them in run order.</summary>
    /// <param name="samples">The figures, in run order; the list is left untouched.</param>
    /// <returns>The summary, computed from a sorted copy.</returns>
    private static (double Min, double P50, double P95, double Max, double Mean) Summarise(List<double> samples)
    {
        var ordered = samples.ToList();
        ordered.Sort();

        return (ordered[0], NearestRank(ordered, 50), NearestRank(ordered, 95), ordered[^1], ordered.Average());
    }

    /// <summary>The nearest-rank percentile: the sample at <c>ceil(p / 100 × n) - 1</c> after sorting.</summary>
    /// <param name="sorted">Ascending samples.</param>
    /// <param name="percentile">The percentile, 1–100.</param>
    /// <returns>The sample value.</returns>
    private static double NearestRank(List<double> sorted, int percentile) =>
        sorted[Math.Clamp((int)Math.Ceiling(percentile / 100d * sorted.Count) - 1, 0, sorted.Count - 1)];
}
