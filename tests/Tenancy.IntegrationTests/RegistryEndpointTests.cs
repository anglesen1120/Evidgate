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
public sealed class RegistryEndpointTests
{
    private static readonly string[] ProjectReadWrite = ["project:read", "project:write"];
    private static readonly string[] OrganizationWrite = ["organization:write"];
    private static readonly string[] AcmeDomain = ["acme.example"];
    private static readonly string[] NotADomain = ["not a domain"];

    private readonly PostgresTenancyFixture _fixture;

    public RegistryEndpointTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// AC8 (T98): the creator of an org holds <c>tenant_admin</c> and
    /// <c>release_approver</c> in one active membership, and the real evaluator then
    /// lets that creator create a project — while a stranger is refused.
    /// </summary>
    [Fact]
    public async Task CreateOrganization_GrantsCreatorBothRoles()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.RealEvaluator(_fixture.AppConnectionString(database));
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
        await using var api = TenancyApiFactory.RealEvaluator(_fixture.AppConnectionString(database));
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
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
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
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
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
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
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
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
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
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
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

    /// <summary>AC5 for the org: <c>PATCH /v1/orgs/{o}</c> under If-Match, and a taken slug is 409.</summary>
    [Fact]
    public async Task OrganizationPatchWithIfMatch()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var org = await SeedOrgAsync(database, "acme");
        var taken = await CreateOrgAsync(client, Unique.Slug("taken"));
        taken.Status.ShouldBe(HttpStatusCode.Created);
        var takenSlug = taken.Text("slug") ?? throw new InvalidOperationException("the created org carries its slug");
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());

        var read = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}", caller);
        read.Status.ShouldBe(HttpStatusCode.OK);

        var patched = await client.SendAsync(
            HttpMethod.Patch,
            $"/v1/orgs/{org}",
            caller,
            new { name = "Acme Inc", retention_default_days = 90, allowed_email_domains = AcmeDomain },
            NewKey(),
            ifMatch: read.ETag);
        patched.Status.ShouldBe(HttpStatusCode.OK);
        patched.Body.GetProperty("retention_default_days").GetInt32().ShouldBe(90);
        patched.Body.GetProperty("allowed_email_domains")[0].GetString().ShouldBe("acme.example");

        var conflict = await client.SendAsync(
            HttpMethod.Patch,
            $"/v1/orgs/{org}",
            caller,
            new { slug = takenSlug },
            NewKey(),
            ifMatch: patched.ETag);
        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("slug_taken");

        var badDomain = await client.SendAsync(
            HttpMethod.Patch,
            $"/v1/orgs/{org}",
            caller,
            new { allowed_email_domains = NotADomain },
            NewKey(),
            ifMatch: patched.ETag);
        badDomain.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    /// <summary>
    /// AC3: another org's project id — and another org's id in <c>{o}</c> — answer the
    /// same 404 <c>not_found</c> as a missing one, for reads and writes alike.
    /// </summary>
    [Fact]
    public async Task OtherOrgIdsAre404()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var orgA = await SeedOrgAsync(database, "org-a");
        var orgB = await SeedOrgAsync(database, "org-b");
        var projectB = (await CreateProjectAsync(client, orgB, "web")).Id();
        var asA = TenancyApiFactory.Caller(org: orgA, user: Guid.CreateVersion7());

        var foreign = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{projectB}", asA);
        var missing = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{Guid.CreateVersion7()}", asA);
        foreign.Status.ShouldBe(HttpStatusCode.NotFound);
        foreign.ErrorCode.ShouldBe("not_found");
        foreign.Body.GetProperty("error").GetProperty("message").GetString()
            .ShouldBe(missing.Body.GetProperty("error").GetProperty("message").GetString());

        (await client.SendAsync(HttpMethod.Patch, $"/v1/projects/{projectB}", asA, new { name = "x" }, NewKey(), "\"1\""))
            .Status.ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{orgB}", asA)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{orgB}/projects", asA)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await CreateProjectAsync(client, orgB, "sneaky", asA)).Status.ShouldBe(HttpStatusCode.NotFound);

        (await _fixture.ScalarAsync<string>(database, $"select name from public.project where id = '{projectB}'"))
            .ShouldBe("web project");
    }

    /// <summary>
    /// AC4: a token scoped to project A that addresses project B of the same org is 403
    /// <c>project_scope_mismatch</c> before any read; the same token on A succeeds. The
    /// real evaluator decides the claims.
    /// </summary>
    [Fact]
    public async Task ProjectScopedTokenCannotAddressAnotherProject()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var org = await SeedOrgAsync(database, "acme");
        Guid projectA;
        Guid projectB;

        await using (var setup = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database)))
        using (var setupClient = setup.CreateClient())
        {
            var a = await CreateProjectAsync(setupClient, org, "aa");
            a.Status.ShouldBe(HttpStatusCode.Created, a.Body.ToString());
            projectA = a.Id();
            projectB = (await CreateProjectAsync(setupClient, org, "bb")).Id();
        }

        // The evaluator is bypassed on purpose: since 02.6.2 AC6 a service token can no longer
        // hold project:read/write, so this isolates the handler-side ScopeGuard (AC4).
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var token = TenancyApiFactory.Caller(
            org: org,
            project: projectA,
            serviceScope: projectA,
            servicePermissions: ProjectReadWrite);

        var mismatch = await client.SendAsync(HttpMethod.Patch, $"/v1/projects/{projectB}", token, new { name = "x" }, NewKey(), "\"1\"");
        mismatch.Status.ShouldBe(HttpStatusCode.Forbidden);
        mismatch.ErrorCode.ShouldBe("project_scope_mismatch");

        var read = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{projectB}", token);
        read.Status.ShouldBe(HttpStatusCode.Forbidden);
        read.ErrorCode.ShouldBe("project_scope_mismatch");

        var own = await client.SendAsync(HttpMethod.Patch, $"/v1/projects/{projectA}", token, new { name = "Renamed" }, NewKey(), "\"1\"");
        own.Status.ShouldBe(HttpStatusCode.OK);

        (await _fixture.ScalarAsync<string>(database, $"select name from public.project where id = '{projectB}'"))
            .ShouldBe("bb project");
    }

    /// <summary>AC6 for projects: same key and body replays (one row); changed body is 409.</summary>
    [Fact]
    public async Task CreateProject_IdempotencyKeyReplaysAndConflicts()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var org = await SeedOrgAsync(database, "acme");
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());
        var key = NewKey();

        var first = await client.SendAsync(HttpMethod.Post, $"/v1/orgs/{org}/projects", caller, new { name = "Web", slug = "web" }, key);
        var replay = await client.SendAsync(HttpMethod.Post, $"/v1/orgs/{org}/projects", caller, new { name = "Web", slug = "web" }, key);
        var changed = await client.SendAsync(HttpMethod.Post, $"/v1/orgs/{org}/projects", caller, new { name = "Web", slug = "web-2" }, key);

        first.Status.ShouldBe(HttpStatusCode.Created);
        replay.Status.ShouldBe(HttpStatusCode.Created);
        replay.Id().ShouldBe(first.Id());
        changed.Status.ShouldBe(HttpStatusCode.Conflict);
        changed.ErrorCode.ShouldBe("idempotency_conflict");
        (await _fixture.ScalarAsync<long>(database, $"select count(*) from public.project where org_id = '{org}'")).ShouldBe(1);

        var noKey = await client.SendAsync(HttpMethod.Post, $"/v1/orgs/{org}/projects", caller, new { name = "Web", slug = "web-3" });
        noKey.Status.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// 02.1.1-T3.2: with <c>max_projects = 2</c> the third create is 429
    /// <c>quota_exceeded</c> with <c>Retry-After</c>; a deleted project frees its slot.
    /// </summary>
    [Fact]
    public async Task MaxProjectsReachedIs429()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var org = await SeedOrgAsync(database, "acme", maxProjects: 2);

        (await CreateProjectAsync(client, org, "one")).Status.ShouldBe(HttpStatusCode.Created);
        var two = await CreateProjectAsync(client, org, "two");
        two.Status.ShouldBe(HttpStatusCode.Created);

        var three = await CreateProjectAsync(client, org, "three");
        three.Status.ShouldBe(HttpStatusCode.TooManyRequests);
        three.ErrorCode.ShouldBe("quota_exceeded");
        three.Headers.RetryAfter.ShouldNotBeNull();

        await _fixture.ExecuteAsync(database, $"update public.project set status = 'deleted' where id = '{two.Id()}'");
        (await CreateProjectAsync(client, org, "three")).Status.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>
    /// 02.1.1-T3.2 under concurrency: eight parallel creates against <c>max_projects = 3</c>
    /// leave exactly three projects — the org row lock serialises the check and the insert.
    /// </summary>
    [Fact]
    public async Task ConcurrentCreatesNeverExceedMaxProjects()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var org = await SeedOrgAsync(database, "acme", maxProjects: 3);

        var answers = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => CreateProjectAsync(client, org, string.Create(CultureInfo.InvariantCulture, $"p{i}"))));

        answers.Count(answer => answer.Status == HttpStatusCode.Created).ShouldBe(3);
        answers.Count(answer => answer.Status == HttpStatusCode.TooManyRequests).ShouldBe(5);
        (await _fixture.ScalarAsync<long>(database, $"select count(*) from public.project where org_id = '{org}'")).ShouldBe(3);
    }

    /// <summary>02.1.1 §6: a suspended org still reads, but its registry mutations are 409 <c>org_suspended</c>.</summary>
    [Fact]
    public async Task SuspendedOrgReadsButRefusesMutations()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var org = await SeedOrgAsync(database, "acme");
        var project = (await CreateProjectAsync(client, org, "web")).Id();
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());

        await _fixture.ExecuteAsync(database, $"update public.organization set status = 'suspended' where id = '{org}'");

        (await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}", caller)).Status.ShouldBe(HttpStatusCode.OK);
        (await client.SendAsync(HttpMethod.Get, $"/v1/projects/{project}", caller)).Status.ShouldBe(HttpStatusCode.OK);

        var create = await CreateProjectAsync(client, org, "api");
        create.ErrorCode.ShouldBe("org_suspended");
        var patchProject = await client.SendAsync(HttpMethod.Patch, $"/v1/projects/{project}", caller, new { name = "x" }, NewKey(), "\"1\"");
        patchProject.ErrorCode.ShouldBe("org_suspended");
        var patchOrg = await client.SendAsync(HttpMethod.Patch, $"/v1/orgs/{org}", caller, new { name = "x" }, NewKey(), "\"2\"");
        patchOrg.ErrorCode.ShouldBe("org_suspended");
    }

    /// <summary>
    /// The keyset list walks <c>(created_at, id)</c> with no gap or repeat, and a
    /// malformed cursor or limit is 422.
    /// </summary>
    [Fact]
    public async Task ListProjectsWalksPages()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var org = await SeedOrgAsync(database, "acme");
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());
        var created = new List<Guid>();

        for (var i = 0; i < 5; i++)
        {
            created.Add((await CreateProjectAsync(client, org, string.Create(CultureInfo.InvariantCulture, $"p{i}"))).Id());
        }

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var url = cursor is null
                ? $"/v1/orgs/{org}/projects?limit=2"
                : $"/v1/orgs/{org}/projects?limit=2&cursor={Uri.EscapeDataString(cursor)}";
            var page = await client.SendAsync(HttpMethod.Get, url, caller);
            page.Status.ShouldBe(HttpStatusCode.OK);
            seen.AddRange(page.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
            cursor = page.Body.GetProperty("next_cursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        pages.ShouldBe(3);
        seen.ShouldBe(created);

        (await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/projects?cursor=nope", caller))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/projects?limit=0", caller))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/projects?limit=201", caller))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    /// <summary>
    /// P3 statement budget: one list page and one project read each run exactly one
    /// statement against <c>public.project</c> and at most two statements of their own
    /// besides the tenant-scope setup.
    /// </summary>
    [Fact]
    public async Task ReadsStayInsideTheStatementBudget()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = TenancyApiFactory.GrantAll(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var org = await SeedOrgAsync(database, "acme");
        var caller = TenancyApiFactory.Caller(org: org, user: Guid.CreateVersion7());
        var project = (await CreateProjectAsync(client, org, "web")).Id();
        await CreateProjectAsync(client, org, "api");

        foreach (var url in new[]
        {
            $"/v1/orgs/{org}/projects?limit=1",
            $"/v1/projects/{project}",
        })
        {
            api.Statements.Clear();
            (await client.SendAsync(HttpMethod.Get, url, caller)).Status.ShouldBe(HttpStatusCode.OK);

            var statements = api.Statements.Snapshot();
            var work = statements.Where(statement => !statement.Contains("set_config", StringComparison.Ordinal)).ToList();

            work.Count(statement => statement.Contains("from public.project", StringComparison.Ordinal)).ShouldBe(1, url);
            work.Count.ShouldBeLessThanOrEqualTo(2, string.Join(Environment.NewLine, statements));
        }
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
