using System.Security.Cryptography;
using System.Text;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Foundation.IntegrationTests;

/// <summary>
/// 01.6.1-T3.1 (AC6): the .NET half of the shared fixture corpus. The same
/// <c>fixtures/reports/junit/sample</c> bytes are loaded by
/// <c>packages/test-fixtures/src/loadFixture.test.ts</c>, which pins the same
/// <c>sha256:</c> digest of <c>expected.json</c> — a byte-level proof that both
/// runtimes read the same corpus.
/// <para>
/// The class deliberately does not extend <see cref="HarnessTest"/> or join
/// <see cref="HarnessSuite"/>: loading files needs neither the container nor the
/// migrated database (<c>DatabaseFixture</c> starts its container lazily, so
/// this class never starts one and runs where no Docker daemon exists).
/// </para>
/// </summary>
public sealed class FixtureLoaderTests
{
    private const string SampleDirectory = "reports/junit/sample";

    /// <summary>
    /// The <c>sha256:</c> of <c>fixtures/reports/junit/sample/expected.json</c>;
    /// pinned identically in <c>loadFixture.test.ts</c>, so a byte change that
    /// only one runtime notices fails here.
    /// </summary>
    private const string SampleExpectedSha256 =
        "sha256:722cd0645ed9bc4ee524514cf4856c8e718c21beb23a2941393efacd7ee2090d";

    private const string InputXml = "<testsuite tests=\"1\"></testsuite>";

    private const string ValidMeta =
        "{\"domain\":\"tmp\",\"kind\":\"junit\",\"source\":\"handcrafted\",\"added_by\":\"tests\"}";

    /// <summary>
    /// AC6 round-trip: one fixture loaded by the .NET loader carries the same
    /// domain/kind/name, input bytes and expected document the TS loader sees.
    /// </summary>
    [Fact]
    public void FixtureLoader_RoundTrip_Sample()
    {
        var fixture = FixtureLoader.Load(FixtureLoader.Fixtures(SampleDirectory));

        fixture.Domain.ShouldBe("reports");
        fixture.Kind.ShouldBe("junit");
        fixture.Name.ShouldBe("sample");
        fixture.Meta.ShouldBe(new FixtureMeta("reports", "junit", "handcrafted", "01.6.1-T3.2"));
        Path.GetFileName(fixture.InputPath).ShouldBe("input.xml");
        fixture.DirectoryPath.ShouldBe(FixtureLoader.Fixtures(SampleDirectory));

        fixture.InputText.ShouldContain("<testsuite");
        fixture.InputText.ShouldContain("tests=\"3\"");

        var expected = fixture.Expected.ShouldNotBeNull();
        var summary = expected["summary"].ShouldNotBeNull();
        summary["tests"]!.GetValue<int>().ShouldBe(3);
        summary["failures"]!.GetValue<int>().ShouldBe(1);
        summary["errors"]!.GetValue<int>().ShouldBe(0);
        summary["skipped"]!.GetValue<int>().ShouldBe(1);
        summary["durationMs"]!.GetValue<int>().ShouldBe(420);

        var events = expected["events"]!.AsArray();
        events.Count.ShouldBe(3);
        events[0]!["outcome"]!.GetValue<string>().ShouldBe("passed");
        events[1]!["outcome"]!.GetValue<string>().ShouldBe("failed");
        events[1]!["message"]!.GetValue<string>().ShouldBe("expected 2 but was 1");
        events[2]!["outcome"]!.GetValue<string>().ShouldBe("skipped");
        expected["producer"]!.GetValue<string>().ShouldBe("junit");

        // The corpus contract pins expected.json as the RFC 8785 canonical byte
        // sequence: canonical JSON never contains a raw newline (line breaks in
        // strings are escaped), so a single line with no trailing newline is a
        // necessary — and here checked — condition. Byte-exact canonicality is
        // asserted by packages/test-fixtures/src/goldenFile.ts on the TS side.
        var expectedText = fixture.ExpectedText.ShouldNotBeNull();
        expectedText.ShouldNotContain("\n");

        // Cross-runtime byte parity: the digest the TS loader test pins.
        var digest = "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture.DirectoryPath, "expected.json"))));
        digest.ShouldBe(SampleExpectedSha256);
    }

