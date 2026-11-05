using System.Text;
using Evidgate.GoldenHarness;
using Shouldly;
using Xunit;

namespace GoldenHarness.UnitTests;

/// <summary>
/// The layout contract: a case without <c>case.json</c>, <c>expected.json</c> or a report file
/// fails the scan by name, an empty corpus is a clean empty result, and the older
/// <c>meta.json</c> corpus and the protocol <c>contracts</c> namespace are never read as cases.
/// </summary>
public sealed class LayoutTests
{
    private const string CaseJson =
        """{"producer_id":"junit-pytest","parser_id":"junit_pytest","parser_version":"3.1","rationale":"captured from a real run"}""";

    private const string ExpectedJson =
        """{"raw_report_status":"parsed","declared_count":1,"events":[{"producer_id":"junit-pytest","sequence":0,"case_key":"tests/test_a.py::test_a","raw_status":"passed","normalised_status":"passed"}]}""";

    private const string MetaJson =
        """{"domain":"legacy-domain","kind":"junit","source":"a real run","added_by":"the corpus loader"}""";

    private const string PendingCaseJson =
        """{"producer_id":"junit-pytest","parser_id":"junit_pytest","parser_version":"3.1","rationale":"seed","parser_status":"pending"}""";

    private const string SkippedCaseJson =
        """{"producer_id":"junit-pytest","parser_id":"junit_pytest","parser_version":"3.1","rationale":"seed","parser_status":"skipped"}""";

