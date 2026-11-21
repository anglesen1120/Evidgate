using System.Net;
using Evidgate.Modules.Catalog.Domain;
using Evidgate.Testing;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class TemplateTests
{
    /// <summary>The id of the built-in row 0119 ships.</summary>
    private static readonly Guid ShippedTemplateId = Guid.Parse("018f8f5e-0000-7000-8000-000000000119");

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

    [Fact]
    public async Task Template_ShippedBuiltIn_CarriesTheDefaultTaxonomyOnTheDatabaseAndTheRoute()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var taxonomy = await _fixture.ScalarAsync<string>(
            database,
            $"select platform || '/' || level || '/' || purpose from public.template where id = '{ShippedTemplateId}'");
        taxonomy.ShouldBe("web/e2e/regression", "0119's row takes the column defaults and the explicit backfill");

        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database.Name));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("template-shipped"));
        var answer = await client.SendAsync(HttpMethod.Get, "/v1/templates?limit=50",
            TenancyApiFactory.Resolved(user: user, orgHeader: org));
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        var shipped = answer.Body.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == ShippedTemplateId);
        shipped.GetProperty("platform").GetString().ShouldBe("web");
        shipped.GetProperty("level").GetString().ShouldBe("e2e");
        shipped.GetProperty("purpose").GetString().ShouldBe("regression");
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