    /// <summary>
    /// AC6 negative: the loader refuses every directory that is not exactly the
    /// shape of 01.6.1 §4 — a malformed corpus entry must fail loudly rather than
    /// silently pass through or be skipped.
    /// </summary>
    [Theory]
    [InlineData("missing-meta", "meta.json is missing")]
    [InlineData("meta-not-object", "meta.json must be a JSON object")]
    [InlineData("meta-missing-field", "meta.json must have exactly the fields")]
    [InlineData("meta-extra-field", "meta.json must have exactly the fields")]
    [InlineData("meta-non-string", "meta.json field 'kind' must be a non-empty string")]
    [InlineData("meta-empty", "meta.json field 'source' must be a non-empty string")]
    [InlineData("meta-null-field", "meta.json field 'source' must be a non-empty string")]
    [InlineData("meta-invalid-json", "meta.json is not valid JSON")]
    [InlineData("meta-bom", "'meta.json' must be UTF-8 without BOM")]
    [InlineData("no-input", "an input.<ext> file is missing")]
    [InlineData("two-inputs", "more than one input.* file")]
    [InlineData("input-no-extension", "the input file has no extension")]
    [InlineData("stray-file", "unexpected file 'extra.txt'")]
    [InlineData("stray-dir", "unexpected subdirectory 'nested'")]
    [InlineData("malformed-expected", "expected.json is not valid JSON")]
    [InlineData("expected-bom", "'expected.json' must be UTF-8 without BOM")]
    [InlineData("meta-duplicate-key", "meta.json is not valid JSON")]
    [InlineData("expected-duplicate-key", "expected.json is not valid JSON")]
    [InlineData("input-bom", "'input.xml' must be UTF-8 without BOM")]
    public void FixtureLoader_UnknownShape_Rejects(string caseName, string problem)
    {
        using var directory = new TempFixtureDirectory();
        Populate(directory, caseName);

        var exception = Should.Throw<FixtureShapeException>(() => FixtureLoader.Load(directory.Root));

        exception.FixtureDirectory.ShouldBe(directory.Root);
        exception.Message.ShouldContain(problem);
    }

    /// <summary><c>expected.json</c> is optional: meta + input alone is a valid fixture.</summary>
    [Fact]
    public void FixtureLoader_ExpectedJson_Optional()
    {
        using var directory = new TempFixtureDirectory();
        directory.WriteFile("meta.json", ValidMeta);
        directory.WriteFile("input.xml", InputXml);

        var fixture = FixtureLoader.Load(directory.Root);

        fixture.Domain.ShouldBe("tmp");
        fixture.Name.ShouldBe(Path.GetFileName(directory.Root));
        fixture.Expected.ShouldBeNull();
        fixture.ExpectedText.ShouldBeNull();
        fixture.InputText.ShouldBe(InputXml);
    }

    /// <summary><see cref="FixtureLoader.TryLoad"/> reports the refusal instead of throwing.</summary>
    [Fact]
    public void FixtureLoader_TryLoad_ReportsProblem()
    {
        var missing = Path.Combine(Path.GetTempPath(), "evidgate-missing-" + Guid.NewGuid().ToString("N"));

        FixtureLoader.TryLoad(missing, out var fixture, out var problem).ShouldBeFalse();
        fixture.ShouldBeNull();
        problem.ShouldNotBeNull();
        problem.ShouldContain("fixture directory does not exist");

        using var valid = new TempFixtureDirectory();
        valid.WriteFile("meta.json", ValidMeta);
        valid.WriteFile("input.xml", InputXml);

        FixtureLoader.TryLoad(valid.Root, out var loaded, out var noProblem).ShouldBeTrue();
        loaded.ShouldNotBeNull();
        noProblem.ShouldBeNull();
    }

