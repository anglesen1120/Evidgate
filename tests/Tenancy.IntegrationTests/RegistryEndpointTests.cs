using System.Globalization;
using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 02.1.1-T2/T3 through the real Api composition: the registry routes of feature plan
/// §5 against a migrated PostgreSQL 18 database, one database per test. Each test
/// names the acceptance criterion it proves (§9 test plan).
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class RegistryEndpointTests : IClassFixture<TenancyHostPool>
{
    private static readonly string[] ProjectReadWrite = ["project:read", "project:write"];
    private static readonly string[] OrganizationWrite = ["organization:write"];
    private static readonly string[] AcmeDomain = ["acme.example"];
    private static readonly string[] NotADomain = ["not a domain"];

    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;
    public RegistryEndpointTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

    /// <summary>
    /// AC8 (T98): the creator of an org holds <c>tenant_admin</c> and
    /// <c>release_approver</c> in one active membership, and the real evaluator then
    /// lets that creator create a project — while a stranger is refused.
    /// </summary>
    [Fact]
    public async Task CreateOrganization_GrantsCreatorBothRoles()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = _hosts.RealEvaluator(database);
        using var client = api.CreateClient();
        var creator = Guid.CreateVersion7();
        var slug = Unique.Slug("acme");

        var created = await client.SendAsync(
            HttpMethod.Post,
            "/v1/orgs",
            TenancyApiFactory.Caller(user: creator, email: "Founder@Example.test"),
            new { name = "Acme", slug },
            NewKey());

        created.Status.ShouldBe(HttpStatusCode.Created);
        created.Text("slug").ShouldBe(slug);
        created.ETag.ShouldBe("\"1\"");
        var org = created.Id();

        var roles = await _fixture.ColumnAsync<string>(
            database,
            $"""
            select r.code
              from public.membership m
              join public.membership_role mr on mr.membership_id = m.id and mr.org_id = m.org_id
              join public.role r on r.id = mr.role_id
             where m.org_id = '{org}' and m.user_id = '{creator}' and m.status = 'active'
               and m.invited_email = 'founder@example.test'
             order by r.code
            """);
        roles.ShouldBe(["release_approver", "tenant_admin"]);

        var project = await client.SendAsync(
            HttpMethod.Post,
            $"/v1/orgs/{org}/projects",
            TenancyApiFactory.Caller(org: org, user: creator),
            new { name = "Checkout", slug = "checkout" },
            NewKey());
        project.Status.ShouldBe(HttpStatusCode.Created);
        project.Id("org_id").ShouldBe(org);

        var stranger = await client.SendAsync(
            HttpMethod.Post,
            $"/v1/orgs/{org}/projects",
            TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7()),
            new { name = "Other", slug = "other" },
            NewKey());
        stranger.Status.ShouldBe(HttpStatusCode.Forbidden);
        stranger.ErrorCode.ShouldBe("no_membership");
    }

    /// <summary>
    /// <c>POST /v1/orgs</c> is open to a signed-in user only: no caller is 401, a service
    /// token is 403, and a user without a verified email cannot found an org (AC8 binds
    /// the creator's membership to it). Nothing is written in any of the three cases.
    /// </summary>
    [Fact]
    public async Task CreateOrganization_RefusesCallersThatAreNotSignedInUsers()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = _hosts.RealEvaluator(database);
        using var client = api.CreateClient();
        var body = new { name = "Acme", slug = Unique.Slug("acme") };

        var anonymous = await client.SendAsync(HttpMethod.Post, "/v1/orgs", TenancyApiFactory.Caller(), body, NewKey());
        anonymous.Status.ShouldBe(HttpStatusCode.Unauthorized);
        anonymous.ErrorCode.ShouldBe("unauthenticated");

        var service = await client.SendAsync(
            HttpMethod.Post,
            "/v1/orgs",
            TenancyApiFactory.Caller(servicePermissions: OrganizationWrite),
            body,
            NewKey());
        service.Status.ShouldBe(HttpStatusCode.Forbidden);

        var noEmail = await client.SendAsync(
            HttpMethod.Post,
            "/v1/orgs",
            TenancyApiFactory.Caller(user: Guid.CreateVersion7()),
            body,
            NewKey());
        noEmail.Status.ShouldBe(HttpStatusCode.Forbidden);
        noEmail.ErrorCode.ShouldBe("forbidden");

        (await _fixture.ScalarAsync<long>(database, $"select count(*) from public.organization where slug = '{body.slug}'")).ShouldBe(0);
    }

    /// <summary>AC2 for orgs: slugs are global, so a second org with a taken slug is 409 <c>slug_taken</c>.</summary>
    [Fact]
    public async Task CreateOrganization_TakenSlugIs409()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = _hosts.GrantAll(database);
        using var client = api.CreateClient();

        var slug = Unique.Slug("acme");

        (await CreateOrgAsync(client, slug)).Status.ShouldBe(HttpStatusCode.Created);

        var second = await CreateOrgAsync(client, slug);
        second.Status.ShouldBe(HttpStatusCode.Conflict);
        second.ErrorCode.ShouldBe("slug_taken");
    }

    /// <summary>
    /// AC6 for <c>POST /v1/orgs</c>: the same caller, key and body replays the original
    /// response (same id, one row); a changed body under that key is 409
    /// <c>idempotency_conflict</c>; another caller with the same key is a different org.
    /// </summary>
    [Fact]
    public async Task CreateOrganization_IdempotencyKeyReplaysAndConflicts()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = _hosts.GrantAll(database);
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var key = NewKey();
        var caller = TenancyApiFactory.Caller(user: user, email: "a@example.test");
        var slug = Unique.Slug("acme");

        var first = await client.SendAsync(HttpMethod.Post, "/v1/orgs", caller, new { name = "Acme", slug }, key);
        var replay = await client.SendAsync(HttpMethod.Post, "/v1/orgs", caller, new { name = "Acme", slug }, key);
        var changed = await client.SendAsync(HttpMethod.Post, "/v1/orgs", caller, new { name = "Acme 2", slug }, key);

        first.Status.ShouldBe(HttpStatusCode.Created);
        replay.Status.ShouldBe(HttpStatusCode.Created);
        replay.Id().ShouldBe(first.Id());
        changed.Status.ShouldBe(HttpStatusCode.Conflict);
        changed.ErrorCode.ShouldBe("idempotency_conflict");

        (await _fixture.ScalarAsync<long>(database, $"select count(*) from public.organization where slug = '{slug}'")).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(database, $"select count(*) from public.membership where org_id = '{first.Id()}'")).ShouldBe(1);

        var otherUser = await client.SendAsync(
            HttpMethod.Post,
            "/v1/orgs",
            TenancyApiFactory.Caller(user: Guid.CreateVersion7(), email: "b@example.test"),
            new { name = "Beta", slug = Unique.Slug("beta") },
            key);
        otherUser.Status.ShouldBe(HttpStatusCode.Created);
        otherUser.Id().ShouldNotBe(first.Id());
    }

    /// <summary>The D-A1 grammar and the name limits are 422s with a field path, never a stored row.</summary>
    [Fact]
    public async Task Create_InvalidSlugOrNameIs422()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = _hosts.GrantAll(database);
        using var client = api.CreateClient();
        var org = await SeedOrgAsync(database, "acme");

        foreach (var slug in new[] { "ab-", "-ab", "a", "AB", "a_b", new string('a', 64) })
        {
            var answer = await CreateProjectAsync(client, org, slug);
            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"slug '{slug}'");
            answer.ErrorCode.ShouldBe("request_invalid");
        }

        var blank = await client.SendAsync(
            HttpMethod.Post,
            $"/v1/orgs/{org}/projects",
            TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7()),
            new { name = "   ", slug = "web" },
            NewKey());
        blank.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // Control and format characters are a 422, not a PostgreSQL 22021 surfacing as
        // 500 (NUL) or a spoofed display name (bidi override) — 02.1.1 security review.
        foreach (var name in new[] { "web\u0000shop", "web\u202Eshop" })
        {
            var controlled = await client.SendAsync(
                HttpMethod.Post,
                $"/v1/orgs/{org}/projects",
                TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7()),
                new { name, slug = "web" },
                NewKey());
            controlled.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        (await CreateOrgAsync(client, "trailing-")).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await _fixture.ScalarAsync<long>(database, $"select count(*) from public.project where org_id = '{org}'")).ShouldBe(0);
    }

    /// <summary>AC2: a slug is unique per org — 409 <c>slug_taken</c> in the same org, fine in another.</summary>
    [Fact]
    public async Task ProjectSlugIsUniquePerOrg()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = _hosts.GrantAll(database);
        using var client = api.CreateClient();
        var orgA = await SeedOrgAsync(database, "org-a");
        var orgB = await SeedOrgAsync(database, "org-b");

        (await CreateProjectAsync(client, orgA, "web")).Status.ShouldBe(HttpStatusCode.Created);

        var again = await CreateProjectAsync(client, orgA, "web");
        again.Status.ShouldBe(HttpStatusCode.Conflict);
        again.ErrorCode.ShouldBe("slug_taken");

        (await CreateProjectAsync(client, orgB, "web")).Status.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>
    /// AC5: create → read (ETag "1") → patch under the right <c>If-Match</c> bumps the
    /// version; a stale <c>If-Match</c> is 412 with the current version in
    /// <c>error.details</c>; a missing one is 422.
    /// </summary>
    [Fact]
    public async Task ProjectCreateReadPatchWithIfMatch()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = _hosts.GrantAll(database);
        using var client = api.CreateClient();
        var org = await SeedOrgAsync(database, "acme");
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());

        var project = (await CreateProjectAsync(client, org, "web")).Id();

        var read = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}", caller);
        read.Status.ShouldBe(HttpStatusCode.OK);
        read.ETag.ShouldBe("\"1\"");

        var patched = await client.SendAsync(
            HttpMethod.Patch,
            $"/v1/projects/{project}",
            caller,
            new { name = "Web shop", retention_days = 30 },
            NewKey(),
            ifMatch: read.ETag);
        patched.Status.ShouldBe(HttpStatusCode.OK);
        patched.Text("name").ShouldBe("Web shop");
        patched.Body.GetProperty("retention_days").GetInt32().ShouldBe(30);
        patched.Body.GetProperty("version").GetInt32().ShouldBe(2);
        patched.ETag.ShouldBe("\"2\"");

        var stale = await client.SendAsync(
            HttpMethod.Patch,
            $"/v1/projects/{project}",
            caller,
            new { name = "Lost update" },
            NewKey(),
            ifMatch: "\"1\"");
        stale.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        stale.ErrorCode.ShouldBe("precondition_failed");
        stale.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .ShouldContain(detail => detail.GetProperty("path").GetString() == "version"
                && detail.GetProperty("reason").GetString() == "2");

        var missing = await client.SendAsync(
            HttpMethod.Patch,
            $"/v1/projects/{project}",
            caller,
            new { name = "No precondition" },
            NewKey());
        missing.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);

        (await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}", caller)).Text("name").ShouldBe("Web shop");
    }

    private static Task<ApiAnswer> CreateOrgAsync(HttpClient client, string slug) =>
        client.SendAsync(
            HttpMethod.Post,
            "/v1/orgs",
            TenancyApiFactory.Caller(user: Guid.CreateVersion7(), email: "creator@example.test"),
            new { name = slug + " org", slug },
            NewKey());

    private static Task<ApiAnswer> CreateProjectAsync(
        HttpClient client,
        Guid org,
        string slug,
        Action<System.Net.Http.Headers.HttpRequestHeaders>? caller = null) =>
        client.SendAsync(
            HttpMethod.Post,
            $"/v1/orgs/{org}/projects",
            caller ?? TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7()),
            new { name = slug + " project", slug },
            NewKey());

    private async Task<Guid> SeedOrgAsync(string database, string slug, int? maxProjects = null)
    {
        var id = Guid.CreateVersion7();
        var unique = Unique.Slug(slug);
        var limit = maxProjects is { } value ? value.ToString(CultureInfo.InvariantCulture) : "null";
        await _fixture.ExecuteAsync(
            database,
            $"insert into public.organization (id, name, slug, max_projects) values ('{id}', '{unique} org', '{unique}', {limit})");
        return id;
    }
}
