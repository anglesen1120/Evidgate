using System.Globalization;
using System.Net;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class RegistryEndpointTests
{
    /// <summary>AC5 for the org: <c>PATCH /v1/orgs/{o}</c> under If-Match, and a taken slug is 409.</summary>
    [Fact]
    public async Task OrganizationPatchWithIfMatch()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = _hosts.GrantAll(database);
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
        await using var api = _hosts.GrantAll(database);
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

        await using (var setup = _hosts.GrantAll(database))
        using (var setupClient = setup.CreateClient())
        {
            var a = await CreateProjectAsync(setupClient, org, "aa");
            a.Status.ShouldBe(HttpStatusCode.Created, a.Body.ToString());
            projectA = a.Id();
            projectB = (await CreateProjectAsync(setupClient, org, "bb")).Id();
        }

        // The evaluator is bypassed on purpose: since 02.6.2 AC6 a service token can no longer
        // hold project:read/write, so this isolates the handler-side ScopeGuard (AC4).
        await using var api = _hosts.GrantAll(database);
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
        await using var api = _hosts.GrantAll(database);
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
        await using var api = _hosts.GrantAll(database);
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
        await using var api = _hosts.GrantAll(database);
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
        await using var api = _hosts.GrantAll(database);
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
        await using var api = _hosts.GrantAll(database);
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
        await using var api = _hosts.GrantAll(database);
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
}
