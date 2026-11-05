using System.Globalization;
using System.Text.Json.Nodes;
using Evidgate.GoldenHarness;
using Shouldly;
using Xunit;

namespace GoldenHarness.UnitTests;

/// <summary>
/// The console runner: exit codes, the verdict table and how one case's outcome is decided.
/// The seeded corpus is read from the repository's <c>fixtures/</c> directory because the same
/// bytes are the checked-in golden set; every forced case is written to a temporary root.
/// </summary>
public sealed partial class RunnerTests : IDisposable
{
    private const string Producer = "temp-producer";
    private const string ParserId = "temp-parser";

    private static readonly string SeededFixtures = LocateFixturesRoot();

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "evidgate-golden-harness-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    public RunnerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Run_Seeded_Corpus_With_No_Parsers_Reports_Every_Case_Pending_And_Exits_Zero()
    {
        var (exitCode, output) = Run("--fixtures", SeededFixtures);

        exitCode.ShouldBe(0);
        output.ShouldContain("fixture");
        output.ShouldContain("verdict");

        foreach (var seeded in new[] { "junit-pytest/happy", "junit-pytest/truncated", "playwright-reporter/skeleton" })
        {
            RowFor(output, seeded).ShouldContain("PENDING");
        }

        // The corpus grows, so the count is the number of discovered cases: the summary must
        // agree with the rows, and the three seeded cases must still be discovered.
        var pending = PendingRows(output);
        pending.ShouldBeGreaterThanOrEqualTo(3);
        output.ShouldNotContain(" PASS ");
        output.ShouldContain("0 passed");
        output.ShouldContain("0 failed");
        output.ShouldContain($"{pending} pending");
        output.ShouldContain("0 error");
    }

    [Fact]
    public void Run_Seeded_Corpus_Under_Strict_Exits_One_While_Pending()
    {
        var (exitCode, output) = Run("--fixtures", SeededFixtures, "--strict");

        exitCode.ShouldBe(1);

        var pending = PendingRows(output);
        pending.ShouldBeGreaterThanOrEqualTo(3);
        output.ShouldContain($"{pending} pending");
        RowFor(output, "junit-pytest/happy").ShouldContain("PENDING");
    }

    [Fact]
    public void Run_Producer_Filter_Selects_Only_That_Producer()
    {
        CreateCase("happy", ExpectedJson("parsed", 0), producer: "junit-pytest");
        CreateCase("skeleton", ExpectedJson("parsed", 0), producer: "playwright-reporter");

        var (exitCode, output) = Run("--fixtures", _root, "--producer", "junit-pytest");

        exitCode.ShouldBe(0);
        output.ShouldContain("junit-pytest/happy");
        output.ShouldNotContain("playwright-reporter/skeleton");
        output.ShouldContain("1 pending");
    }

    [Fact]
    public void Run_Unknown_Producer_Is_A_Named_Error()
    {
        CreateCase("happy", ExpectedJson("parsed", 0), producer: "junit-pytest");

        var (exitCode, output) = Run("--fixtures", _root, "--producer", "nope");

        exitCode.ShouldBe(1);
        output.ShouldContain("ERROR");
        output.ShouldContain("nope");
    }

    [Fact]
    public void Run_Registered_Parser_That_Matches_Prints_Pass_And_Exits_Zero()
    {
        CreateCase(
            "happy",
            ExpectedJson("parsed", 2, Event(1, "case::one", "passed", "passed"), Event(2, "case::two", "failed", "failed")));

        var registry = Registry(ProducedDoc(
            ("case::one", 1, "passed", "passed"),
            ("case::two", 2, "failed", "failed")));

        var (exitCode, output) = Run(registry, "--fixtures", _root);

        exitCode.ShouldBe(0);
        output.ShouldContain("temp-producer/happy");
        output.ShouldContain("PASS");
        output.ShouldContain("1 passed");
        output.ShouldContain("0 pending");
    }

    [Fact]
    public void Run_Forced_Mismatch_Names_Fixture_Event_And_Field()
    {
        CreateCase(
            "mismatch",
            ExpectedJson(
                "parsed",
                2,
                Event(1, "case::one", "passed", "passed"),
                Event(2, "case::two", "failed", "failed")));

        var registry = Registry(ProducedDoc(
            ("case::one", 1, "passed", "passed"),
            ("case::two", 2, "failed", "passed")));

        var (exitCode, output) = Run(registry, "--fixtures", _root);

        exitCode.ShouldBe(1);
        output.ShouldContain("temp-producer/mismatch");
        output.ShouldMatch(@"event 1 field normalised_status");
        output.ShouldContain("1 failed");
    }

    [Fact]
    public void Run_Produced_Order_Does_Not_Decide_The_Diff()
    {
        CreateCase(
            "reordered",
            ExpectedJson("parsed", 2, Event(2, "case::two", "failed", "failed"), Event(1, "case::one", "passed", "passed")));

        var registry = Registry(ProducedDoc(
            ("case::one", 1, "passed", "passed"),
            ("case::two", 2, "failed", "failed")));

        var (exitCode, output) = Run(registry, "--fixtures", _root);

        exitCode.ShouldBe(0);
        output.ShouldContain("temp-producer/reordered");
        output.ShouldContain("1 passed");
    }

