using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1 AC4 / OI-13-101 through the real host and a real PostgreSQL: <c>project.case_prefix</c> (0039) is exposed
/// through the existing <c>PATCH /v1/projects/{p}</c>, editable by a <c>tenant_admin</c> only (<c>project:write</c> is
/// tenant_admin in <c>contracts/permissions.json</c>), grammar-checked as 422, unique per org as 409, free in another
/// org, and a change never rewrites an existing manual case <c>code</c>.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class ProjectCasePrefixTests
{
    private readonly PostgresTenancyFixture _fixture;

    public ProjectCasePrefixTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>A <c>tenant_admin</c> changes the prefix: 200, the DTO shows it, the version and ETag move, and the list carries it.</summary>
    [Fact]
    public async Task Patch_ChangesTheCasePrefix_AndTheProjectDtoShowsIt()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var org = await SeedOrgAsync(database, "acme");
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());
        var project = (await CreateProjectAsync(client, org, "shop")).Id();

        var read = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}", caller);
        read.Status.ShouldBe(HttpStatusCode.OK);
        read.Text("case_prefix").ShouldNotBeNullOrEmpty();
        read.ETag.ShouldBe("\"1\"");

        var patched = await client.SendAsync(
            HttpMethod.Patch,
            $"/v1/projects/{project}",
            caller,
            new { case_prefix = "SHOPX" },
            NewKey(),
            read.ETag);

        patched.Status.ShouldBe(HttpStatusCode.OK);
        patched.Text("case_prefix").ShouldBe("SHOPX");
        patched.Body.GetProperty("version").GetInt32().ShouldBe(2);
        patched.ETag.ShouldBe("\"2\"");

        (await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}", caller)).Text("case_prefix").ShouldBe("SHOPX");
        (await _fixture.ScalarAsync<string>(database, $"select case_prefix from public.project where id = '{project}'"))
            .ShouldBe("SHOPX");

        // The list projection carries the same field (one DTO, one column set).
        var page = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/projects", caller);
        page.Status.ShouldBe(HttpStatusCode.OK);
        page.Body.GetProperty("items")[0].GetProperty("case_prefix").GetString().ShouldBe("SHOPX");
    }

    /// <summary>A member without <c>project:write</c> is 403 <c>forbidden</c> and the prefix is unchanged.</summary>
    [Fact]
    public async Task Patch_RefusesAMemberWithoutProjectWrite()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var founder = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, founder, "acme");
        var project = await RegistryApi.CreateProjectAsync(client, founder, org, "shop");
        var admin = TenancyApiFactory.Resolved(user: founder, orgHeader: org);

        var viewer = await AddMemberAsync(database, client, org, founder, "viewer");
        var read = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}", admin);
        read.Status.ShouldBe(HttpStatusCode.OK);
        var before = read.Text("case_prefix");

        var refused = await client.SendAsync(
            HttpMethod.Patch,
            $"/v1/projects/{project}",
            TenancyApiFactory.Resolved(user: viewer, orgHeader: org),
            new { case_prefix = "NOPE" },
            NewKey(),
            read.ETag);

        refused.Status.ShouldBe(HttpStatusCode.Forbidden);
        refused.ErrorCode.ShouldBe("forbidden");
        (await _fixture.ScalarAsync<string>(database, $"select case_prefix from public.project where id = '{project}'"))
            .ShouldBe(before);
    }

    /// <summary>The grammar is the migration's <c>^[A-Z][A-Z0-9]{1,7}$</c>: 422 <c>request_invalid</c> on <c>case_prefix</c>.</summary>
    [Fact]
    public async Task Patch_RefusesABadGrammarAs422()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var org = await SeedOrgAsync(database, "acme");
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());
        var project = (await CreateProjectAsync(client, org, "shop")).Id();

        // `SHOP\n` is the reason the rule uses \z and not $: in .NET $ also matches before a trailing newline.
        foreach (var bad in new[] { "shop", "SHOP-1", "S", "TOOLONGPREFIX", "1SHOP", "SHOP\n", string.Empty })
        {
            var refused = await client.SendAsync(
                HttpMethod.Patch, $"/v1/projects/{project}", caller, new { case_prefix = bad }, NewKey(), "\"1\"");

            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"'{bad.Replace("\n", "\\n", StringComparison.Ordinal)}'");
            refused.ErrorCode.ShouldBe("request_invalid");
            refused.Body.GetProperty("error").GetProperty("details").EnumerateArray()
                .ShouldContain(detail => detail.GetProperty("path").GetString() == "/case_prefix"
                    && detail.GetProperty("reason").GetString() == "value_invalid");
        }

        // The longest accepted value still passes (8 characters), and nothing above was written.
        (await client.SendAsync(HttpMethod.Patch, $"/v1/projects/{project}", caller, new { case_prefix = "SHOP1234" }, NewKey(), "\"1\""))
            .Text("case_prefix").ShouldBe("SHOP1234");
    }

    /// <summary>A prefix another project of the same org holds is 409 <c>case_prefix_taken</c> (<c>project_org_case_prefix_uq</c>).</summary>
    [Fact]
    public async Task Patch_RefusesAPrefixAnotherProjectOfTheOrgHolds()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var org = await SeedOrgAsync(database, "acme");
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());
        var first = (await CreateProjectAsync(client, org, "shop")).Id();
        var second = (await CreateProjectAsync(client, org, "checkout")).Id();
        var held = await _fixture.ScalarAsync<string>(database, $"select case_prefix from public.project where id = '{first}'");

        var refused = await client.SendAsync(
            HttpMethod.Patch, $"/v1/projects/{second}", caller, new { case_prefix = held }, NewKey(), "\"1\"");

        refused.Status.ShouldBe(HttpStatusCode.Conflict);
        refused.ErrorCode.ShouldBe("case_prefix_taken");
        (await _fixture.ScalarAsync<string>(database, $"select case_prefix from public.project where id = '{second}'"))
            .ShouldNotBe(held);
    }

    /// <summary>Uniqueness is per org: the same prefix in another org is accepted, and the write really happens.</summary>
    [Fact]
    public async Task Patch_AcceptsAPrefixAnotherOrgHolds()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var orgA = await SeedOrgAsync(database, "acme");
        var orgB = await SeedOrgAsync(database, "rival");
        var asA = TenancyApiFactory.Caller(org: orgA, user: Guid.CreateVersion7());
        var asB = TenancyApiFactory.Caller(org: orgB, user: Guid.CreateVersion7());

        // Different slugs, so the trigger gives the two projects different stems: a shared "shop" slug would make this
        // PATCH a no-op and the assertion below vacuous.
        var projectA = (await CreateProjectAsync(client, orgA, "shop")).Id();
        var projectB = (await CreateProjectAsync(client, orgB, "checkout")).Id();
        var prefixA = await _fixture.ScalarAsync<string>(database, $"select case_prefix from public.project where id = '{projectA}'");
        var before = await _fixture.ScalarAsync<string>(database, $"select case_prefix from public.project where id = '{projectB}'");
        before.ShouldNotBe(prefixA, "the two stems must differ for this test to prove cross-org reuse");

        var patched = await client.SendAsync(
            HttpMethod.Patch, $"/v1/projects/{projectB}", asB, new { case_prefix = prefixA }, NewKey(), "\"1\"");

        patched.Status.ShouldBe(HttpStatusCode.OK);
        patched.Text("case_prefix").ShouldBe(prefixA);
        patched.Body.GetProperty("version").GetInt32().ShouldBe(2);
        (await _fixture.ScalarAsync<string>(database, $"select case_prefix from public.project where id = '{projectB}'"))
            .ShouldBe(prefixA);

        // Org A's own project is untouched by an org B write.
        (await client.SendAsync(HttpMethod.Get, $"/v1/projects/{projectA}", asA)).Text("case_prefix").ShouldBe(prefixA);
    }

    /// <summary>
    /// A prefix change is not a re-key: an existing manual case keeps its <c>code</c> (<c>SHOP-001</c> stays
    /// <c>SHOP-001</c> after the project becomes <c>NEWP</c>), because <c>code</c> is allocated once (AC4).
    /// </summary>
    [Fact]
    public async Task Patch_DoesNotRewriteAnExistingManualCaseCode()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var org = await SeedOrgAsync(database, "acme");
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());
        var project = (await CreateProjectAsync(client, org, "shop")).Id();
        var prefix = await _fixture.ScalarAsync<string>(database, $"select case_prefix from public.project where id = '{project}'");
        var caseId = await SeedManualCaseAsync(database, org, project, prefix + "-001");

        var patched = await client.SendAsync(
            HttpMethod.Patch, $"/v1/projects/{project}", caller, new { case_prefix = "NEWP" }, NewKey(), "\"1\"");

        patched.Status.ShouldBe(HttpStatusCode.OK);
        patched.Text("case_prefix").ShouldBe("NEWP");
        (await _fixture.ScalarAsync<string>(database, $"select code from public.manual_test_case where id = '{caseId}'"))
            .ShouldBe(prefix + "-001");
    }

    /// <summary>An org row as the owner, the way the registry suites seed one.</summary>
    private async Task<Guid> SeedOrgAsync(string database, string slug)
    {
        var id = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            database,
            $"insert into public.organization (id, name, slug) values ('{id}', '{slug} org', '{slug}')");

        return id;
    }

    /// <summary>A project created through the real route by a caller of <paramref name="org"/> (permissions granted).</summary>
    private static Task<ApiAnswer> CreateProjectAsync(HttpClient client, Guid org, string slug) =>
        client.SendAsync(
            HttpMethod.Post,
            $"/v1/orgs/{org}/projects",
            TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7()),
            new { name = slug + " project", slug },
            NewKey());

    /// <summary>An active member holding a built-in role, seeded as the owner then granted through the API.</summary>
    private async Task<Guid> AddMemberAsync(
        string database,
        HttpClient client,
        Guid org,
        Guid admin,
        string roleCode)
    {
        var userId = Guid.CreateVersion7();
        var membershipId = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public."user" (id, email) values ('{userId}', 'member-{userId:N}@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{membershipId}', '{org}', '{userId}', 'member-{userId:N}@example.test', 'active')
            """);

        var role = await _fixture.ScalarAsync<Guid>(database, $"select id from public.role where code = '{roleCode}' and org_id is null");
        var granted = await client.SendAsync(
            HttpMethod.Post,
            $"/v1/orgs/{org}/memberships/{membershipId}/roles",
            TenancyApiFactory.Resolved(user: admin, orgHeader: org),
            new { role_id = role },
            NewKey());
        granted.Status.ShouldBe(HttpStatusCode.OK, granted.ErrorMessage);

        return userId;
    }

    /// <summary>One manual case under a seeded node and item (the row chain the composite FKs need), as the owner.</summary>
    private async Task<Guid> SeedManualCaseAsync(string database, Guid org, Guid project, string code)
    {
        var node = Guid.CreateVersion7();
        var item = Guid.CreateVersion7();
        var caseId = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.feature_node (id, org_id, project_id, kind, title, position)
            values ('{node}', '{org}', '{project}', 'module', 'Shop', 0);
            insert into public.checklist_item (id, org_id, project_id, feature_node_id, title, priority, state, source, position)
            values ('{item}', '{org}', '{project}', '{node}', 'Checkout', 'high', 'active', 'manual', 0);
            insert into public.manual_test_case (id, org_id, project_id, checklist_item_id, code, title, steps, priority, state, source)
            values ('{caseId}', '{org}', '{project}', '{item}', '{code}', 'Pay with a card',
                    jsonb_build_object('steps', '[]'::jsonb), 'high', 'draft', 'manual');
            """);

        return caseId;
    }
}
