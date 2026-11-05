using System.Globalization;
using Evidgate.GoldenHarness;
using Shouldly;
using Xunit;

namespace GoldenHarness.UnitTests;

/// <summary>
/// The seeded pytest cases driven through the real mapper table the console tool runs with. This is the
/// corpus-level proof that the checked-in expectations are what the production mapping code produces, not what a
/// test helper claims.
/// </summary>
public sealed class GoldenParsersTests
{
    private static readonly string SeededFixtures = LocateFixturesRoot();

    [Fact]
    public void Registry_Registers_The_Pytest_Dialect()
    {
        GoldenParsers.Create().TryGet(GoldenParsers.PytestParserId, out var parse).ShouldBeTrue();
        parse.ShouldNotBeNull();
    }

    [Fact]
    public void Seeded_Pytest_Cases_Pass_Through_The_Real_Mapper()
    {
        var (exitCode, output) = Run("--fixtures", SeededFixtures, "--producer", GoldenParsers.PytestParserId, "--strict");

        exitCode.ShouldBe(0);
        RowFor(output, "junit-pytest/happy").ShouldContain("PASS");
        RowFor(output, "junit-pytest/truncated").ShouldContain("PASS");
        RowFor(output, "junit-pytest/truncated").ShouldContain("declared malformed");
        output.ShouldContain("2 passed");
        output.ShouldContain("0 failed");
        output.ShouldContain("0 pending");
        output.ShouldContain("0 error");
    }

    [Fact]
    public void Whole_Seeded_Corpus_Leaves_Only_Unlanded_Parsers_Pending()
    {
        var (exitCode, output) = Run("--fixtures", SeededFixtures);

        exitCode.ShouldBe(0);
        RowFor(output, "junit-pytest/happy").ShouldContain("PASS");
        RowFor(output, "playwright-reporter/skeleton").ShouldContain("PENDING");
        output.ShouldContain("2 passed");
        output.ShouldContain("0 failed");
        output.ShouldContain("0 error");
    }

    private static (int ExitCode, string Output) Run(params string[] args)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var exitCode = HarnessRunner.Run(args, GoldenParsers.Create(), writer);
        return (exitCode, writer.ToString());
    }

    private static string RowFor(string output, string fixture) =>
        output.Split('\n').Single(line =>
            line.StartsWith(fixture, StringComparison.Ordinal)
            && line.Length > fixture.Length
            && line[fixture.Length] == ' ');

    private static string LocateFixturesRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Evidgate.slnx")))
            {
                return Path.Combine(directory.FullName, "fixtures");
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Evidgate.slnx not found above '{AppContext.BaseDirectory}'");
    }
}
