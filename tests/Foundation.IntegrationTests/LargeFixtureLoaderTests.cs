using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Foundation.IntegrationTests;

/// <summary>
/// The .NET half of the large-report fixture: the repository stores the generator and
/// a loader-shaped directory (<c>meta.json</c>, <c>input.json</c> generator parameters,
/// <c>expected.json</c> checksums), never the report. The tests load that directory, run the generator from
/// its parameters, and pin the bytes to the committed digest — the same pins <c>largeFixture.test.ts</c> asserts on the TypeScript side. Like
/// <see cref="FixtureLoaderTests"/> it needs neither the container nor the database.
/// </summary>
public sealed class LargeFixtureLoaderTests
{
    private static readonly string Generator = Path.Combine(
        FixtureLoader.RepoRoot, "scripts", "dev", "gen-large-report", "gen-large-report.mjs");

    private static readonly Fixture Committed = FixtureLoader.Load(FixtureLoader.Fixtures("large/100k-tests"));

    private static readonly JsonElement Checksums = JsonDocument.Parse(Committed.ExpectedText!).RootElement.Clone();

    private static readonly JsonElement Parameters = JsonDocument.Parse(Committed.InputText).RootElement.Clone();

    [Fact]
    public void LargeFixture_CommittedDirectory_LoadsWithGeneratorParameters()
    {
        Committed.Domain.ShouldBe("large");
        Committed.Name.ShouldBe("100k-tests");
        Committed.Meta.AddedBy.ShouldBe("04.1.3-T3.1");
        Parameters.GetProperty("seed").GetInt32().ShouldBe(Checksums.GetProperty("seed").GetInt32());
        Parameters.GetProperty("tests").GetInt32().ShouldBe(Checksums.GetProperty("full").GetProperty("tests").GetInt32());
        SmallTests.ShouldBe(Checksums.GetProperty("small").GetProperty("tests").GetInt32());
    }

    [Fact]
    public void LargeFixture_Generator_IsDeterministic_AcrossTwoRuns()
    {
        using var temp = new TempDirectory();
        var first = Generate(Path.Combine(temp.Path, "a"), SmallTests);
        var second = Generate(Path.Combine(temp.Path, "b"), SmallTests);

        second.ShouldBe(first);
        first.ShouldBe(Checksums.GetProperty("small").GetProperty("sha256").GetString());
    }

    [Fact]
    public void LargeFixture_ChangedReportBytes_DoNotMatchPinnedChecksum()
    {
        using var temp = new TempDirectory();
        Generate(temp.Path, SmallTests);
        var report = Path.Combine(temp.Path, "input.xml");
        File.AppendAllText(report, " ");

        Sha256Of(report).ShouldNotBe(Checksums.GetProperty("small").GetProperty("sha256").GetString());
    }

    [Fact]
    public void LargeFixture_FullReport_MatchesCommittedChecksum()
    {
        using var temp = new TempDirectory();
        var stopwatch = Stopwatch.StartNew();
        var digest = Generate(temp.Path, Parameters.GetProperty("tests").GetInt32());
        stopwatch.Stop();
        var bytes = new FileInfo(Path.Combine(temp.Path, "input.xml")).Length;

        // The measured generation time and size, printed once; no budget is asserted here.
        Console.WriteLine($"large-report 100k: {stopwatch.ElapsedMilliseconds} ms, {bytes} bytes, {digest}");
        digest.ShouldBe(Checksums.GetProperty("full").GetProperty("sha256").GetString());
        bytes.ShouldBe(Checksums.GetProperty("full").GetProperty("bytes").GetInt64());
    }

    private static int SmallTests => Parameters.GetProperty("small_tests").GetInt32();

    private static string Generate(string outDirectory, int tests)
    {
        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Generator);
        start.ArgumentList.Add("--out-dir");
        start.ArgumentList.Add(outDirectory);
        start.ArgumentList.Add($"--tests={tests}");
        start.ArgumentList.Add($"--seed={Parameters.GetProperty("seed").GetInt32()}");

        using var process = Process.Start(start).ShouldNotBeNull();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, stderr);
        return JsonDocument.Parse(stdout).RootElement.GetProperty("sha256").GetString()!;
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "evidgate-large-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
