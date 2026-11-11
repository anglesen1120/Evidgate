// Request bodies are written inline so each test reads as the request it sends; the arrays are tiny and built once per test.
#pragma warning disable CA1861
using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 03.2.1 AC1–AC8 through the real host and a real PostgreSQL: suites, immutable revisions with variants and a dependency
/// DAG, secret-free validation, concurrent numbering, publishing, diff, ETag and the preview.
/// </summary>
[Trait("Category", "Docker")]
public sealed class CatalogSuiteTests : IClassFixture<TenancyHostPool>
{
    private const string Commit1 = "aaaaaaa1111111";

    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;
    public CatalogSuiteTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project, Guid Suite, Guid[] Revisions)
        : IAsyncDisposable
    {
        public Action<System.Net.Http.Headers.HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>An org with a project, <paramref name="cases"/> registered cases (one revision each) and an empty suite.</summary>
    private async Task<World> StartAsync(int cases = 4)
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = _hosts.RealResolver(database);
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, "checkout");
        var revisions = await SeedCasesAsync(api, org, project, database, cases, 0);

        var admin = TenancyApiFactory.Resolved(user: user, orgHeader: org);
        var suite = await client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/suites", admin, new { name = "Smoke", slug = "smoke" }, NewKey());
        suite.Status.ShouldBe(HttpStatusCode.Created, suite.ErrorMessage);

        return new World(api, client, database, user, org, project, suite.Id(), revisions);
    }

    private async Task<Guid[]> SeedCasesAsync(TenancyApiFactory api, Guid org, Guid project, string database, int count, int offset)
    {
        var files = Enumerable.Range(offset, count).Select(i => new SourceFile(
            $"t/{i}.spec.ts",
            $"// evidgate.id: 018f2f0e-8a52-7c4b-9d3e-{i:D12}\ntest('{i}', () => {{ expect(a).toBe({i}); }});",
            "playwright",
            "typescript")).ToArray();

        await using var services = api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(org, null, false, TestContext.Current.CancellationToken);
        var outcome = await services.ServiceProvider.GetRequiredService<ICaseIdentityService>()
            .RegisterDiscoveredAsync(project, Commit1, files, TestContext.Current.CancellationToken);
        outcome.Succeeded.ShouldBeTrue(outcome.Failure);
        await scope.CommitAsync(TestContext.Current.CancellationToken);

        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            ids.Add(await _fixture.ScalarAsync<Guid>(
                database,
                $"select r.id from public.test_revision r join public.test_case c on c.org_id = r.org_id and c.id = r.case_id where c.project_id = '{project}' and c.file_path = 't/{offset + i}.spec.ts'"));
        }

        return [.. ids];
    }

    private static Task<ApiAnswer> CreateRevisionAsync(World world, object body, string query = "") =>
        world.Client.SendAsync(HttpMethod.Post, $"/v1/suites/{world.Suite}/revisions{query}", world.Admin, body, NewKey());

    private static object Member(Guid revision, object? parameters = null, string? reference = null, bool? required = null) =>
        new { @ref = reference, case_revision_id = revision, @params = parameters, required };

    // ------------------------------------------------------------------ suites

    [Fact]
    public async Task Suite_CanBeCreatedListedAndRead_WithAnETagAnd304()
    {
        await using var world = await StartAsync();

        var read = await world.Client.SendAsync(HttpMethod.Get, $"/v1/suites/{world.Suite}", world.Admin);
        read.Status.ShouldBe(HttpStatusCode.OK);
        read.Text("slug").ShouldBe("smoke");
        read.ETag.ShouldBe("\"0-1\""); // head number - row version

        var list = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/suites", world.Admin);
        list.Body.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(world.Suite);

        (await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0]) } })).Status.ShouldBe(HttpStatusCode.Created);

        var again = await world.Client.SendAsync(HttpMethod.Get, $"/v1/suites/{world.Suite}", world.Admin);
        again.ETag.ShouldStartWith("\"1-");
        var headTag = again.ETag!;
        headTag.ShouldNotBe(read.ETag);
        again.Body.GetProperty("head_revision_no").GetInt32().ShouldBe(1);

        // If-None-Match with the current head: nothing to send.
        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"/v1/suites/{world.Suite}");
        world.Admin(conditional.Headers);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", "W/" + headTag);
        using var response = await world.Client.SendAsync(conditional, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();

        conditional.Dispose();
        using var stale = new HttpRequestMessage(HttpMethod.Get, $"/v1/suites/{world.Suite}");
        world.Admin(stale.Headers);
        stale.Headers.TryAddWithoutValidation("If-None-Match", "\"0\"");
        (await world.Client.SendAsync(stale, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Suite_Slug_IsUniquePerProject_AndValidated()
    {
        await using var world = await StartAsync();

        var taken = await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/suites", world.Admin, new { name = "Again", slug = "smoke" }, NewKey());
        taken.Status.ShouldBe(HttpStatusCode.Conflict);
        taken.ErrorCode.ShouldBe("suite_slug_taken");

        (await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/suites", world.Admin, new { name = "X", slug = "Bad Slug" }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/suites", world.Admin, new { name = "  ", slug = "ok-slug" }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var second = await CreateProjectAsync(world.Client, world.User, world.Org, "billing");
        (await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{second}/suites", world.Admin, new { name = "Smoke", slug = "smoke" }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Suite_Routes_NeedThePermission_AndHideOtherTenants()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "theirs");
        var rival = TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg);
        var stranger = TenancyApiFactory.Resolved(user: Guid.CreateVersion7(), orgHeader: world.Org);

        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/suites/{world.Suite}", stranger)).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await world.Client.SendAsync(HttpMethod.Post, $"/v1/suites/{world.Suite}/revisions", stranger, new { }, NewKey())).Status.ShouldBe(HttpStatusCode.Forbidden);

        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/suites/{world.Suite}", rival)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(HttpMethod.Post, $"/v1/suites/{world.Suite}/revisions", rival, new { }, NewKey())).Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/suites", rival)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{otherProject}/suites", world.Admin)).Status.ShouldBe(HttpStatusCode.NotFound);

        var service = TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["suite:read"]);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/suites/{world.Suite}", service)).Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Suite_List_PagesWithACursor()
    {
        await using var world = await StartAsync();
        foreach (var slug in new[] { "second", "third" })
        {
            (await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/suites", world.Admin, new { name = slug, slug }, NewKey())).Status.ShouldBe(HttpStatusCode.Created);
        }

        var first = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/suites?limit=2", world.Admin);
        first.Body.GetProperty("items").GetArrayLength().ShouldBe(2);
        var cursor = first.Body.GetProperty("next_cursor").GetString()!;
        var rest = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/suites?limit=2&cursor={Uri.EscapeDataString(cursor)}", world.Admin);
        rest.Body.GetProperty("items").EnumerateArray().Single().GetProperty("slug").GetString().ShouldBe("third");
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/suites?limit=0", world.Admin)).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/suites?cursor=junk", world.Admin)).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    // ------------------------------------------------------------------ AC1 revisions

    [Fact]
    public async Task Revision_IsWrittenWhole_AndReadBackWithItsPinnedContent()
    {
        await using var world = await StartAsync();
        var created = await CreateRevisionAsync(world, new
        {
            members = new[]
            {
                Member(world.Revisions[0], new { browser = "Chromium" }, "login"),
                Member(world.Revisions[1], new { browser = "firefox" }, "cart", required: false),
                Member(world.Revisions[2]),
            },
            edges = new[] { new { parent = "login", child = "cart" } },
            shard_strategy = new { kind = "count", count = 2, balance_by = "duration" },
            retry_policy = new { max_retries = 1, only_on_status = new[] { "failed" } },
            environment_requirements = new { base_urls = new { web = "https://staging.example.com" }, data_isolation = "shared", secrets = new[] { "API_TOKEN" } },
        });

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        (created.Body.GetProperty("revision_no").GetInt32(), created.Text("status"), created.Body.GetProperty("created").GetBoolean()).ShouldBe((1, "draft", true));
        created.Text("revision_hash").ShouldStartWith("sha256:");

        var revision = await world.Client.SendAsync(HttpMethod.Get, $"/v1/suite-revisions/{created.Id("suite_revision_id")}", world.Admin);
        revision.Status.ShouldBe(HttpStatusCode.OK);
        var members = revision.Body.GetProperty("members").EnumerateArray().ToList();
        members.Count.ShouldBe(3);
        members.Select(member => member.GetProperty("ordinal").GetInt32()).ShouldBe([0, 1, 2]);
        members.Select(member => member.GetProperty("case_revision_id").GetGuid()).ShouldBe(world.Revisions[..3].Order().ToArray());
        members.Single(member => member.GetProperty("case_revision_id").GetGuid() == world.Revisions[0]).GetProperty("params").GetProperty("browser").GetString().ShouldBe("chromium");
        members.Single(member => member.GetProperty("case_revision_id").GetGuid() == world.Revisions[1]).GetProperty("required").GetBoolean().ShouldBeFalse();
        revision.Body.GetProperty("edges").GetArrayLength().ShouldBe(1);
        revision.Body.GetProperty("shard_strategy").GetProperty("count").GetInt32().ShouldBe(2);
        revision.Body.GetProperty("environment_requirements").GetProperty("data_isolation").GetString().ShouldBe("shared");

        // The suite's head moved with the revision.
        (await _fixture.ScalarAsync<Guid>(world.Database, $"select head_revision_id from public.suite where id = '{world.Suite}'")).ShouldBe(created.Id("suite_revision_id"));
    }

    [Fact]
    public async Task Revision_WithDefaults_AndWithNoMembers_IsLegal()
    {
        await using var world = await StartAsync();

        var empty = await CreateRevisionAsync(world, new { });

        empty.Status.ShouldBe(HttpStatusCode.Created, empty.ErrorMessage);
        empty.Body.GetProperty("warnings").EnumerateArray().Single().GetString().ShouldBe("the revision has no members");
        var read = await world.Client.SendAsync(HttpMethod.Get, $"/v1/suite-revisions/{empty.Id("suite_revision_id")}", world.Admin);
        read.Body.GetProperty("members").GetArrayLength().ShouldBe(0);
        read.Body.GetProperty("shard_strategy").GetProperty("kind").GetString().ShouldBe("none");
        read.Body.GetProperty("retry_policy").GetProperty("max_retries").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Revision_WithIdenticalContent_ReturnsTheExistingOne_AndOrderDoesNotMatter()
    {
        await using var world = await StartAsync();
        var first = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], new { browser = "chromium", retries = 2 }, "a"), Member(world.Revisions[1], null, "b") }, edges = new[] { new { parent = "a", child = "b" } } });
        var second = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[1], null, "x"), Member(world.Revisions[0], new { retries = 2, Browser_note = (string?)null, browser = "CHROMIUM" }, "y") }, edges = new[] { new { parent = "y", child = "x" } } });

        first.Status.ShouldBe(HttpStatusCode.Created);
        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        second.Id("suite_revision_id").ShouldBe(first.Id("suite_revision_id"));
        second.Body.GetProperty("created").GetBoolean().ShouldBeFalse();
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.suite_revision where org_id = '{world.Org}'")).ShouldBe(1);

        // Another suite with the very same content has its own revision.
        var other = await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/suites", world.Admin, new { name = "Other", slug = "other" }, NewKey());
        var elsewhere = await world.Client.SendAsync(
            HttpMethod.Post,
            $"/v1/suites/{other.Id()}/revisions",
            world.Admin,
            new { members = new[] { Member(world.Revisions[0], new { browser = "chromium", retries = 2 }, "a"), Member(world.Revisions[1], null, "b") }, edges = new[] { new { parent = "a", child = "b" } } },
            NewKey());
        elsewhere.Status.ShouldBe(HttpStatusCode.Created);
        elsewhere.Text("revision_hash").ShouldBe(first.Text("revision_hash"));
        elsewhere.Id("suite_revision_id").ShouldNotBe(first.Id("suite_revision_id"));
    }

    [Fact]
    public async Task Revision_IsIdempotent_ByKey()
    {
        await using var world = await StartAsync();
        var key = NewKey();
        var body = new { members = new[] { Member(world.Revisions[0]) } };

        var first = await world.Client.SendAsync(HttpMethod.Post, $"/v1/suites/{world.Suite}/revisions", world.Admin, body, key);
        var replay = await world.Client.SendAsync(HttpMethod.Post, $"/v1/suites/{world.Suite}/revisions", world.Admin, body, key);

        first.Status.ShouldBe(HttpStatusCode.Created);
        replay.Id("suite_revision_id").ShouldBe(first.Id("suite_revision_id"));
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.suite_revision where org_id = '{world.Org}'")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ AC2 membership

    [Fact]
    public async Task Revision_WithACaseRevisionFromAnotherProject_Or_Unknown_Is422_CrossProjectMember()
    {
        await using var world = await StartAsync();
        var second = await CreateProjectAsync(world.Client, world.User, world.Org, "billing");
        var foreign = (await SeedCasesAsync(world.Api, world.Org, second, world.Database, 1, 100))[0];

        var cross = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], null, "ok"), Member(foreign, null, "stray") } });
        cross.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        cross.ErrorCode.ShouldBe("cross_project_member");
        cross.Body.GetProperty("error").GetProperty("details").EnumerateArray().Single().GetProperty("path").GetString().ShouldBe("/members/stray");

        (await CreateRevisionAsync(world, new { members = new[] { Member(Guid.CreateVersion7()) } })).ErrorCode.ShouldBe("cross_project_member");
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.suite_revision where org_id = '{world.Org}'")).ShouldBe(0);
    }

    [Fact]
    public async Task Database_RejectsAMemberOfAnotherOrg_EvenIfTheApiCheckWereBypassed()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));
        var otherProject = await CreateProjectAsync(world.Client, otherUser, otherOrg, "theirs");
        var foreign = (await SeedCasesAsync(world.Api, otherOrg, otherProject, world.Database, 1, 200))[0];
        var variant = Guid.CreateVersion7();
        var revision = Guid.CreateVersion7();

        // One statement batch = one transaction, so the revision is still draft-and-new when its member arrives and the
        // insert guard lets it through; only the composite foreign key can then refuse the other org's case revision.
        var error = await Should.ThrowAsync<PostgresException>(async () => await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.variant (id, org_id, project_id, variant_hash, params) values ('{variant}', '{world.Org}', '{world.Project}', 'sha256:{new string('c', 64)}', jsonb_build_object());
            insert into public.suite_revision (id, org_id, suite_id, revision_no, revision_hash, shard_strategy, retry_policy) values ('{revision}', '{world.Org}', '{world.Suite}', 99, 'sha256:{new string('d', 64)}', jsonb_build_object(), jsonb_build_object());
            insert into public.suite_member (id, org_id, suite_revision_id, case_revision_id, variant_id, ordinal) values ('{Guid.CreateVersion7()}', '{world.Org}', '{revision}', '{foreign}', '{variant}', 0);
            """));

        error.ConstraintName.ShouldBe("suite_member_case_revision_fk", error.MessageText);
    }

    // ------------------------------------------------------------------ AC3 DAG

    [Fact]
    public async Task Revision_WithACycle_Is422_WithThePath_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var members = new[] { Member(world.Revisions[0], null, "a"), Member(world.Revisions[1], null, "b"), Member(world.Revisions[2], null, "c") };

        var cycle = await CreateRevisionAsync(world, new { members, edges = new[] { new { parent = "a", child = "b" }, new { parent = "b", child = "c" }, new { parent = "c", child = "a" } } });

        cycle.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        cycle.ErrorCode.ShouldBe("dependency_cycle");
        var path = cycle.Body.GetProperty("error").GetProperty("details").EnumerateArray().Select(detail => detail.GetProperty("reason").GetString()).ToList();
        path.Count.ShouldBe(4);
        path[0].ShouldBe(path[^1]);
        path.Distinct().Order().ShouldBe(["a", "b", "c"]);

        var self = await CreateRevisionAsync(world, new { members, edges = new[] { new { parent = "b", child = "b" } } });
        self.ErrorCode.ShouldBe("dependency_cycle");
        self.Body.GetProperty("error").GetProperty("details").EnumerateArray().Select(detail => detail.GetProperty("reason").GetString()).ShouldBe(["b", "b"]);

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.suite_revision where org_id = '{world.Org}'")).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.variant where org_id = '{world.Org}'")).ShouldBe(0);
    }

    [Fact]
    public async Task Revision_WithBadRefsOrDuplicates_Is422()
    {
        await using var world = await StartAsync();
        var one = Member(world.Revisions[0], null, "a");

        (await CreateRevisionAsync(world, new { members = new[] { one }, edges = new[] { new { parent = "a", child = "ghost" } } })).ErrorCode.ShouldBe("request_invalid");
        (await CreateRevisionAsync(world, new { members = new[] { one, Member(world.Revisions[1], null, "a") } })).ErrorCode.ShouldBe("request_invalid");
        (await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], null, "bad ref!") } })).ErrorCode.ShouldBe("request_invalid");
        (await CreateRevisionAsync(world, new { members = new[] { Member(Guid.Empty) } })).ErrorCode.ShouldBe("request_invalid");

        // The same case revision under the same variant twice.
        var twice = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], new { browser = "a" }, "x"), Member(world.Revisions[0], new { Browser = "A", note = "n" }, "y") } });
        twice.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        twice.Body.GetProperty("error").GetProperty("details").EnumerateArray().Select(detail => detail.GetProperty("reason").GetString()).ShouldAllBe(reason => reason == "duplicate_member");

        // The same case revision under two variants is fine.
        (await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], new { browser = "a" }, "x"), Member(world.Revisions[0], new { browser = "b" }, "y") } })).Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Revision_BodyIsStrict_AndBounded()
    {
        await using var world = await StartAsync();

        async Task<HttpStatusCode> Send(string text)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/suites/{world.Suite}/revisions") { Content = new StringContent(text, System.Text.Encoding.UTF8, "application/json") };
            world.Admin(request.Headers);
            request.Headers.Add("Idempotency-Key", NewKey());
            using var response = await world.Client.SendAsync(request, TestContext.Current.CancellationToken);

            return response.StatusCode;
        }

        (await Send("{not json")).ShouldBe(HttpStatusCode.BadRequest);
        (await Send("[1]")).ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Send("{\"unknown\":1}")).ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Send("{\"members\":[{\"case_revision_id\":\"" + world.Revisions[0] + "\",\"extra\":1}]}")).ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Send("{\"members\":\"x\"}")).ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Send("{\"pad\":\"" + new string('x', 4 * 1024 * 1024) + "\"}")).ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    // ------------------------------------------------------------------ AC4 / AC8 secrets and strategies

    [Fact]
    public async Task Revision_WithASecretInParams_Is422_NotSecretFree_AndStoresNothing()
    {
        await using var world = await StartAsync();

        foreach (var parameters in new object[]
        {
            new { api_token = "x" },
            new { password = "hunter2" },
            new { Secret = 1 },
            new { mode = "ghp_abcdefghijklmnopqrstuvwxyz0123456789" },
            new { data = "q8Zt3RkP0vLm9XwB2nYc5HsD7JfA4GeU" },
            new { url = "https://user:hunter2pass@example.com" },
        })
        {
            var answer = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], parameters, "m") } });
            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
            answer.ErrorCode.ShouldBe("variant_not_secret_free");
            answer.Body.GetProperty("error").GetProperty("details").EnumerateArray().First().GetProperty("path").GetString().ShouldStartWith("/members/0/params/");
        }

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.variant where org_id = '{world.Org}'")).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.suite_revision where org_id = '{world.Org}'")).ShouldBe(0);
    }

    [Fact]
    public async Task Revision_VariantsWithTheSameNormalisedParams_ShareOneRow_AcrossRevisions()
    {
        await using var world = await StartAsync();
        await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], new { browser = "Chromium", retries = 1 }) } });
        await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], new { retries = 1, browser = "chromium", note = "x" }), Member(world.Revisions[1], new { browser = "CHROMIUM", retries = 1 }) } });

        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.variant where org_id = '{world.Org}'")).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(distinct variant_id) from public.suite_member where org_id = '{world.Org}'")).ShouldBe(1);
    }

    [Theory]
    [InlineData("""{"shard_strategy":{"kind":"count"}}""")]
    [InlineData("""{"shard_strategy":{"kind":"none","count":2}}""")]
    [InlineData("""{"shard_strategy":{"kind":"count","count":0}}""")]
    [InlineData("""{"retry_policy":{"max_retries":9}}""")]
    [InlineData("""{"retry_policy":{"max_retries":1,"only_on_status":["passed"]}}""")]
    [InlineData("""{"environment_requirements":{"unknown":1}}""")]
    [InlineData("""{"environment_requirements":{"secrets":["lower"]}}""")]
    [InlineData("""{"environment_requirements":{"data_isolation":"maybe"}}""")]
    [InlineData("""{"environment_requirements":{"base_urls":{"web":"not a uri"}}}""")]
    public async Task Revision_WithAnInvalidStrategyDocument_Is422(string body)
    {
        await using var world = await StartAsync();

        var answer = await world.Client.SendAsync(HttpMethod.Post, $"/v1/suites/{world.Suite}/revisions", world.Admin, JsonSerializer.Deserialize<JsonElement>(body), NewKey());

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, body);
        answer.ErrorCode.ShouldBe("request_invalid");
    }

    [Fact]
    public async Task Revision_EnvironmentRequirements_NeverHoldASecretValue()
    {
        await using var world = await StartAsync();

        foreach (var environment in new object[]
        {
            new { base_urls = new { web = "https://user:hunter2pass@staging.example.com" } },
            new { dependency_versions = new { svc = "ghp_abcdefghijklmnopqrstuvwxyz0123456789" } },
            new { dependency_versions = new { api_token = "1" } },
            new { warm_up = new { path = "Bearer abcdefgh" } },
        })
        {
            var answer = await CreateRevisionAsync(world, new { environment_requirements = environment });
            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, environment.ToString());
            answer.ErrorCode.ShouldBe("variant_not_secret_free");
        }

        // Names of secrets are exactly what the field is for.
        (await CreateRevisionAsync(world, new { environment_requirements = new { secrets = new[] { "API_TOKEN", "DB_PASSWORD" } } })).Status.ShouldBe(HttpStatusCode.Created);
    }

    // ------------------------------------------------------------------ AC5 numbering

    [Fact]
    public async Task ConcurrentRevisionCreates_GetConsecutiveNumbers_NeverALostOne()
    {
        await using var world = await StartAsync(8);

        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[i]) } })));

        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.Created);
        answers.Select(answer => answer.Body.GetProperty("revision_no").GetInt32()).Order().ShouldBe(Enumerable.Range(1, 8));
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(distinct revision_no) from public.suite_revision where org_id = '{world.Org}'")).ShouldBe(8);
        (await _fixture.ScalarAsync<int>(world.Database, $"select revision_no from public.suite_revision where id = (select head_revision_id from public.suite where id = '{world.Suite}')")).ShouldBeInRange(1, 8);
    }

    [Fact]
    public async Task ConcurrentCreatesOfTheSameContent_YieldOneRevision()
    {
        await using var world = await StartAsync();

        var answers = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], new { browser = "x" }) } })));

        answers.Count(answer => answer.Status == HttpStatusCode.Created).ShouldBe(1);
        answers.Select(answer => answer.Id("suite_revision_id")).Distinct().Count().ShouldBe(1);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.suite_revision where org_id = '{world.Org}'")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ AC6 immutability

    [Fact]
    public async Task Revision_Content_IsFrozenAtTheDatabase_ForEveryRole()
    {
        await using var world = await StartAsync();
        var created = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], null, "a"), Member(world.Revisions[1], null, "b") }, edges = new[] { new { parent = "a", child = "b" } } });
        created.Status.ShouldBe(HttpStatusCode.Created);

        foreach (var statement in new List<string>
        {
            "update public.suite_revision set revision_hash = 'sha256:" + new string('0', 64) + "'",
            "update public.suite_revision set shard_strategy = '{\"kind\":\"none\"}'::jsonb",
            "update public.suite_revision set revision_no = 99",
            "update public.suite_revision set status = 'draft'",
            "delete from public.suite_revision",
            "update public.suite_member set required = false",
            "delete from public.suite_member",
            "update public.case_dependency set child_member_id = parent_member_id",
            "delete from public.case_dependency",
            "update public.variant set params = '{\"x\":1}'",
            "delete from public.variant",
        })
        {
            await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(world.Database));
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
            {
                set.Parameters.AddWithValue("org", world.Org.ToString());
                await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            }

            await using var command = new NpgsqlCommand(statement, connection, transaction);
            var asApp = await Should.ThrowAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            asApp.SqlState.ShouldBeOneOf(["42501", "23514"], statement);

            var asOwner = await Should.ThrowAsync<PostgresException>(async () => await _fixture.ExecuteAsync(world.Database, statement));
            asOwner.MessageText.ShouldContain("revision_immutable", customMessage: statement);
        }
    }

    [Fact]
    public async Task Publishing_IsAOneWayFlip_AfterWhichNothingMoves()
    {
        await using var world = await StartAsync();
        var created = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], new { retries = 1 }) }, environment_requirements = new { data_isolation = "shared" } });
        var id = created.Id("suite_revision_id");

        await using (var services = world.Api.Services.CreateAsyncScope())
        {
            var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
            await using var scope = await scopes.BeginAsync(world.Org, null, false, TestContext.Current.CancellationToken);
            var contracts = services.ServiceProvider.GetRequiredService<ICatalogContracts>();

            (await contracts.GetDataIsolationAsync(id, TestContext.Current.CancellationToken)).ShouldBe("shared");
            (await contracts.PublishSuiteRevisionAsync(id, TestContext.Current.CancellationToken)).ShouldBeTrue();
            (await contracts.PublishSuiteRevisionAsync(id, TestContext.Current.CancellationToken)).ShouldBeTrue();
            (await contracts.PublishSuiteRevisionAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken)).ShouldBeFalse();
            (await contracts.GetDataIsolationAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken)).ShouldBeNull();
            await scope.CommitAsync(TestContext.Current.CancellationToken);
        }

        (await _fixture.ScalarAsync<string>(world.Database, $"select status from public.suite_revision where id = '{id}'")).ShouldBe("published");
        var read = await world.Client.SendAsync(HttpMethod.Get, $"/v1/suite-revisions/{id}", world.Admin);
        read.Text("status").ShouldBe("published");

        // Back to draft, or any other edit, is refused — even for the owner.
        var back = await Should.ThrowAsync<PostgresException>(async () => await _fixture.ExecuteAsync(world.Database, $"update public.suite_revision set status = 'draft' where id = '{id}'"));
        back.MessageText.ShouldContain("revision_immutable");

        // Re-creating identical content finds the published revision instead of making another.
        var again = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], new { retries = 1 }) }, environment_requirements = new { data_isolation = "shared" } });
        again.Status.ShouldBe(HttpStatusCode.OK);
        again.Text("status").ShouldBe("published");
    }

    [Fact]
    public async Task ArchivedSuite_TakesNoNewRevision()
    {
        await using var world = await StartAsync();
        await _fixture.ExecuteAsync(world.Database, $"update public.suite set status = 'archived' where id = '{world.Suite}'");

        var answer = await CreateRevisionAsync(world, new { });

        answer.Status.ShouldBe(HttpStatusCode.Conflict);
        answer.ErrorCode.ShouldBe("invalid_transition");
    }

    // ------------------------------------------------------------------ AC7 diff

    [Fact]
    public async Task Diff_ReportsMembersEdgesAndStrategyChanges()
    {
        await using var world = await StartAsync();
        var first = await CreateRevisionAsync(world, new
        {
            members = new[] { Member(world.Revisions[0], null, "a"), Member(world.Revisions[1], null, "b"), Member(world.Revisions[2], null, "c") },
            edges = new[] { new { parent = "a", child = "b" } },
            retry_policy = new { max_retries = 0 },
        });
        var second = await CreateRevisionAsync(world, new
        {
            members = new[] { Member(world.Revisions[0], null, "a"), Member(world.Revisions[1], null, "b", required: false), Member(world.Revisions[3], null, "d") },
            edges = new[] { new { parent = "a", child = "d" } },
            retry_policy = new { max_retries = 2 },
            shard_strategy = new { kind = "count", count = 3 },
        });

        var diff = await world.Client.SendAsync(HttpMethod.Get, $"/v1/suite-revisions/{second.Id("suite_revision_id")}/diff?against={first.Id("suite_revision_id")}", world.Admin);

        diff.Status.ShouldBe(HttpStatusCode.OK, diff.ErrorMessage);
        diff.Body.GetProperty("members_added").EnumerateArray().Single().GetProperty("case_revision_id").GetGuid().ShouldBe(world.Revisions[3]);
        diff.Body.GetProperty("members_removed").EnumerateArray().Single().GetProperty("case_revision_id").GetGuid().ShouldBe(world.Revisions[2]);
        diff.Body.GetProperty("required_changed").EnumerateArray().Single().GetProperty("case_revision_id").GetGuid().ShouldBe(world.Revisions[1]);
        diff.Body.GetProperty("edges_added").GetArrayLength().ShouldBe(1);
        diff.Body.GetProperty("edges_removed").GetArrayLength().ShouldBe(1);
        diff.Body.GetProperty("retry_policy").GetProperty("to").GetProperty("max_retries").GetInt32().ShouldBe(2);
        diff.Body.GetProperty("shard_strategy").GetProperty("from").GetProperty("kind").GetString().ShouldBe("none");
        diff.Body.TryGetProperty("environment_requirements", out var environment).ShouldBeTrue();
        environment.ValueKind.ShouldBe(JsonValueKind.Null);

        var same = await world.Client.SendAsync(HttpMethod.Get, $"/v1/suite-revisions/{second.Id("suite_revision_id")}/diff?against={second.Id("suite_revision_id")}", world.Admin);
        same.Body.GetProperty("members_added").GetArrayLength().ShouldBe(0);
        same.Body.GetProperty("retry_policy").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Diff_RefusesUnknownRevisions_OtherSuites_AndAMissingAgainst()
    {
        await using var world = await StartAsync();
        var mine = await CreateRevisionAsync(world, new { });
        var other = await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/suites", world.Admin, new { name = "Other", slug = "other" }, NewKey());
        var theirs = await world.Client.SendAsync(HttpMethod.Post, $"/v1/suites/{other.Id()}/revisions", world.Admin, new { }, NewKey());
        var id = mine.Id("suite_revision_id");

        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/suite-revisions/{id}/diff?against={Guid.CreateVersion7()}", world.Admin)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/suite-revisions/{Guid.CreateVersion7()}/diff?against={id}", world.Admin)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/suite-revisions/{id}/diff?against={theirs.Id("suite_revision_id")}", world.Admin)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/suite-revisions/{id}/diff", world.Admin)).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/suite-revisions/{id}/diff?against=nope", world.Admin)).Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Revision_OfAnotherOrg_IsNotFound()
    {
        await using var world = await StartAsync();
        var created = await CreateRevisionAsync(world, new { });
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));
        var rival = TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg);

        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/suite-revisions/{created.Id("suite_revision_id")}", rival)).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ preview and statement budget

    [Fact]
    public async Task DryRun_ReturnsTheRealHash_AndWritesNothing()
    {
        await using var world = await StartAsync();
        var body = new { members = new[] { Member(world.Revisions[0], new { browser = "chromium" }, "a"), Member(world.Revisions[1], null, "b") }, edges = new[] { new { parent = "a", child = "b" } } };

        var preview = await CreateRevisionAsync(world, body, "?dry_run=true");

        preview.Status.ShouldBe(HttpStatusCode.OK, preview.ErrorMessage);
        (preview.Text("status"), preview.Body.GetProperty("created").GetBoolean(), preview.Body.GetProperty("revision_no").GetInt32()).ShouldBe(("preview", false, 1));
        preview.Body.GetProperty("suite_revision_id").ValueKind.ShouldBe(JsonValueKind.Null);
        foreach (var table in new[] { "suite_revision", "suite_member", "variant", "case_dependency" })
        {
            (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.{table} where org_id = '{world.Org}'")).ShouldBe(0, table);
        }

        var real = await CreateRevisionAsync(world, body);
        real.Text("revision_hash").ShouldBe(preview.Text("revision_hash"));

        // Once it exists the preview points at it.
        var again = await CreateRevisionAsync(world, body, "?dry_run=true");
        again.Id("suite_revision_id").ShouldBe(real.Id("suite_revision_id"));

        // The preview validates like the real thing, without writing.
        (await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], new { password = "x" }) } }, "?dry_run=true")).ErrorCode.ShouldBe("variant_not_secret_free");
        (await CreateRevisionAsync(world, new { members = new[] { Member(Guid.CreateVersion7()) } }, "?dry_run=true")).ErrorCode.ShouldBe("cross_project_member");
    }

    [Fact]
    public async Task Create_OfFiveHundredMembers_IsAFixedNumberOfStatements()
    {
        await using var world = await StartAsync(500);
        var members = world.Revisions.Select((revision, i) => Member(revision, new { shard = i % 7 }, $"m{i}")).ToArray();
        var edges = Enumerable.Range(1, 499).Select(i => new { parent = $"m{i - 1}", child = $"m{i}" }).ToArray();

        world.Api.Statements.Clear();
        var created = await CreateRevisionAsync(world, new { members, edges });

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var writes = world.Api.Statements.Snapshot().Where(statement => statement.Contains("public.", StringComparison.Ordinal)).ToList();
        writes.Count.ShouldBeLessThanOrEqualTo(15, string.Join(Environment.NewLine, writes));
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.suite_member where org_id = '{world.Org}'")).ShouldBe(500);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.case_dependency where org_id = '{world.Org}'")).ShouldBe(499);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.variant where org_id = '{world.Org}'")).ShouldBe(7);
    }

    [Fact]
    public async Task Rls_HidesAnotherOrgsSuiteRows()
    {
        await using var world = await StartAsync();
        await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0]) } });
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));

        await using var services = world.Api.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        var db = services.ServiceProvider.GetRequiredService<ITenantDb>();
        await using (await scopes.BeginAsync(otherOrg, null, true, TestContext.Current.CancellationToken))
        {
            foreach (var table in new[] { "suite", "suite_revision", "variant", "suite_member", "case_dependency" })
            {
                (await db.QueryAsync<Guid>($"select id from public.{table}", null, TestContext.Current.CancellationToken)).ShouldBeEmpty(table);
            }
        }


    }

    // ------------------------------------------------------------------ review fixes (03.2.1 §8a)

    [Fact]
    public async Task Resubmitting_AnOlderRevisionsContent_MovesTheHeadBack()
    {
        await using var world = await StartAsync();
        var first = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], null, "a") } });
        var second = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], null, "a"), Member(world.Revisions[1], null, "b") } });
        first.Status.ShouldBe(HttpStatusCode.Created);
        second.Status.ShouldBe(HttpStatusCode.Created);

        var revert = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], null, "a") } });

        revert.Status.ShouldBe(HttpStatusCode.OK);
        revert.Body.GetProperty("head_moved").GetBoolean().ShouldBeTrue();
        revert.Id("suite_revision_id").ShouldBe(first.Id("suite_revision_id"));
        var read = await world.Client.SendAsync(HttpMethod.Get, $"/v1/suites/{world.Suite}", world.Admin);
        read.Body.GetProperty("head_revision_no").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task DryRun_MustBeABoolean()
    {
        await using var world = await StartAsync();

        var answer = await CreateRevisionAsync(world, new { }, "?dry_run=maybe");

        answer.Status.ShouldBe(HttpStatusCode.BadRequest);
        (await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.suite_revision where org_id = '{world.Org}'")).ShouldBe(0);
    }

    [Fact]
    public async Task Revision_IsServedWithItsHashAsETag_AndCachesPrivately()
    {
        await using var world = await StartAsync();
        var created = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0]) } });

        var read = await world.Client.SendAsync(HttpMethod.Get, $"/v1/suite-revisions/{created.Id("suite_revision_id")}", world.Admin);

        read.ETag.ShouldBe($"\"{read.Text("revision_hash")}\"");
        read.Headers.CacheControl!.ToString().ShouldContain("immutable");
    }

    [Fact]
    public async Task Database_RefusesAMemberAddedToAnOlderRevision()
    {
        await using var world = await StartAsync();
        var created = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0]) } });
        var variant = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(world.Database, $"insert into public.variant (id, org_id, project_id, variant_hash, params) values ('{variant}', '{world.Org}', '{world.Project}', 'sha256:{new string('e', 64)}', jsonb_build_object())");

        var error = await Should.ThrowAsync<PostgresException>(async () => await _fixture.ExecuteAsync(
            world.Database,
            $"insert into public.suite_member (id, org_id, suite_revision_id, case_revision_id, variant_id, ordinal) values ('{Guid.CreateVersion7()}', '{world.Org}', '{created.Id("suite_revision_id")}', '{world.Revisions[1]}', '{variant}', 5)"));

        error.SqlState.ShouldBe("23514");
    }

    // ------------------------------------------------------------------ 03.1.4 taxonomy

    private static IEnumerable<string> DetailPaths(ApiAnswer answer) =>
        answer.Body.GetProperty("error").GetProperty("details").EnumerateArray().Select(detail => detail.GetProperty("path").GetString()!);

    [Fact]
    public async Task SuiteTaxonomy_OutOfCatalogue_422()
    {
        await using var world = await StartAsync();

        (await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/suites", world.Admin, new { name = "desk", slug = "desk", platform = "desktop" }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/suites", world.Admin, new { name = "lev", slug = "lev", level = "smoke_test" }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/suites", world.Admin, new { name = "pur", slug = "pur", purpose = "gate" }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // The level follows the platform: an api suite is an integration suite unless the caller says otherwise.
        var api = await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/suites", world.Admin, new { name = "api", slug = "api-suite", platform = "api" }, NewKey());
        api.Status.ShouldBe(HttpStatusCode.Created, api.ErrorMessage);
        api.Text("platform").ShouldBe("api");
        api.Text("level").ShouldBe("integration");
        api.Text("purpose").ShouldBe("regression");

        // A revision and a list filter are held to the same lists, and each names its own member.
        var revision = await CreateRevisionAsync(world, new { level = "weekly" });
        revision.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        DetailPaths(revision).ShouldContain("/level");

        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/suites?platform=desktop", world.Admin))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/suites?purpose=gate", world.Admin))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task SuiteTaxonomy_RoundTrips_AndFiltersTheList()
    {
        await using var world = await StartAsync();
        var api = await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/suites", world.Admin, new { name = "Api", slug = "api-suite", platform = "api", purpose = "release_gate" }, NewKey());
        api.Status.ShouldBe(HttpStatusCode.Created, api.ErrorMessage);
        var apiId = api.Id();

        var web = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/suites?platform=web", world.Admin);
        web.Body.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(world.Suite);

        var gate = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/suites?purpose=release_gate&level=integration", world.Admin);
        gate.Body.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(apiId);

        var read = await world.Client.SendAsync(HttpMethod.Get, $"/v1/suites/{apiId}", world.Admin);
        read.Text("platform").ShouldBe("api");
        read.Text("level").ShouldBe("integration");
        read.Text("purpose").ShouldBe("release_gate");
    }

    [Fact]
    public async Task SuiteTaxonomyChange_CreatesNewRevision_OldHashStable()
    {
        await using var world = await StartAsync(1);
        var first = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0]) } });
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);

        var second = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0]) }, purpose = "release_gate" });
        second.Status.ShouldBe(HttpStatusCode.Created, second.ErrorMessage);
        second.Text("purpose").ShouldBe("release_gate");
        second.Body.GetProperty("revision_hash").GetString().ShouldNotBe(first.Body.GetProperty("revision_hash").GetString());

        // The suite now reads as the head revision it holds.
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/suites/{world.Suite}", world.Admin)).Text("purpose").ShouldBe("release_gate");

        // The older revision keeps the taxonomy and the hash it was written with: nothing recomputes an old hash.
        var older = await world.Client.SendAsync(HttpMethod.Get, $"/v1/suite-revisions/{first.Id("suite_revision_id")}", world.Admin);
        older.Status.ShouldBe(HttpStatusCode.OK);
        older.Text("purpose").ShouldBe("regression");
        older.Body.GetProperty("revision_hash").GetString().ShouldBe(first.Body.GetProperty("revision_hash").GetString());

        // Re-submitting the older content, taxonomy included, moves the head and the taxonomy back with it. The taxonomy
        // is part of the hash, so the revert is only the same revision when the request names the old taxonomy too.
        var again = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0]) }, purpose = "regression" });
        again.Status.ShouldBe(HttpStatusCode.OK, again.ErrorMessage);
        again.Body.GetProperty("created").GetBoolean().ShouldBeFalse();
        again.Body.GetProperty("head_moved").GetBoolean().ShouldBeTrue();
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/suites/{world.Suite}", world.Admin)).Text("purpose").ShouldBe("regression");

        // The taxonomy moved, so the diff between the new head and the revision before it says so.
        var diff = await world.Client.SendAsync(
            HttpMethod.Get, $"/v1/suite-revisions/{again.Id("suite_revision_id")}/diff?against={second.Id("suite_revision_id")}", world.Admin);
        diff.Status.ShouldBe(HttpStatusCode.OK, diff.ErrorMessage);
        diff.Body.GetProperty("taxonomy").GetProperty("from").GetProperty("purpose").GetString().ShouldBe("release_gate");
        diff.Body.GetProperty("taxonomy").GetProperty("to").GetProperty("purpose").GetString().ShouldBe("regression");
        diff.Body.GetProperty("members_added").GetArrayLength().ShouldBe(0);
        diff.Body.GetProperty("shard_strategy").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task SuiteTaxonomy_OmittedMember_KeepsTheCurrentValue()
    {
        await using var world = await StartAsync(2);
        var first = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0]) }, purpose = "release_gate", level = "smoke" });
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);

        // A request that names no taxonomy at all inherits all three: an omitted member is not a reset to the default.
        var inherited = await CreateRevisionAsync(world, new
        {
            members = new[] { Member(world.Revisions[0]), Member(world.Revisions[1]) },
        });
        inherited.Status.ShouldBe(HttpStatusCode.Created, inherited.ErrorMessage);
        inherited.Text("purpose").ShouldBe("release_gate");
        inherited.Text("level").ShouldBe("smoke");
        inherited.Text("platform").ShouldBe("web");

        // Naming only the purpose keeps the level the suite holds.
        var partly = await CreateRevisionAsync(world, new
        {
            members = new[] { Member(world.Revisions[0]), Member(world.Revisions[1]) },
            purpose = "exploratory",
            shard_strategy = new { kind = "count", count = 3 },
        });
        partly.Status.ShouldBe(HttpStatusCode.Created, partly.ErrorMessage);
        partly.Text("purpose").ShouldBe("exploratory");
        partly.Text("level").ShouldBe("smoke");
        partly.Text("platform").ShouldBe("web");
    }

    [Fact]
    public async Task CreateSuite_MobileWithoutCapability_422()
    {
        await using var world = await StartAsync();

        // A new suite has no member whose variant could name a native platform: `mobile` would be unexecutable.
        var mobile = await world.Client.SendAsync(
            HttpMethod.Post, $"/v1/projects/{world.Project}/suites", world.Admin, new { name = "mob", slug = "mob-suite", platform = "mobile" }, NewKey());
        mobile.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        DetailPaths(mobile).ShouldContain("/platform");
        mobile.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetProperty("reason").GetString()).ShouldContain("mobile_capability_required");

        // The other three platforms are accepted and keep their catalogue defaults.
        foreach (var (platform, level) in new[] { ("web", "e2e"), ("api", "integration"), ("unit", "unit") })
        {
            var created = await world.Client.SendAsync(
                HttpMethod.Post, $"/v1/projects/{world.Project}/suites", world.Admin, new { name = platform, slug = $"{platform}-suite", platform }, NewKey());
            created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
            created.Text("level").ShouldBe(level);
            created.Text("purpose").ShouldBe("regression");
        }
    }

    [Fact]
    public async Task MobileViewportVariant_OnWebRunner_StoredAsWeb()
    {
        await using var world = await StartAsync(1);

        // A browser variant under `mobile` is device emulation: the revision is stored as a web suite.
        var emulated = await CreateRevisionAsync(world, new
        {
            members = new[] { Member(world.Revisions[0], new { browser = "chromium", viewport = "390x844" }) },
            platform = "mobile",
        });
        emulated.Status.ShouldBe(HttpStatusCode.Created, emulated.ErrorMessage);
        emulated.Text("platform").ShouldBe("web");
        emulated.Body.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()).ShouldContain(
            warning => warning!.Contains("device emulation", StringComparison.Ordinal));

        // Without a native platform anywhere, `mobile` cannot be executed by any runner: refused, naming the member.
        var unsupported = await CreateRevisionAsync(world, new { platform = "mobile" });
        unsupported.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        DetailPaths(unsupported).ShouldContain("/platform");

        // A variant naming the platform runs natively, so the suite is stored as mobile.
        var native = await CreateRevisionAsync(world, new
        {
            members = new[] { Member(world.Revisions[0], new { platform = "android" }) },
            platform = "mobile",
        });
        native.Status.ShouldBe(HttpStatusCode.Created, native.ErrorMessage);
        native.Text("platform").ShouldBe("mobile");
    }

    [Fact]
    public async Task MobilePlatform_MixedVariants_StayMobile()
    {
        await using var world = await StartAsync(2);

        // One native variant is enough: the suite is not device emulation just because another member uses a browser.
        var mixed = await CreateRevisionAsync(world, new
        {
            members = new[]
            {
                Member(world.Revisions[0], new { platform = "android" }),
                Member(world.Revisions[1], new { browser = "chromium", viewport = "390x844" }),
            },
            platform = "mobile",
        });
        mixed.Status.ShouldBe(HttpStatusCode.Created, mixed.ErrorMessage);
        mixed.Text("platform").ShouldBe("mobile");
        mixed.Body.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString())
            .ShouldNotContain(warning => warning!.Contains("device emulation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Taxonomy_CrossOrgInvisible_AndNeedsSuiteWrite()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));
        var rival = TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg);

        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/suites/{world.Suite}", rival)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/suites?platform=web", rival)).Status.ShouldBe(HttpStatusCode.NotFound);

        var reader = TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["suite:read"]);
        (await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{world.Project}/suites", reader, new { name = "x", slug = "x-suite", platform = "api" }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.Forbidden);
        (await world.Client.SendAsync(HttpMethod.Post, $"/v1/suites/{world.Suite}/revisions", reader, new { purpose = "smoke" }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.Forbidden);

        // The taxonomy never crosses a project either: another project of the same org has its own suites.
        var secondProject = await CreateProjectAsync(world.Client, world.User, world.Org, "billing");
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{secondProject}/suites?platform=web", world.Admin))
            .Body.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Database_RefusesATaxonomyChangeWithoutItsRevision()
    {
        await using var world = await StartAsync(1);
        var created = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0]) } });
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        var changed = await Should.ThrowAsync<PostgresException>(async () => await _fixture.ExecuteAsync(
            world.Database,
            $"update public.suite set purpose = 'smoke' where id = '{world.Suite}'"));

        changed.SqlState.ShouldBe("23514");
    }
}