    [Fact]
    public void Fixture_MissingExpected_FailsNamed()
    {
        var root = NewRoot();
        try
        {
            var directory = CaseDirectory(root, "junit-pytest", "happy");
            WriteCaseFiles(directory, expectedJson: null, reports: "report.xml");

            var error = Should.Throw<FixtureLayoutException>(() => FixtureLayout.Scan(root));

            error.Fixture.ShouldBe("junit-pytest/happy");
            error.Message.ShouldBe("junit-pytest/happy: expected.json is missing");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fixture_MissingCaseJson_FailsNamed()
    {
        var root = NewRoot();
        try
        {
            var directory = CaseDirectory(root, "junit-pytest", "happy");
            WriteCaseFiles(directory, caseJson: null, reports: "report.xml");

            var error = Should.Throw<FixtureLayoutException>(() => FixtureLayout.Scan(root));

            error.Fixture.ShouldBe("junit-pytest/happy");
            error.Message.ShouldContain("case.json is missing");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fixture_MissingReportFile_FailsNamed()
    {
        var root = NewRoot();
        try
        {
            var directory = CaseDirectory(root, "junit-pytest", "happy");
            WriteCaseFiles(directory);
            File.WriteAllText(Path.Combine(directory, "README.md"), "why this case exists");

            var error = Should.Throw<FixtureLayoutException>(() => FixtureLayout.Scan(root));

            error.Fixture.ShouldBe("junit-pytest/happy");
            // README.md documents a case; it is never the report the parser runs on.
            error.Message.ShouldContain("no original report file");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fixture_MalformedJson_FailsNamed()
    {
        var root = NewRoot();
        try
        {
            var directory = CaseDirectory(root, "junit-pytest", "happy");
            WriteCaseFiles(directory, expectedJson: """{"raw_report_status":""", reports: "report.xml");

            var error = Should.Throw<FixtureLayoutException>(() => FixtureLayout.Scan(root));

            error.Fixture.ShouldBe("junit-pytest/happy");
            error.Message.ShouldContain("'expected.json' is not valid JSON");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fixture_DuplicateMemberName_FailsNamed()
    {
        var root = NewRoot();
        try
        {
            var directory = CaseDirectory(root, "junit-pytest", "happy");
            WriteCaseFiles(
                directory,
                expectedJson: """{"raw_report_status":"parsed","declared_count":1,"declared_count":2,"events":[]}""",
                reports: "report.xml");

            var error = Should.Throw<FixtureLayoutException>(() => FixtureLayout.Scan(root));

            error.Fixture.ShouldBe("junit-pytest/happy");
            error.Message.ShouldContain("'expected.json' is not valid JSON");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fixture_Bom_FailsNamed()
    {
        var root = NewRoot();
        try
        {
            var directory = CaseDirectory(root, "junit-pytest", "happy");
            WriteCaseFiles(directory, expectedJson: null, reports: "report.xml");
            File.WriteAllText(
                Path.Combine(directory, "expected.json"),
                ExpectedJson,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var error = Should.Throw<FixtureLayoutException>(() => FixtureLayout.Scan(root));

            error.Fixture.ShouldBe("junit-pytest/happy");
            error.Message.ShouldContain("must be UTF-8 without BOM");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fixture_MissingField_FailsNamed()
    {
        var root = NewRoot();
        try
        {
            var directory = CaseDirectory(root, "junit-pytest", "happy");
            WriteCaseFiles(
                directory,
                caseJson: """{"producer_id":"junit-pytest","parser_id":"junit_pytest","parser_version":"3.1"}""",
                reports: "report.xml");

            var error = Should.Throw<FixtureLayoutException>(() => FixtureLayout.Scan(root));

            error.Message.ShouldContain("field 'rationale' must be a non-empty string");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fixture_EmptyRoot_ReturnsEmpty()
    {
        var root = NewRoot();
        try
        {
            FixtureLayout.Scan(root).ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fixture_HappyCase_ReadsEveryField()
    {
        var root = NewRoot();
        try
        {
            var directory = CaseDirectory(root, "junit-pytest", "happy");
            WriteCaseFiles(directory, reports: "report.xml");

            var fixture = FixtureLayout.Scan(root).ShouldHaveSingleItem();

            fixture.Producer.ShouldBe("junit-pytest");
            fixture.Name.ShouldBe("happy");
            fixture.Directory.ShouldBe(directory);
            fixture.ReportFiles.ShouldBe(["report.xml"]);

            fixture.Meta.ShouldBe(new CaseMeta("junit-pytest", "junit_pytest", "3.1", "captured from a real run", null));
            fixture.Expected.RawReportStatus.ShouldBe("parsed");
            fixture.Expected.DeclaredCount.ShouldBe(1);
            fixture.Expected.Events.ShouldBe(
                [new NormalisedEvent("junit-pytest", 0, "tests/test_a.py::test_a", "passed", "passed")]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fixture_Scan_IsOrdinalByProducerThenCase()
    {
        var root = NewRoot();
        try
        {
            WriteCaseFiles(CaseDirectory(root, "junit-pytest", "b-case"), reports: "report.xml");
            WriteCaseFiles(CaseDirectory(root, "junit-pytest", "a-case"), reports: "report.xml");
            WriteCaseFiles(CaseDirectory(root, "Playwright", "only-case"), reports: "report.xml");

            var names = FixtureLayout.Scan(root).Select(fixture => fixture.Producer + "/" + fixture.Name);

            // Ordinal, not culture order: 'P' sorts before 'j', so re-running the scan on any
            // machine prints the corpus in one order.
            names.ShouldBe(["Playwright/only-case", "junit-pytest/a-case", "junit-pytest/b-case"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fixture_ReportFiles_AreOrdinalAndExcludeReservedNames()
    {
        var root = NewRoot();
        try
        {
            var directory = CaseDirectory(root, "junit-pytest", "shard-merged");
            WriteCaseFiles(directory, reports: ["shard-2.xml", "shard-1.xml"]);
            File.WriteAllText(Path.Combine(directory, "README.md"), "why this case exists");

            var fixture = FixtureLayout.Scan(root).ShouldHaveSingleItem();

            fixture.ReportFiles.ShouldBe(["shard-1.xml", "shard-2.xml"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fixture_MetaJsonCorpus_AndContractsNamespace_AreSkipped()
    {
        var root = NewRoot();
        try
        {
            // The older corpus: a case-shaped directory whose meta.json marks it as another
            // namespace, so even its expected.json must not be read as a case.
            var legacy = Path.Combine(root, "legacy-domain", "a-case");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "meta.json"), MetaJson);
            File.WriteAllText(Path.Combine(legacy, "input.xml"), "<testsuite/>");
            File.WriteAllText(Path.Combine(legacy, "expected.json"), ExpectedJson);

            var contracts = Path.Combine(root, "contracts", "adapter");
            Directory.CreateDirectory(contracts);
            WriteCaseFiles(contracts, reports: "report.xml");

            WriteCaseFiles(CaseDirectory(root, "junit-pytest", "happy"), reports: "report.xml");

            var fixture = FixtureLayout.Scan(root).ShouldHaveSingleItem();

            fixture.Producer.ShouldBe("junit-pytest");
            fixture.Name.ShouldBe("happy");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fixture_ProducerFilter_ReadsOnlyThatProducer()
    {
        var root = NewRoot();
        try
        {
            WriteCaseFiles(CaseDirectory(root, "junit-pytest", "happy"), reports: "report.xml");
            WriteCaseFiles(CaseDirectory(root, "playwright-reporter", "happy"), reports: "report.xml");

            var fixture = FixtureLayout.Scan(root, "playwright-reporter").ShouldHaveSingleItem();

            fixture.Producer.ShouldBe("playwright-reporter");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("unknown_producer")]
    [InlineData("../junit-pytest")]
    public void Fixture_UnusableProducer_FailsNamed(string producer)
    {
        var root = NewRoot();
        try
        {
            var error = Should.Throw<FixtureLayoutException>(() => FixtureLayout.Scan(root, producer));

            error.Fixture.ShouldBe(producer);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fixture_CaseFilesDirectlyUnderProducer_FailsNamed()
    {
        var root = NewRoot();
        try
        {
            WriteCaseFiles(CaseDirectory(root, "junit-pytest", "."), reports: "report.xml");

            var error = Should.Throw<FixtureLayoutException>(() => FixtureLayout.Scan(root));

            error.Fixture.ShouldBe("junit-pytest");
            error.Message.ShouldContain("own directory below the producer");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fixture_ParserStatusPending_IsCarried()
    {
        var root = NewRoot();
        try
        {
            WriteCaseFiles(
                CaseDirectory(root, "junit-pytest", "waiting"),
                caseJson: PendingCaseJson,
                reports: "report.xml");

            var fixture = FixtureLayout.Scan(root).ShouldHaveSingleItem();

            fixture.Meta.ParserStatus.ShouldBe("pending");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fixture_UnknownParserStatus_FailsNamed()
    {
        var root = NewRoot();
        try
        {
            WriteCaseFiles(
                CaseDirectory(root, "junit-pytest", "happy"),
                caseJson: SkippedCaseJson,
                reports: "report.xml");

            var error = Should.Throw<FixtureLayoutException>(() => FixtureLayout.Scan(root));

            error.Message.ShouldContain("field 'parser_status', when present, must be \"pending\"");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("""[]""", "'expected.json' must be a JSON object")]
    [InlineData(
        """{"raw_report_status":"unknown","declared_count":0,"events":[]}""",
        "field 'raw_report_status' must be one of")]
    [InlineData(
        """{"raw_report_status":"parsed","declared_count":0,"events":{}}""",
        "field 'events' must be an array")]
    [InlineData(
        """{"raw_report_status":"parsed","declared_count":0,"events":[1]}""",
        "'expected.json' events[0] must be a JSON object")]
    [InlineData(
        """{"raw_report_status":"parsed","declared_count":-1,"events":[]}""",
        "field 'declared_count' must be a non-negative integer")]
    public void Fixture_RefusedExpectedShapes_FailNamed(string expectedJson, string problem)
    {
        var root = NewRoot();
        try
        {
            var directory = CaseDirectory(root, "junit-pytest", "happy");
            WriteCaseFiles(directory, expectedJson: expectedJson, reports: "report.xml");

            var error = Should.Throw<FixtureLayoutException>(() => FixtureLayout.Scan(root));

            error.Fixture.ShouldBe("junit-pytest/happy");
            error.Message.ShouldContain(problem);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "evidgate-golden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CaseDirectory(string root, string producer, string caseName)
    {
        var directory = Path.GetFullPath(Path.Combine(root, producer, caseName));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteCaseFiles(
        string directory,
        string? caseJson = CaseJson,
        string? expectedJson = ExpectedJson,
        params string[] reports)
    {
        if (caseJson is not null)
        {
            File.WriteAllText(Path.Combine(directory, "case.json"), caseJson);
        }

        if (expectedJson is not null)
        {
            File.WriteAllText(Path.Combine(directory, "expected.json"), expectedJson);
        }

        foreach (var report in reports)
        {
            File.WriteAllText(Path.Combine(directory, report), "<testsuite/>");
        }
    }
}
