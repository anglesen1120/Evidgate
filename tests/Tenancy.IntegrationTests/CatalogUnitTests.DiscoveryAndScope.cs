using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class CatalogUnitTests
{
    // ------------------------------------------------------------------ CaseDiscovery

    private static SourceFile Test(string path, string content = "") => new(path, content, "playwright", "typescript");

    private static SourceFile Sidecar(string directory, params (string File, string Id)[] cases) =>
        new(
            directory.Length == 0 ? "evidgate.cases.yaml" : directory + "/evidgate.cases.yaml",
            "version: 1\ncases:\n" + string.Concat(cases.Select(c => $"  - file: {c.File}\n    evidgate.id: {c.Id}\n")));

    private static DiscoveryResult Discover(params SourceFile[] files) =>
        new CaseDiscovery(new SidecarManifestReader(new Lazy<ContractSchemaSet>(Schemas))).Discover(files);

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
    public void Discovery_SidecarRequirement_ReachesTheCase()
    {
        var sidecar = new SourceFile(
            "tests/evidgate.cases.yaml",
            $"version: 1\ncases:\n  - file: a.spec.ts\n    evidgate.id: {IdA}\n    requirement: REQ-1042\n");

        var found = Discover(Test("tests/a.spec.ts"), sidecar).Cases.Single();

        found.RequirementRef.ShouldBe("REQ-1042");
    }

    [Fact]
    public void Discovery_UnusableSidecarRequirement_WarnsOnTheCase_AndHoldsNoDirectory()
    {
        var sidecar = new SourceFile(
            "tests/evidgate.cases.yaml",
            $"version: 1\ncases:\n  - file: a.spec.ts\n    evidgate.id: {IdA}\n    requirement: \"REQ\\u0001\"\n");

        var result = Discover(Test("tests/a.spec.ts"), sidecar);

        // Unlike a refused entry, a dropped pointer leaves the id usable: the case still resolves, unpointed.
        result.Cases.Single().RequirementRef.ShouldBeNull();
        result.HeldIds.ShouldBeEmpty();
        result.HeldDirectories.ShouldBeEmpty();
        result.Problems.Single().Path.ShouldBe("tests/a.spec.ts");
        result.Problems.Single().Code.ShouldBe(CaseSyncCodes.InvalidSidecarEntry);
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