    [Fact]
    public void Run_Extra_Produced_Event_Fails()
    {
        CreateCase("extra", ExpectedJson("parsed", 1, Event(1, "case::one", "passed", "passed")));

        var registry = Registry(ProducedDoc(
            ("case::one", 1, "passed", "passed"),
            ("case::two", 2, "passed", "passed")));

        var (exitCode, output) = Run(registry, "--fixtures", _root);

        exitCode.ShouldBe(1);
        output.ShouldContain("temp-producer/extra");
        output.ShouldContain("extra_event");
    }

    private static (int ExitCode, string Output) Run(params string[] args) =>
        Run(new ParserRegistry(), args);

    private static (int ExitCode, string Output) Run(IParserRegistry registry, params string[] args)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var exitCode = HarnessRunner.Run(args, registry, writer);
        return (exitCode, writer.ToString());
    }

    private static ParserRegistry Registry(ExpectedDoc produced) =>
        new ParserRegistry().Register(ParserId, _ => produced);

    private static int PendingRows(string output) => output.Split("PENDING").Length - 1;

    /// <summary>The verdict row of one fixture, without its padding or its detail column.</summary>
    private static string RowFor(string output, string fixture) =>
        output.Split('\n').Single(line =>
            line.StartsWith(fixture, StringComparison.Ordinal)
            && line.Length > fixture.Length
            && line[fixture.Length] == ' ');

    private static ExpectedDoc ProducedDoc(string rawReportStatus, params (string CaseKey, long Sequence, string Raw, string Normalised)[] events) =>
        new(
            rawReportStatus,
            events.Length,
            events
                .Select(eventEntry => new NormalisedEvent(
                    Producer,
                    eventEntry.Sequence,
                    eventEntry.CaseKey,
                    eventEntry.Raw,
                    eventEntry.Normalised))
                .ToList());

    private static ExpectedDoc ProducedDoc(params (string CaseKey, long Sequence, string Raw, string Normalised)[] events) =>
        ProducedDoc("parsed", events);

    private static JsonObject Event(long sequence, string caseKey, string rawStatus, string normalisedStatus) =>
        new()
        {
            ["producer_id"] = Producer,
            ["sequence"] = sequence,
            ["case_key"] = caseKey,
            ["raw_status"] = rawStatus,
            ["normalised_status"] = normalisedStatus,
        };

    private static string ExpectedJson(string rawReportStatus, int declaredCount, params JsonObject[] events) =>
        new JsonObject
        {
            ["raw_report_status"] = rawReportStatus,
            ["declared_count"] = declaredCount,
            ["events"] = new JsonArray(events.Cast<JsonNode>().ToArray()),
        }.ToJsonString();

    private static string CaseJson(string producer, string parserId, string? parserStatus)
    {
        var meta = new JsonObject
        {
            ["producer_id"] = producer,
            ["parser_id"] = parserId,
            ["parser_version"] = "0.0.0-pending",
            ["rationale"] = "synthetic case written by the runner tests",
        };

        if (parserStatus is not null)
        {
            meta["parser_status"] = parserStatus;
        }

        return meta.ToJsonString();
    }

    private string CreateCase(
        string name,
        string? expectedJson,
        string producer = Producer,
        string parserId = ParserId,
        string? parserStatus = "pending")
    {
        var directory = Path.Combine(_root, producer, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "case.json"), CaseJson(producer, parserId, parserStatus));
        File.WriteAllText(Path.Combine(directory, "report.xml"), "<testsuites />");

        if (expectedJson is not null)
        {
            File.WriteAllText(Path.Combine(directory, "expected.json"), expectedJson);
        }

        return directory;
    }

    private void CopySeededCase(string producer, string name)
    {
        var source = Path.Combine(SeededFixtures, producer, name);
        var target = Path.Combine(_root, producer, name);
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
    }

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

/// <summary>
/// The parser table itself: a miss is a result, and a duplicate id is refused so one
/// producer's parser cannot silently replace another's.
/// </summary>
public sealed class ParserRegistryTests
{
    [Fact]
    public void TryGet_Unregistered_Id_Returns_False()
    {
        var registry = new ParserRegistry();

        registry.TryGet("absent", out var parse).ShouldBeFalse();
        parse.ShouldBeNull();
    }

    [Fact]
    public void Register_Then_TryGet_Returns_The_Same_Parser()
    {
        ParseFixture parser = _ => new ExpectedDoc("parsed", 0, []);

        var registry = new ParserRegistry().Register("junit-pytest", parser);

        registry.TryGet("junit-pytest", out var resolved).ShouldBeTrue();
        resolved.ShouldBeSameAs(parser);
    }

    [Fact]
    public void Register_Duplicate_Id_Is_Refused()
    {
        var registry = new ParserRegistry().Register("junit-pytest", _ => new ExpectedDoc("parsed", 0, []));

        Should.Throw<InvalidOperationException>(
            () => registry.Register("junit-pytest", _ => new ExpectedDoc("parsed", 0, [])));
    }

    [Fact]
    public void Register_With_An_Empty_Id_Is_Refused()
    {
        var registry = new ParserRegistry();

        Should.Throw<ArgumentException>(() => registry.Register(" ", _ => new ExpectedDoc("parsed", 0, [])));
    }
}
