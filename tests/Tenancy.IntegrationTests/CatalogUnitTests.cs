using System.Text.Json;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 03.1.1-T1.2/T1.3 and the discovery rules (AC1–AC5) without a database: the annotation parser, the repository path
/// rules, the sidecar reader (untrusted YAML) and the tree-to-cases resolution.
/// </summary>
[Collection(ExclusiveTenancySuite.Name)]
public sealed partial class CatalogUnitTests
{
    private const string IdA = "018f2f0e-8a52-7c4b-9d3e-5a1b2c3d4e5f";
    private const string IdB = "018f2f0e-8a52-7c4b-9d3e-5a1b2c3d4e60";

    private static readonly ContractSchemaSet Schemas = ContractSchemaSet.Load(
        Path.Combine(Path.GetDirectoryName(PostgresTenancyFixture.RepoMigrationsPath)!, "contracts"));

    // ------------------------------------------------------------------ AnnotationParser

    [Theory]
    [InlineData($"// evidgate.id: {IdA}\ntest('x', () => {{}});")]
    [InlineData($"# evidgate.id: {IdA}\ndef test_x(): pass")]
    [InlineData($"/* evidgate.id: {IdA} */ test('x')")]
    [InlineData($"/**\n * @evidgate.id {IdA}\n */")]
    [InlineData($"-- evidgate.id = {IdA}")]
    [InlineData($"@evidgate.id('{IdA}')\npublic void X() {{}}")]
    [InlineData($"test('x', {{ annotation: {{ type: 'evidgate.id', description: '{IdA}' }} }}, async () => {{}});")]
    [InlineData($"    // evidgate.id: {IdA}")]
    public void Parser_RecognisesEveryAnnotationSpelling(string source)
    {
        var result = AnnotationParser.Parse(source);

        result.Outcome.ShouldBe(AnnotationOutcome.Valid);
        result.Id.ShouldBe(Guid.Parse(IdA));
    }

    [Fact]
    public void Parser_UppercaseUuid_IsTheSameId()
    {
        AnnotationParser.Parse($"// evidgate.id: {IdA.ToUpperInvariant()}").Id.ShouldBe(Guid.Parse(IdA));
    }

    [Theory]
    [InlineData("// evidgate.id: not-a-uuid")]
    [InlineData("// evidgate.id: 018f2f0e8a527c4b9d3e5a1b2c3d4e5f")]
    [InlineData("// evidgate.id: {018f2f0e-8a52-7c4b-9d3e-5a1b2c3d4e5f}")]
    [InlineData("// evidgate.id: 00000000-0000-0000-0000-000000000000")]
    [InlineData("// evidgate.id:")]
    public void Parser_RefusesNonCanonicalValues(string source)
    {
        AnnotationParser.Parse(source).Outcome.ShouldBe(AnnotationOutcome.Invalid);
    }

    [Theory]
    [InlineData("test('x', () => { const s = 'evidgate.id: " + IdA + "'; });")]
    [InlineData("const evidgate = { id: '" + IdA + "' };")]
    [InlineData("")]
    [InlineData("// nothing to see")]
    public void Parser_IgnoresPlainStringsAndCode(string source)
    {
        AnnotationParser.Parse(source).Outcome.ShouldBe(AnnotationOutcome.None);
    }

    [Fact]
    public void Parser_TwoAnnotations_AreMultiple_EvenWhenEqual()
    {
        AnnotationParser.Parse($"// evidgate.id: {IdA}\n// evidgate.id: {IdA}").Outcome.ShouldBe(AnnotationOutcome.Multiple);
        AnnotationParser.Parse($"// evidgate.id: {IdA}\n@evidgate.id('{IdB}')").Outcome.ShouldBe(AnnotationOutcome.Multiple);
    }

    [Fact]
    public void Parser_CommentedDecorator_IsOneAnnotation_NotTwo()
    {
        AnnotationParser.Parse($"// @evidgate.id('{IdA}')").Outcome.ShouldBe(AnnotationOutcome.Valid);
    }

    [Fact]
    public void Parser_OversizedContent_IsNotScanned()
    {
        AnnotationParser.Parse(new string('a', AnnotationParser.MaxContentChars + 1) + $"\n// evidgate.id: {IdA}").Outcome
            .ShouldBe(AnnotationOutcome.None);
    }