    /// <summary>
    /// Corpus walk: the reports domain currently contains exactly the sample, and
    /// the full corpus walk finds it too (sibling families without
    /// <c>meta.json</c>/<c>input.*</c>, like <c>rfc8785/</c>, are skipped).
    /// </summary>
    [Fact]
    public void FixtureLoader_LoadAll_FindsSample()
    {
        var reports = FixtureLoader.LoadAll(FixtureLoader.Fixtures("reports"));

        var sample = reports.ShouldHaveSingleItem();
        sample.Domain.ShouldBe("reports");
        sample.Name.ShouldBe("sample");

        var corpus = FixtureLoader.LoadAll(FixtureLoader.FixturesRoot);
        corpus.ShouldContain(fixture => fixture.Domain == "reports" && fixture.Name == "sample");
        corpus.ShouldAllBe(fixture =>
            fixture.Meta.Domain.Length > 0 &&
            fixture.Meta.Kind.Length > 0 &&
            File.Exists(fixture.InputPath));
    }

    /// <summary>A missing corpus root is a refusal, not an empty list.</summary>
    [Fact]
    public void FixtureLoader_LoadAll_MissingRoot_Rejects()
    {
        var missing = Path.Combine(Path.GetTempPath(), "evidgate-no-root-" + Guid.NewGuid().ToString("N"));

        Should.Throw<FixtureShapeException>(() => FixtureLoader.LoadAll(missing))
            .Message.ShouldContain("fixtures root does not exist");
    }

    /// <summary>
    /// An empty or whitespace-only path is a shape refusal (not an
    /// <see cref="ArgumentException"/>), with the same
    /// <c>&lt;empty&gt;</c>-pathed message <c>loadFixture.ts</c>/
    /// <c>loadAllFixtures</c> throw — and <see cref="FixtureLoader.TryLoad"/>
    /// reports exactly that exception's message.
    /// </summary>
    [Fact]
    public void FixtureLoader_EmptyPath_Rejects()
    {
        var loadException = Should.Throw<FixtureShapeException>(() => FixtureLoader.Load("   "));
        loadException.FixtureDirectory.ShouldBe("<empty>");
        loadException.Message.ShouldBe("<empty>: fixture directory is empty");

        var loadAllException = Should.Throw<FixtureShapeException>(() => FixtureLoader.LoadAll(""));
        loadAllException.Message.ShouldBe("<empty>: fixtures root is empty");

        FixtureLoader.TryLoad("", out var fixture, out var problem).ShouldBeFalse();
        fixture.ShouldBeNull();
        problem.ShouldBe("<empty>: fixture directory is empty");
    }

