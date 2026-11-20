using System.Diagnostics;
using System.Text.Json;
using Evidgate.GoldenHarness;
using Shouldly;
using Xunit;

namespace GoldenHarness.UnitTests;

/// <summary>
/// The generated large report must be one the landed pytest parser accepts: a 1 000-test report goes through the
/// same in-process mapper the golden harness and the ingest path use, and every declared case comes back.
/// </summary>
public sealed class LargeReportParserTests
{
    private const int Tests = 1_000;

    [Fact]
    public void Generated_SmallReport_IsAccepted_ByThePytestParser_WithTheExactCaseCount()
    {
        var root = Path.Combine(Path.GetTempPath(), "evidgate-large-parse-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunGenerator(root);
            GoldenParsers.Create().TryGet(GoldenParsers.PytestParserId, out var parse).ShouldBeTrue();
            var meta = new CaseMeta("junit-pytest", GoldenParsers.PytestParserId, "1.0.0", "generated", null);
            var fixture = new FixtureCase(
                "junit-pytest", "large", root, meta, new ExpectedDoc("parsed", 0, []), ["input.xml"]);

            var result = parse!(fixture);

            result.RawReportStatus.ShouldBe("parsed");
            result.DeclaredCount.ShouldBe(Tests);
            result.Events.Count.ShouldBe(Tests);
            result.Events.Select(e => e.CaseKey).Distinct().Count().ShouldBe(Tests);
            result.Events.Count(e => e.NormalisedStatus == "passed").ShouldBeGreaterThan(Tests * 9 / 10);
            result.Events.Count(e => e.NormalisedStatus == "failed").ShouldBeGreaterThan(0);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void RunGenerator(string outDirectory)
    {
        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(LocateRepoRoot(), "scripts", "dev", "gen-large-report", "gen-large-report.mjs"));
        start.ArgumentList.Add("--out-dir");
        start.ArgumentList.Add(outDirectory);
        start.ArgumentList.Add($"--tests={Tests}");

        using var process = Process.Start(start).ShouldNotBeNull();
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, stderr);
    }

    private static string LocateRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Evidgate.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Evidgate.slnx not found above '{AppContext.BaseDirectory}'");
    }
}
