using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 03.1.1-T1.2/T1.3 and the discovery rules (AC1–AC5) without a database: the annotation parser, the repository path
/// rules, the sidecar reader (untrusted YAML) and the tree-to-cases resolution.
/// </summary>
public sealed class CatalogUnitTests
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
        new SidecarManifestReader(Schemas).Read(path, yaml);

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

    // ------------------------------------------------------------------ CaseDiscovery

    private static SourceFile Test(string path, string content = "") => new(path, content, "playwright", "typescript");

    private static SourceFile Sidecar(string directory, params (string File, string Id)[] cases) =>
        new(
            directory.Length == 0 ? "evidgate.cases.yaml" : directory + "/evidgate.cases.yaml",
            "version: 1\ncases:\n" + string.Concat(cases.Select(c => $"  - file: {c.File}\n    evidgate.id: {c.Id}\n")));

    private static DiscoveryResult Discover(params SourceFile[] files) =>
        new CaseDiscovery(new SidecarManifestReader(Schemas)).Discover(files);

    [Fact]
    public void Discovery_AnnotatedFile_BecomesACase_NamedAfterItsStem()
    {
        var result = Discover(Test("tests/login.spec.ts", $"// evidgate.id: {IdA}"));

        var found = result.Cases.Single();
        found.Id.ShouldBe(Guid.Parse(IdA));
        found.Source.ShouldBe("annotation");
        found.Name.ShouldBe("login.spec");
        found.Framework.ShouldBe("playwright");
        result.Problems.ShouldBeEmpty();
    }

    [Fact]
    public void Discovery_SidecarIsAnEquivalentSource()
    {
        var result = Discover(Test("tests/a.spec.ts"), Sidecar("tests", ("a.spec.ts", IdA)));

        var found = result.Cases.Single();
        (found.Path, found.Id, found.Source).ShouldBe(("tests/a.spec.ts", Guid.Parse(IdA), "sidecar"));
        result.Problems.ShouldBeEmpty();
    }

    [Fact]
    public void Discovery_AnnotationAndSidecarAgreeing_IsOneCase()
    {
        var result = Discover(Test("tests/a.spec.ts", $"// evidgate.id: {IdA}"), Sidecar("tests", ("a.spec.ts", IdA)));

        result.Cases.Single().Source.ShouldBe("annotation");
        result.Problems.ShouldBeEmpty();
    }

    [Fact]
    public void Discovery_AnnotationAndSidecarDisagreeing_IsAConflict_AndWritesNoCase()
    {
        var result = Discover(Test("tests/a.spec.ts", $"// evidgate.id: {IdA}"), Sidecar("tests", ("a.spec.ts", IdB)));

        result.Cases.ShouldBeEmpty();
        var problem = result.Problems.Single();
        (problem.Path, problem.Result, problem.Code).ShouldBe(("tests/a.spec.ts", CaseSyncResults.IdSourceConflict, CaseSyncCodes.IdSourceConflict));
        result.HeldIds.ShouldBe([Guid.Parse(IdA), Guid.Parse(IdB)], ignoreOrder: true);
    }

    [Fact]
    public void Discovery_SameIdOnTwoFiles_ReportsEachPath_AndRegistersNeither()
    {
        var result = Discover(
            Test("tests/a.spec.ts", $"// evidgate.id: {IdA}"),
            Test("tests/b.spec.ts", $"// evidgate.id: {IdA}"),
            Test("tests/c.spec.ts", $"// evidgate.id: {IdB}"));

        result.Cases.Select(found => found.Path).ShouldBe(["tests/c.spec.ts"]);
        result.Problems.Select(problem => (problem.Path, problem.Code)).ShouldBe(
        [
            ("tests/a.spec.ts", CaseSyncCodes.DuplicateCaseId),
            ("tests/b.spec.ts", CaseSyncCodes.DuplicateCaseId),
        ]);
        result.HeldIds.ShouldContain(Guid.Parse(IdA));
    }

    [Fact]
    public void Discovery_DuplicateAcrossSources_IsAlsoDuplicate()
    {
        var result = Discover(
            Test("tests/a.spec.ts", $"// evidgate.id: {IdA}"),
            Test("tests/b.spec.ts"),
            Sidecar("tests", ("b.spec.ts", IdA)));

        result.Cases.ShouldBeEmpty();
        result.Problems.Count(problem => problem.Code == CaseSyncCodes.DuplicateCaseId).ShouldBe(2);
    }

    [Fact]
    public void Discovery_MalformedAnnotation_IsAWarning_AndOtherFilesStillProcessed()
    {
        var result = Discover(
            Test("tests/bad.spec.ts", "// evidgate.id: nope"),
            Test("tests/good.spec.ts", $"// evidgate.id: {IdA}"));

        result.Cases.Select(found => found.Path).ShouldBe(["tests/good.spec.ts"]);
        var warning = result.Problems.Single();
        (warning.Path, warning.Result, warning.Code).ShouldBe(("tests/bad.spec.ts", CaseSyncResults.Warning, CaseSyncCodes.InvalidAnnotation));
    }

    [Fact]
    public void Discovery_TwoAnnotationsInOneFile_IsAConflict()
    {
        var result = Discover(Test("tests/a.spec.ts", $"// evidgate.id: {IdA}\n// evidgate.id: {IdB}"));

        result.Cases.ShouldBeEmpty();
        result.Problems.Single().Code.ShouldBe(CaseSyncCodes.IdSourceConflict);
        result.HeldIds.ShouldBe([Guid.Parse(IdA), Guid.Parse(IdB)], ignoreOrder: true);
    }

    [Fact]
    public void Discovery_SidecarEntryNamingAMissingFile_IsAWarning_OnlyForThatEntry()
    {
        var result = Discover(
            Test("tests/a.spec.ts"),
            Sidecar("tests", ("ghost.spec.ts", IdB), ("a.spec.ts", IdA)));

        result.Cases.Single().Path.ShouldBe("tests/a.spec.ts");
        var warning = result.Problems.Single();
        (warning.Path, warning.Code).ShouldBe(("tests/ghost.spec.ts", CaseSyncCodes.InvalidSidecarEntry));
    }

    [Fact]
    public void Discovery_MalformedSidecar_IsAWarningOnTheManifest_NotOnTheTestFiles()
    {
        var result = Discover(
            Test("tests/a.spec.ts", $"// evidgate.id: {IdA}"),
            new SourceFile("tests/evidgate.cases.yaml", "version: 9"));

        result.Cases.Single().Path.ShouldBe("tests/a.spec.ts");
        result.Problems.Single().Path.ShouldBe("tests/evidgate.cases.yaml");
    }

    [Fact]
    public void Discovery_TwoSidecarEntriesForOneFile_ConflictOnlyWhenTheyDiffer()
    {
        var differ = Discover(
            Test("tests/a.spec.ts"),
            new SourceFile("tests/evidgate.cases.yaml", $"version: 1\ncases:\n  - file: a.spec.ts\n    evidgate.id: {IdA}\n  - file: a.spec.ts\n    evidgate.id: {IdB}\n"));
        differ.Cases.ShouldBeEmpty();
        differ.Problems.Single().Code.ShouldBe(CaseSyncCodes.IdSourceConflict);

        var same = Discover(
            Test("tests/a.spec.ts"),
            new SourceFile("tests/evidgate.cases.yaml", $"version: 1\ncases:\n  - file: a.spec.ts\n    evidgate.id: {IdA}\n  - file: a.spec.ts\n    evidgate.id: {IdA}\n"));
        same.Cases.Single().Id.ShouldBe(Guid.Parse(IdA));
        same.Problems.ShouldBeEmpty();
    }

    [Fact]
    public void Discovery_SidecarNameWins_AndLongNamesAreTruncated()
    {
        var named = Discover(
            Test("tests/a.spec.ts"),
            new SourceFile("tests/evidgate.cases.yaml", $"version: 1\ncases:\n  - file: a.spec.ts\n    evidgate.id: {IdA}\n    name: Login works\n"));
        named.Cases.Single().Name.ShouldBe("Login works");

        var stem = Discover(Test("tests/" + new string('a', 600) + ".ts", $"// evidgate.id: {IdA}"));
        stem.Cases.Single().Name.Length.ShouldBe(512);
    }

    [Fact]
    public void Discovery_InvalidOrRepeatedPaths_AreWarnings()
    {
        var result = Discover(
            Test("../escape.spec.ts", $"// evidgate.id: {IdA}"),
            Test("tests/a.spec.ts", $"// evidgate.id: {IdB}"),
            Test("tests/a.spec.ts", $"// evidgate.id: {IdB}"));

        result.Cases.Select(found => found.Path).ShouldBe(["tests/a.spec.ts"]);
        result.Problems.Count.ShouldBe(2);
    }

    [Fact]
    public void Discovery_IdClaimedByAConflictingSource_IsNotRegisteredFromTheCleanFile()
    {
        var result = Discover(
            Test("tests/clean.spec.ts", $"// evidgate.id: {IdA}"),
            Test("tests/messy.spec.ts", $"// evidgate.id: {IdB}"),
            Sidecar("tests", ("messy.spec.ts", IdA)));

        result.Cases.ShouldBeEmpty();
        result.Problems.Select(problem => problem.Path).ShouldContain("tests/clean.spec.ts");
        result.Problems.Select(problem => problem.Path).ShouldContain("tests/messy.spec.ts");
    }

    [Fact]
    public void Discovery_FrameworkFallsBack_WhenBlank()
    {
        var result = Discover(new SourceFile("a.ts", $"// evidgate.id: {IdA}", "  ", null));

        result.Cases.Single().Framework.ShouldBe("unknown");
    }

    [Fact]
    public void Discovery_Output_IsInPathOrder()
    {
        var result = Discover(
            Test("z.spec.ts", $"// evidgate.id: {IdA}"),
            Test("a.spec.ts", $"// evidgate.id: {IdB}"));

        result.Cases.Select(found => found.Path).ShouldBe(["a.spec.ts", "z.spec.ts"]);
    }

    // ------------------------------------------------------------------ CatalogScope (the C2 project rule for case routes)

    private static (Evidgate.BuildingBlocks.Pipeline.Authorization.IRequestPrincipalAccessor Principals, Evidgate.BuildingBlocks.Pipeline.Tenancy.IRequestTenantContextAccessor Tenant) Scope(
        Guid? tokenProject,
        Guid? requestProject)
    {
        var principals = NSubstitute.Substitute.For<Evidgate.BuildingBlocks.Pipeline.Authorization.IRequestPrincipalAccessor>();
        principals.Current.Returns(
            tokenProject is null
                ? Evidgate.BuildingBlocks.Pipeline.Authorization.RequestPrincipal.Member(Guid.CreateVersion7())
                : Evidgate.BuildingBlocks.Pipeline.Authorization.RequestPrincipal.Service(tokenProject, ["case:read"]));
        var tenant = NSubstitute.Substitute.For<Evidgate.BuildingBlocks.Pipeline.Tenancy.IRequestTenantContextAccessor>();
        tenant.Current.Returns(
            requestProject is null ? new Evidgate.BuildingBlocks.Pipeline.Tenancy.RequestTenantContext(Guid.CreateVersion7(), null)
                                   : new Evidgate.BuildingBlocks.Pipeline.Tenancy.RequestTenantContext(Guid.CreateVersion7(), requestProject));

        return (principals, tenant);
    }

    [Fact]
    public void CatalogScope_ACredentialScopedToAnotherProject_IsRefused()
    {
        var mine = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();

        var (principals, tenant) = Scope(tokenProject: mine, requestProject: null);
        Evidgate.Modules.Catalog.Application.CatalogScope.Project(mine, principals, tenant).ShouldBeNull();
        Evidgate.Modules.Catalog.Application.CatalogScope.Project(other, principals, tenant)!.Code.ShouldBe("project_scope_mismatch");

        var (member, headered) = Scope(tokenProject: null, requestProject: mine);
        Evidgate.Modules.Catalog.Application.CatalogScope.Project(mine, member, headered).ShouldBeNull();
        Evidgate.Modules.Catalog.Application.CatalogScope.Project(other, member, headered)!.Status.ShouldBe(403);

        var (open, unscoped) = Scope(tokenProject: null, requestProject: null);
        Evidgate.Modules.Catalog.Application.CatalogScope.Project(other, open, unscoped).ShouldBeNull();
    }
}
