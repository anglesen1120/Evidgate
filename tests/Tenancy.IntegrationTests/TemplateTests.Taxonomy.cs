using System.Net;
using System.Text.Json;
using Evidgate.Modules.Catalog.Domain;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class TemplateTests
{
    private const string TaxonomyInsertSql = """
        insert into public.template
          (id, org_id, kind, name, version, body_ref, conformance_rules, platform, level, purpose)
        values (@id, @org::uuid, @kind, @name, @version, @body_ref, @rules::jsonb, @platform, @level, @purpose)
        """;

    [Theory]
    [InlineData("platform", "desktop")]
    [InlineData("level", "system")]
    [InlineData("purpose", "nightly")]
    public async Task Template_TaxonomyOutsideCatalogue_IsRefusedByDatabase(string field, string value)
    {
        await using var world = await StartAsync();
        (await TaxonomyErrorAsync(world, field, field == "platform" ? "web" : field == "level" ? "e2e" : "regression"))
            .ShouldBeNull("the same insert with a catalogue value succeeds");

        var error = await TaxonomyErrorAsync(world, field, value);
        error.ShouldNotBeNull();
        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe($"template_{field}_check");
    }

    [Fact]
    public async Task Template_TaxonomyDefaults_AreReturnedByListAndDetail()
    {
        await using var world = await StartAsync();
        var id = await SeedAsync(world);
        var stored = await _fixture.ScalarAsync<string>(world.Database,
            $"select platform || '/' || level || '/' || purpose from public.template where id = '{id}'");
        stored.ShouldBe("web/e2e/regression");

        var detail = await GetAsync(world, id);
        detail.Status.ShouldBe(HttpStatusCode.OK, detail.ErrorMessage);
        AssertTemplateTaxonomy(detail.Body, "web", "e2e", "regression");
        var list = await ListAsync(world);
        list.Status.ShouldBe(HttpStatusCode.OK, list.ErrorMessage);
        AssertTemplateTaxonomy(list.Body.GetProperty("items")[0], "web", "e2e", "regression");
    }

    [Theory]
    [InlineData("platform", "mobile")]
    [InlineData("level", "integration")]
    [InlineData("purpose", "release_gate")]
    public async Task List_TaxonomyFilter_IsCanonicalAndKeepsContinuationScope(string field, string value)
    {
        await using var world = await StartAsync();
        (await TaxonomyErrorAsync(world, field, value)).ShouldBeNull();
        await SeedAsync(world);
        (await TaxonomyErrorAsync(world, field, value)).ShouldBeNull();
        var expected = await _fixture.ColumnAsync<string>(world.Database,
            $"select id::text from public.template where {field} = '{value}' order by created_at, id");
        expected.Count.ShouldBe(2, "two rows carry the filtered value and one does not");
        var query = $"?{field}={Uri.EscapeDataString(" " + value.ToUpperInvariant() + " ")}&limit=1";

        var page = await ListAsync(world, query);
        page.Status.ShouldBe(HttpStatusCode.OK, page.ErrorMessage);
        Ids(page).ShouldBe([Guid.Parse(expected[0])]);
        page.Body.GetProperty("items")[0].GetProperty(field).GetString().ShouldBe(value);
        var cursor = page.Body.GetProperty("next_cursor").GetString();
        cursor.ShouldNotBeNullOrWhiteSpace();
        var next = await ListAsync(world, query + "&cursor=" + Uri.EscapeDataString(cursor));
        next.Status.ShouldBe(HttpStatusCode.OK, next.ErrorMessage);
        Ids(next).ShouldBe([Guid.Parse(expected[1])], "the continuation keeps every taxonomy filter");
        next.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("platform", "web")]
    [InlineData("level", "e2e")]
    [InlineData("purpose", "regression")]
    public async Task List_UnknownTaxonomy_Is422WithFieldAndInvalidValueReason(string field, string valid)
    {
        await using var world = await StartAsync();
        await SeedAsync(world);
        var accepted = await ListAsync(world, $"?{field}={valid}");
        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        Ids(accepted).Count.ShouldBe(1);
        AssertInvalid(await ListAsync(world, $"?{field}=unknown"), field, "invalid_value");
        AssertInvalid(await ListAsync(world, $"?{field}=%20unknown%20"), field, "invalid_value");
        var padded = await ListAsync(world, $"?{field}=%20{valid.ToUpperInvariant()}%20");
        padded.Status.ShouldBe(HttpStatusCode.OK, padded.ErrorMessage);
        Ids(padded).ShouldBe(Ids(accepted), "a catalogue value is canonicalised despite padding and case");
        var omitted = await ListAsync(world);
        omitted.Status.ShouldBe(HttpStatusCode.OK, omitted.ErrorMessage);
        Ids(omitted).ShouldBe(Ids(accepted), "an absent filter retains every row");
    }

    [Fact]
    public async Task List_CombinedTaxonomyFilters_IntersectWithKind()
    {
        await using var world = await StartAsync();
        var match = Guid.CreateVersion7();
        (await OwnerErrorAsync(world, TaxonomyInsertSql, ("id", match),
            ("platform", "api"), ("level", "integration"), ("purpose", "smoke"))).ShouldBeNull();
        (await OwnerErrorAsync(world, TaxonomyInsertSql,
            ("platform", "api"), ("level", "integration"), ("purpose", "regression"))).ShouldBeNull();
        (await OwnerErrorAsync(world, TaxonomyInsertSql,
            ("platform", "api"), ("level", "e2e"), ("purpose", "smoke"))).ShouldBeNull();
        (await OwnerErrorAsync(world, TaxonomyInsertSql,
            ("platform", "web"), ("level", "integration"), ("purpose", "smoke"))).ShouldBeNull();
        (await OwnerErrorAsync(world, TaxonomyInsertSql, ("kind", "api"),
            ("platform", "api"), ("level", "integration"), ("purpose", "smoke"))).ShouldBeNull();

        var answer = await ListAsync(world, "?kind=web&platform=api&level=integration&purpose=smoke");
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        Ids(answer).ShouldBe([match]);
        var detail = await GetAsync(world, match);
        detail.Status.ShouldBe(HttpStatusCode.OK, detail.ErrorMessage);
        AssertTemplateTaxonomy(detail.Body, "api", "integration", "smoke");
    }

    [Fact]
    public async Task Template_ChangedTaxonomy_RequiresANewBuiltInVersionKey()
    {
        await using var world = await StartAsync();
        const string name = "versioned-taxonomy";
        var original = await SeedAsync(world, builtIn: true, name: name);
        var duplicate = await OwnerErrorAsync(world, TaxonomyInsertSql, ("org", null), ("name", name),
            ("platform", "api"), ("level", "integration"), ("purpose", "release_gate"));
        duplicate.ShouldNotBeNull();
        duplicate.SqlState.ShouldBe("23505");
        duplicate.ConstraintName.ShouldBe("template_builtin_kind_name_version_key");
        var newer = Guid.CreateVersion7();
        (await OwnerErrorAsync(world, TaxonomyInsertSql, ("id", newer), ("org", null), ("name", name), ("version", 2),
            ("platform", "api"), ("level", "integration"), ("purpose", "release_gate"))).ShouldBeNull();

        var oldDetail = await GetAsync(world, original);
        var newDetail = await GetAsync(world, newer);
        oldDetail.Status.ShouldBe(HttpStatusCode.OK, oldDetail.ErrorMessage);
        newDetail.Status.ShouldBe(HttpStatusCode.OK, newDetail.ErrorMessage);
        AssertTemplateTaxonomy(oldDetail.Body, "web", "e2e", "regression");
        AssertTemplateTaxonomy(newDetail.Body, "api", "integration", "release_gate");
        oldDetail.Body.GetProperty("version").GetInt32().ShouldBe(1);
        newDetail.Body.GetProperty("version").GetInt32().ShouldBe(2);
        (await RefusalAsync(world, "update public.template set platform = 'api' where id = @id", ("id", original)))
            .ShouldBe("42501", "the application cannot rewrite the old version's taxonomy");
    }

    [Theory]
    [InlineData("platform")]
    [InlineData("level")]
    [InlineData("purpose")]
    public async Task Template_TaxonomyCatalogue_MatchesContractAndDatabase(string field)
    {
        await using var world = await StartAsync();
        using var schema = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(RepositoryPaths.RepoRoot, "contracts/schemas/taxonomy.v1.json"), TestContext.Current.CancellationToken));
        var expected = schema.RootElement.GetProperty("$defs").GetProperty(field).GetProperty("enum")
            .EnumerateArray().Select(item => item.GetString()!).ToArray();
        expected.Length.ShouldBe(4);
        var catalogue = field switch
        {
            "platform" => Taxonomy.Platforms,
            "level" => Taxonomy.Levels,
            _ => Taxonomy.Purposes,
        };
        catalogue.ShouldBe(expected);
        var definition = await _fixture.ScalarAsync<string>(world.Database,
            $"select pg_get_constraintdef(oid) from pg_constraint where conrelid = 'public.template'::regclass and conname = 'template_{field}_check'");
        definition.ShouldNotBeNullOrWhiteSpace();
        definition.Split('\'').Where((_, index) => index % 2 == 1).ShouldBe(expected);
        foreach (var value in expected)
        {
            (await TaxonomyErrorAsync(world, field, value)).ShouldBeNull($"the database accepts {field}={value}");
            var page = await ListAsync(world, $"?{field}={value}");
            page.Status.ShouldBe(HttpStatusCode.OK, page.ErrorMessage);
            page.Body.GetProperty("items").EnumerateArray().ShouldNotBeEmpty();
            page.Body.GetProperty("items").EnumerateArray().All(item => item.GetProperty(field).GetString() == value).ShouldBeTrue();
        }
    }

    private Task<Npgsql.PostgresException?> TaxonomyErrorAsync(World world, string field, string value, Guid? id = null) =>
        OwnerErrorAsync(world, TaxonomyInsertSql, ("id", id ?? Guid.CreateVersion7()),
            ("platform", field == "platform" ? value : "web"),
            ("level", field == "level" ? value : "e2e"),
            ("purpose", field == "purpose" ? value : "regression"));

    private static void AssertTemplateTaxonomy(JsonElement template, string platform, string level, string purpose)
    {
        template.GetProperty("platform").GetString().ShouldBe(platform);
        template.GetProperty("level").GetString().ShouldBe(level);
        template.GetProperty("purpose").GetString().ShouldBe(purpose);
    }
}