    private static void Populate(TempFixtureDirectory directory, string caseName)
    {
        switch (caseName)
        {
            case "missing-meta":
                directory.WriteFile("input.xml", InputXml);
                break;
            case "meta-not-object":
                directory.WriteFile("meta.json", "[]");
                directory.WriteFile("input.xml", InputXml);
                break;
            case "meta-missing-field":
                directory.WriteFile(
                    "meta.json",
                    "{\"domain\":\"tmp\",\"source\":\"handcrafted\",\"added_by\":\"tests\"}");
                directory.WriteFile("input.xml", InputXml);
                break;
            case "meta-extra-field":
                directory.WriteFile(
                    "meta.json",
                    "{\"domain\":\"tmp\",\"kind\":\"junit\",\"source\":\"handcrafted\",\"added_by\":\"tests\",\"unknown\":1}");
                directory.WriteFile("input.xml", InputXml);
                break;
            case "meta-non-string":
                directory.WriteFile(
                    "meta.json",
                    "{\"domain\":\"tmp\",\"kind\":42,\"source\":\"handcrafted\",\"added_by\":\"tests\"}");
                directory.WriteFile("input.xml", InputXml);
                break;
            case "meta-empty":
                directory.WriteFile(
                    "meta.json",
                    "{\"domain\":\"tmp\",\"kind\":\"junit\",\"source\":\"\",\"added_by\":\"tests\"}");
                directory.WriteFile("input.xml", InputXml);
                break;
            case "meta-null-field":
                // A present-but-null field is a value refusal, not a key-set
                // refusal — the same classification loadFixture.ts applies.
                directory.WriteFile(
                    "meta.json",
                    "{\"domain\":\"tmp\",\"kind\":\"junit\",\"source\":null,\"added_by\":\"tests\"}");
                directory.WriteFile("input.xml", InputXml);
                break;
            case "meta-invalid-json":
                directory.WriteFile("meta.json", "not json");
                directory.WriteFile("input.xml", InputXml);
                break;
            case "meta-duplicate-key":
                // System.Text.Json refuses duplicate member names on first access;
                // loadFixture.ts refuses the same document by scanning the raw text
                // (JSON.parse alone is last-wins), so both runtimes refuse it.
                directory.WriteFile(
                    "meta.json",
                    "{\"domain\":\"a\",\"domain\":\"b\",\"kind\":\"junit\",\"source\":\"handcrafted\",\"added_by\":\"tests\"}");
                directory.WriteFile("input.xml", InputXml);
                break;
            case "expected-duplicate-key":
                // Same duplicate-member refusal, on expected.json — pinned so a
                // refactor dropping the Materialize walk regresses visibly.
                directory.WriteFile("meta.json", ValidMeta);
                directory.WriteFile("input.xml", InputXml);
                directory.WriteFile("expected.json", "{\"a\":1,\"a\":2}");
                break;
            case "meta-bom":
                directory.WriteFileWithBom("meta.json", ValidMeta);
                directory.WriteFile("input.xml", InputXml);
                break;
            case "no-input":
                directory.WriteFile("meta.json", ValidMeta);
                break;
            case "two-inputs":
                directory.WriteFile("meta.json", ValidMeta);
                directory.WriteFile("input.xml", InputXml);
                directory.WriteFile("input.txt", "plain");
                break;
            case "input-no-extension":
                directory.WriteFile("meta.json", ValidMeta);
                directory.WriteFile("input.", InputXml);
                break;
            case "stray-file":
                directory.WriteFile("meta.json", ValidMeta);
                directory.WriteFile("input.xml", InputXml);
                directory.WriteFile("extra.txt", "stray");
                break;
            case "stray-dir":
                directory.WriteFile("meta.json", ValidMeta);
                directory.WriteFile("input.xml", InputXml);
                Directory.CreateDirectory(Path.Combine(directory.Root, "nested"));
                break;
            case "malformed-expected":
                directory.WriteFile("meta.json", ValidMeta);
                directory.WriteFile("input.xml", InputXml);
                directory.WriteFile("expected.json", "{");
                break;
            case "expected-bom":
                directory.WriteFile("meta.json", ValidMeta);
                directory.WriteFile("input.xml", InputXml);
                directory.WriteFileWithBom("expected.json", "{}");
                break;
            case "input-bom":
                // The payload file gets the same BOM refusal the JSON files get;
                // File.ReadAllText would have silently stripped it.
                directory.WriteFile("meta.json", ValidMeta);
                directory.WriteFileWithBom("input.xml", InputXml);
                break;
            default:
                throw new InvalidOperationException($"unknown case '{caseName}'");
        }
    }

    /// <summary>A throwaway fixture directory, deleted with the test.</summary>
    private sealed class TempFixtureDirectory : IDisposable
    {
        public TempFixtureDirectory() => Directory.CreateDirectory(Root);

        public string Root { get; } = Path.Combine(
            Path.GetTempPath(),
            "evidgate-fixture-" + Guid.NewGuid().ToString("N"));

        public void WriteFile(string name, string content) =>
            File.WriteAllText(Path.Combine(Root, name), content);

        /// <summary>Writes the content with a UTF-8 BOM prefix — an unknown shape.</summary>
        public void WriteFileWithBom(string name, string content) =>
            File.WriteAllBytes(
                Path.Combine(Root, name),
                [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(content)]);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