    [Fact]
    public void Parser_PathologicalInput_FinishesQuickly()
    {
        var hostile = string.Concat(Enumerable.Repeat("// evidgate.id:      ", 20_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        _ = AnnotationParser.Parse(hostile);

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
    }

    // ------------------------------------------------------------------ RepoPath

    [Theory]
    [InlineData("a.spec.ts", true)]
    [InlineData("tests/sub/a.spec.ts", true)]
    [InlineData("", false)]
    [InlineData("/abs/a.ts", false)]
    [InlineData("a/../b.ts", false)]
    [InlineData("a/./b.ts", false)]
    [InlineData("a//b.ts", false)]
    [InlineData("a\\b.ts", false)]
    [InlineData("dir/", false)]
    [InlineData("a\u0001b.ts", false)]
    public void RepoPath_IsValid(string path, bool expected) => RepoPath.IsValid(path).ShouldBe(expected);

    [Fact]
    public void RepoPath_LongPaths_AreRefused() => RepoPath.IsValid(new string('a', 1025)).ShouldBeFalse();

    [Fact]
    public void RepoPath_Parts()
    {
        RepoPath.DirectoryOf("a/b/c.ts").ShouldBe("a/b");
        RepoPath.DirectoryOf("c.ts").ShouldBe(string.Empty);
        RepoPath.FileNameOf("a/b/c.ts").ShouldBe("c.ts");
        RepoPath.FileNameOf("c.ts").ShouldBe("c.ts");
        RepoPath.Combine("a/b", "c.ts").ShouldBe("a/b/c.ts");
        RepoPath.Combine(string.Empty, "c.ts").ShouldBe("c.ts");
        RepoPath.Combine("a", "../c.ts").ShouldBeNull();
        RepoPath.Combine("a", "/c.ts").ShouldBeNull();
    }

    // ------------------------------------------------------------------ SidecarManifestReader

    private static SidecarReadResult Read(string yaml, string path = "tests/evidgate.cases.yaml") =>
        new SidecarManifestReader(new Lazy<ContractSchemaSet>(Schemas)).Read(path, yaml);

    [Fact]
    public void Sidecar_ValidManifest_ResolvesPathsRelativeToItsDirectory()
    {
        var result = Read($"version: 1\ncases:\n  - file: login.spec.ts\n    evidgate.id: {IdA}\n  - file: sub/cart.spec.ts\n    evidgate.id: \"{IdB}\"\n    name: Cart totals\n");

        result.Problems.ShouldBeEmpty();
        result.Entries.Select(entry => (entry.FullPath, entry.Id, entry.Name)).ShouldBe(
        [
            ("tests/login.spec.ts", Guid.Parse(IdA), (string?)null),
            ("tests/sub/cart.spec.ts", Guid.Parse(IdB), "Cart totals"),
        ]);
    }

    [Fact]
    public void Sidecar_EmptyCases_IsValid()
    {
        var result = Read("version: 1\ncases: []\n");

        result.Entries.ShouldBeEmpty();
        result.Problems.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("version: 2\ncases: []\n")]
    [InlineData("cases: []\n")]
    [InlineData("version: 1\ncases:\n  - file: a.ts\n")]
    [InlineData("version: 1\ncases:\n  - file: a.ts\n    evidgate.id: nope\n")]
    [InlineData("version: 1\ncases:\n  - file: a.ts\n    evidgate.id: 018f2f0e-8a52-7c4b-9d3e-5a1b2c3d4e5f\n    extra: 1\n")]
    [InlineData("version: 1\ncases:\n  - file: a.ts\n    evidgate.id: 018f2f0e-8a52-7c4b-9d3e-5a1b2c3d4e5f\n    requirement: [1042]\n")]
    [InlineData("- just\n- a list\n")]
    [InlineData("just a string\n")]
    [InlineData("")]
    public void Sidecar_InvalidDocument_ContributesNoEntryAndOneProblem(string yaml)
    {
        var result = Read(yaml);

        result.Entries.ShouldBeEmpty();
        result.Problems.Count.ShouldBe(1);
        result.Problems[0].Path.ShouldBe("tests/evidgate.cases.yaml");
    }

    [Theory]
    [InlineData("version: 1\ncases:\n  - &a\n    file: a.ts\n    evidgate.id: " + IdA + "\n  - *a\n")]
    [InlineData("version: 1\ncases: !!python/object:os.system []\n")]
    [InlineData("version: 1\nversion: 1\ncases: []\n")]
    [InlineData("version: 1\ncases: []\n---\nversion: 1\ncases: []\n")]
    [InlineData("version: 1\ncases: [unterminated\n")]
    [InlineData("version: 1\n\tcases: []\n")]
    public void Sidecar_YamlThatIsNotPlainData_IsRefused(string yaml)
    {
        var result = Read(yaml);

        result.Entries.ShouldBeEmpty();
        result.Problems.Count.ShouldBe(1);
    }

    [Fact]
    public void Sidecar_BillionLaughs_IsRefusedWithoutExpansion()
    {
        var yaml = "a: &a [x, x, x, x, x, x, x, x, x]\nb: &b [*a, *a, *a, *a, *a, *a, *a, *a, *a]\nc: &c [*b, *b, *b, *b, *b, *b, *b, *b, *b]\nversion: 1\ncases: []\n";
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = Read(yaml);

        result.Entries.ShouldBeEmpty();
        result.Problems.Count.ShouldBe(1);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Sidecar_TooLarge_OrTooDeep_IsRefused()
    {
        Read(new string('x', 1024 * 1024 + 1)).Problems.Count.ShouldBe(1);

        var deep = "version: 1\ncases:\n" + string.Concat(Enumerable.Range(0, 12).Select(i => new string(' ', i * 2) + "- \n")) + "x";
        Read(deep).Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Sidecar_EntryOutsideItsDirectory_IsAProblem_AndTheRestStillRead()
    {
        var result = Read($"version: 1\ncases:\n  - file: ../escape.ts\n    evidgate.id: {IdA}\n  - file: ok.ts\n    evidgate.id: {IdB}\n");

        result.Entries.Select(entry => entry.FullPath).ShouldBe(["tests/ok.ts"]);
        result.Problems.Count.ShouldBe(1);
    }

    [Fact]
    public void Sidecar_ARootManifest_AnchorsAtTheRoot()
    {
        var result = Read($"version: 1\ncases:\n  - file: a.ts\n    evidgate.id: {IdA}\n", "evidgate.cases.yaml");

        result.Entries.Single().FullPath.ShouldBe("a.ts");
    }

    [Fact]
    public void Sidecar_Requirement_PresentIsTrimmed_AbsentIsNull()
    {
        var result = Read($"version: 1\ncases:\n  - file: a.ts\n    evidgate.id: {IdA}\n    requirement: \" REQ-1042 \"\n  - file: b.ts\n    evidgate.id: {IdB}\n");

        result.Problems.ShouldBeEmpty();
        result.Warnings.ShouldBeEmpty();
        result.Entries.Select(entry => entry.RequirementRef).ShouldBe(["REQ-1042", null]);

        // 128 characters after trimming is the limit, and a number is read as its digits.
        Read($"version: 1\ncases:\n  - file: a.ts\n    evidgate.id: {IdA}\n    requirement: \"{new string('r', 128)}\"\n")
            .Warnings.ShouldBeEmpty();

        var numeric = Read($"version: 1\ncases:\n  - file: a.ts\n    evidgate.id: {IdA}\n    requirement: 1042\n  - file: b.ts\n    evidgate.id: {IdB}\n    requirement: null\n");
        numeric.Problems.ShouldBeEmpty();
        numeric.Warnings.ShouldBeEmpty();
        numeric.Entries.Select(entry => entry.RequirementRef).ShouldBe(["1042", null]);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("REQ 1042")]
    [InlineData("REQ?focused=1")]
    [InlineData("\tREQ-1")]
    public void Sidecar_UnusableRequirement_WarnsOnThatEntry_AndTheOtherEntryKeepsItsId(string bad)
    {
        // Two entries: the document stays valid, the good id is untouched, and only the bad entry is reported.
        var yaml = $"version: 1\ncases:\n"
            + $"  - file: a.ts\n    evidgate.id: {IdA}\n"
            + $"  - file: b.ts\n    evidgate.id: {IdB}\n    requirement: {JsonSerializer.Serialize(bad)}\n";

        var result = Read(yaml);

        result.Problems.ShouldBeEmpty();
        result.Warnings.Single().Path.ShouldBe("tests/b.ts");
        result.Entries.Select(entry => (entry.FullPath, entry.Id, entry.RequirementRef)).ShouldBe(
        [
            ("tests/a.ts", Guid.Parse(IdA), (string?)null),
            ("tests/b.ts", Guid.Parse(IdB), (string?)null),
        ]);
    }

    [Fact]
    public void Sidecar_OverlongRequirement_WarnsOnThatEntry_AndBothIdsSurvive()
    {
        var yaml = $"version: 1\ncases:\n"
            + $"  - file: a.ts\n    evidgate.id: {IdA}\n    requirement: REQ-1\n"
            + $"  - file: b.ts\n    evidgate.id: {IdB}\n    requirement: \"{new string('r', 129)}\"\n";

        var result = Read(yaml);

        result.Problems.ShouldBeEmpty();
        result.Warnings.Single().Path.ShouldBe("tests/b.ts");
        result.Entries.Select(entry => (entry.Id, entry.RequirementRef)).ShouldBe(
        [
            (Guid.Parse(IdA), (string?)"REQ-1"),
            (Guid.Parse(IdB), (string?)null),
        ]);
    }

    [Fact]
    public void Sidecar_BooleanRequirement_IsAWarning_AndTheEntryStillReads()
    {
        var result = Read($"version: 1\ncases:\n  - file: a.ts\n    evidgate.id: {IdA}\n    requirement: true\n");

        result.Problems.ShouldBeEmpty();
        result.Warnings.Single().Path.ShouldBe("tests/a.ts");
        result.Entries.Single().Id.ShouldBe(Guid.Parse(IdA));
        result.Entries.Single().RequirementRef.ShouldBeNull();
    }
}
