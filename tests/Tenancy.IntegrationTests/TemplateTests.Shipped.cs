using Evidgate.Modules.Catalog.Domain;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class TemplateTests
{
    [Fact]
    public async Task Template_ShippedBuiltIn_MatchesPackagedFiles()
    {
        // A dedicated database starts from the migrated template and holds no test rows, so this reads the row 0119
        // ships before any other test of the suite can insert its own.
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();

        var bodyRef = await _fixture.ScalarAsync<string>(
            database,
            "select body_ref from public.template where org_id is null and kind = 'web' and name = 'playwright-page' and version = 1");

        bodyRef.ShouldBe(RecomputedBodyRef());

        var rules = await _fixture.ScalarAsync<string>(
            database,
            "select conformance_rules::text from public.template where org_id is null and kind = 'web' and name = 'playwright-page' and version = 1");

        rules.ShouldContain("evidgate-id-present");
    }

    private static string RecomputedBodyRef()
    {
        var root = Path.Combine(RepositoryPaths.RepoRoot, "templates/web/playwright-page/1");
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (
                RelativePath: Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                Bytes: File.ReadAllBytes(path)))
            .ToArray();

        files.ShouldNotBeEmpty();
        return TemplateBodyHasher.Compute(files);
    }
}
