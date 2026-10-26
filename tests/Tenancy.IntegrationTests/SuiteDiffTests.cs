// Request bodies are written inline so each test reads as the request it sends; the arrays are tiny and built once per test.
#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The gaps 03.2.1's suite diff, suite ETag and dry-run preview leave open beyond the happy paths of
/// <see cref="CatalogSuiteTests"/>: determinism and direction, variant changes, another org's revision being
/// indistinguishable from a missing one, the strong suite ETag and its 304s, that a preview writes no row anywhere, and
/// that a preview needs the same <c>suite:write</c> standing as a real create.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class SuiteDiffTests
{
    private const string Commit1 = "aaaaaaa1111111";

    private readonly PostgresTenancyFixture _fixture;

    public SuiteDiffTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project, Guid Suite, Guid[] Revisions)
        : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>A second tenant of the same host and database: its own user, org, project, cases and suite.</summary>
    private sealed record Rival(Guid User, Guid Org, Guid Project, Guid Suite, Guid[] Revisions)
    {
        public Action<HttpRequestHeaders> As => TenancyApiFactory.Resolved(user: User, orgHeader: Org);
    }

    /// <summary>One response kept as text, so "byte-identical" can be asserted as such.</summary>
    private sealed record Raw(HttpStatusCode Status, string Text, JsonElement Body, HttpResponseHeaders Headers)
    {
        public string? ETag => Headers.ETag?.Tag;

        public bool ETagIsWeak => Headers.ETag?.IsWeak ?? false;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>An org with a project, <paramref name="cases"/> registered cases (one revision each) and an empty suite.</summary>
    private async Task<World> StartAsync(int cases = 4)
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");
        var revisions = await SeedCasesAsync(api, org, project, database, cases, 0);

        var admin = TenancyApiFactory.Resolved(user: user, orgHeader: org);
        var suite = await client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/suites", admin, new { name = "Smoke", slug = "smoke" }, NewKey());
        suite.Status.ShouldBe(HttpStatusCode.Created, suite.ErrorMessage);

        return new World(api, client, database, user, org, project, suite.Id(), revisions);
    }

    /// <summary>A second org on the same host and database, ready to take revisions of its own.</summary>
    private async Task<Rival> StartRivalAsync(World world, string slug = "rival")
    {
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(world.Client, user, slug);
        var project = await CreateProjectAsync(world.Client, user, org, "theirs");
        var revisions = await SeedCasesAsync(world.Api, org, project, world.Database, 2, 300);
        var admin = TenancyApiFactory.Resolved(user: user, orgHeader: org);
        var suite = await world.Client.SendAsync(HttpMethod.Post, $"/v1/projects/{project}/suites", admin, new { name = "Smoke", slug = "smoke" }, NewKey());
        suite.Status.ShouldBe(HttpStatusCode.Created, suite.ErrorMessage);

        return new Rival(user, org, project, suite.Id(), revisions);
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

    /// <summary>Sends one request and keeps the body as text, which <see cref="ApiAnswer"/> parses away.</summary>
    private static async Task<Raw> RawAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        Action<HttpRequestHeaders> caller,
        object? body = null,
        string? idempotencyKey = null,
        string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(method, url);
        caller(request.Headers);

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new Raw(response.StatusCode, text, document.RootElement.Clone(), response.Headers);
    }

    /// <summary>The <c>(case_revision_id, variant_hash)</c> keys of one diff array, in the order the server sent them.</summary>
    private static List<(Guid Case, string Variant)> MemberKeys(JsonElement body, string property) =>
    [
        .. body.GetProperty(property).EnumerateArray()
            .Select(key => (key.GetProperty("case_revision_id").GetGuid(), key.GetProperty("variant_hash").GetString()!)),
    ];

    /// <summary>The <c>(parent, child)</c> case-revision pairs of one diff edge array, in the order the server sent them.</summary>
    private static List<(Guid Parent, Guid Child)> EdgeKeys(JsonElement body, string property) =>
    [
        .. body.GetProperty(property).EnumerateArray()
            .Select(edge => (
                edge.GetProperty("parent").GetProperty("case_revision_id").GetGuid(),
                edge.GetProperty("child").GetProperty("case_revision_id").GetGuid())),
    ];

    /// <summary>The single member of a stored revision: its variant hash.</summary>
    private static async Task<string> VariantHashAsync(World world, Guid revision)
    {
        var read = await world.Client.SendAsync(HttpMethod.Get, $"/v1/suite-revisions/{revision}", world.Admin);
        read.Status.ShouldBe(HttpStatusCode.OK, read.ErrorMessage);

        return read.Body.GetProperty("members").EnumerateArray().Single().GetProperty("variant_hash").GetString()!;
    }

    /// <summary>
    /// The seven row counts a preview must leave untouched: the four suite tables, the audit trail, the outbox and the
    /// idempotency store (migration names of 0007 and 02.7.1). The database is fresh per test, so a count is this test's.
    /// </summary>
    private async Task<long[]> CountsAsync(World world) =>
    [
        await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.suite_revision"),
        await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.suite_member"),
        await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.variant"),
        await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.case_dependency"),
        await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.audit_log"),
        await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.outbox"),
        await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.idempotency_record"),
    ];

    /// <summary>An active member of the org holding the built-in <paramref name="roleCode"/>, granted by the org's founder.</summary>
    private async Task<Guid> AddMemberAsync(World world, string roleCode)
    {
        var userId = Guid.CreateVersion7();
        var membershipId = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public."user" (id, email) values ('{userId}', 'member-{userId:N}@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{membershipId}', '{world.Org}', '{userId}', 'member-{userId:N}@example.test', 'active')
            """);

        var role = await _fixture.ScalarAsync<Guid>(world.Database, $"select id from public.role where code = '{roleCode}' and org_id is null");
        var granted = await world.Client.SendAsync(
            HttpMethod.Post, $"/v1/orgs/{world.Org}/memberships/{membershipId}/roles", world.Admin, new { role_id = role }, NewKey());
        granted.Status.ShouldBe(HttpStatusCode.OK, granted.ErrorMessage);

        return userId;
    }

    // ------------------------------------------------------------------ AC7 diff: determinism and direction

    [Fact]
    public async Task Diff_IsDeterministic_ForRepeatedAndReversedCalls()
    {
        await using var world = await StartAsync();
        var first = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], null, "a") } });
        // Authored out of order on purpose: the diff's order is the server's, not the request's.
        var second = await CreateRevisionAsync(world, new
        {
            members = new[]
            {
                Member(world.Revisions[2], null, "c"),
                Member(world.Revisions[0], null, "a"),
                Member(world.Revisions[3], null, "d"),
                Member(world.Revisions[1], null, "b"),
            },
            edges = new[] { new { parent = "d", child = "b" }, new { parent = "a", child = "c" }, new { parent = "a", child = "d" } },
        });
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        second.Status.ShouldBe(HttpStatusCode.Created, second.ErrorMessage);

        var forwardUrl = $"/v1/suite-revisions/{second.Id("suite_revision_id")}/diff?against={first.Id("suite_revision_id")}";
        var once = await RawAsync(world.Client, HttpMethod.Get, forwardUrl, world.Admin);
        var twice = await RawAsync(world.Client, HttpMethod.Get, forwardUrl, world.Admin);

        once.Status.ShouldBe(HttpStatusCode.OK, once.Text);
        twice.Status.ShouldBe(HttpStatusCode.OK, twice.Text);
        twice.Text.ShouldBe(once.Text);

        // Three added members, ordered by case revision and then variant hash — the order the server promises.
        var added = MemberKeys(once.Body, "members_added");
        added.Count.ShouldBe(3);
        added.Select(key => key.Case).ShouldBe(world.Revisions[1..4].Order().ToArray());
        added.ShouldBe(added.OrderBy(key => key.Case).ThenBy(key => key.Variant, StringComparer.Ordinal).ToList());

        // Three added edges, ordered by parent and then child: the refs a→c, a→d and d→b of the request.
        var addedEdges = EdgeKeys(once.Body, "edges_added");
        var expectedEdges = new List<(Guid Parent, Guid Child)>
        {
            (world.Revisions[0], world.Revisions[2]),
            (world.Revisions[0], world.Revisions[3]),
            (world.Revisions[3], world.Revisions[1]),
        };
        expectedEdges = expectedEdges.OrderBy(edge => edge.Parent).ThenBy(edge => edge.Child).ToList();
        addedEdges.ShouldBe(expectedEdges);

        MemberKeys(once.Body, "members_removed").ShouldBeEmpty();
        EdgeKeys(once.Body, "edges_removed").ShouldBeEmpty();
        MemberKeys(once.Body, "required_changed").ShouldBeEmpty();

        // Reversed, the two directions swap their added and removed sets — nothing appears from nowhere.
        var reverse = await RawAsync(
            world.Client,
            HttpMethod.Get,
            $"/v1/suite-revisions/{first.Id("suite_revision_id")}/diff?against={second.Id("suite_revision_id")}",
            world.Admin);
        reverse.Status.ShouldBe(HttpStatusCode.OK, reverse.Text);
        MemberKeys(reverse.Body, "members_added").ShouldBe(MemberKeys(once.Body, "members_removed"));
        MemberKeys(reverse.Body, "members_removed").ShouldBe(added);
        EdgeKeys(reverse.Body, "edges_added").ShouldBe(EdgeKeys(once.Body, "edges_removed"));
        EdgeKeys(reverse.Body, "edges_removed").ShouldBe(expectedEdges);
        MemberKeys(reverse.Body, "required_changed").ShouldBeEmpty();
    }

    [Fact]
    public async Task Diff_OrdersEdgesOfTheSameCasePair_ByVariant_NotByStorageOrder()
    {
        await using var world = await StartAsync();
        var first = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], null, "x") } });
        // Two variants of each case, and an edge between every variant pair of the same two cases: four edges tie on case ids alone.
        var second = await CreateRevisionAsync(world, new
        {
            members = new[]
            {
                Member(world.Revisions[1], new { browser = "firefox" }, "c2"),
                Member(world.Revisions[0], new { browser = "chromium" }, "p1"),
                Member(world.Revisions[1], new { browser = "chromium" }, "c1"),
                Member(world.Revisions[0], new { browser = "firefox" }, "p2"),
            },
            edges = new[]
            {
                new { parent = "p2", child = "c2" },
                new { parent = "p1", child = "c2" },
                new { parent = "p2", child = "c1" },
                new { parent = "p1", child = "c1" },
            },
        });
        first.Status.ShouldBe(HttpStatusCode.Created, first.ErrorMessage);
        second.Status.ShouldBe(HttpStatusCode.Created, second.ErrorMessage);

        var url = $"/v1/suite-revisions/{second.Id("suite_revision_id")}/diff?against={first.Id("suite_revision_id")}";
        var diff = await RawAsync(world.Client, HttpMethod.Get, url, world.Admin);
        diff.Status.ShouldBe(HttpStatusCode.OK, diff.Text);

        var edges = diff.Body.GetProperty("edges_added").EnumerateArray()
            .Select(edge => (
                ParentCase: edge.GetProperty("parent").GetProperty("case_revision_id").GetGuid(),
                ParentVariant: edge.GetProperty("parent").GetProperty("variant_hash").GetString()!,
                ChildCase: edge.GetProperty("child").GetProperty("case_revision_id").GetGuid(),
                ChildVariant: edge.GetProperty("child").GetProperty("variant_hash").GetString()!))
            .ToList();

        edges.Count.ShouldBe(4);
        edges.Select(edge => edge.ParentVariant).Distinct().Count().ShouldBe(2); // positive control: the ties really are variant ties
        edges.ShouldBe(edges
            .OrderBy(edge => edge.ParentCase).ThenBy(edge => edge.ChildCase)
            .ThenBy(edge => edge.ParentVariant, StringComparer.Ordinal).ThenBy(edge => edge.ChildVariant, StringComparer.Ordinal)
            .ToList());
        (await RawAsync(world.Client, HttpMethod.Get, url, world.Admin)).Text.ShouldBe(diff.Text);
    }

    // ------------------------------------------------------------------ AC7 diff: a variant change

    [Fact]
    public async Task Diff_VariantChange()
    {
        await using var world = await StartAsync();
        var chromium = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], new { browser = "chromium" }, "a") } });
        var firefox = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], new { browser = "firefox" }, "a") } });
        chromium.Status.ShouldBe(HttpStatusCode.Created, chromium.ErrorMessage);
        firefox.Status.ShouldBe(HttpStatusCode.Created, firefox.ErrorMessage);
        chromium.Id("suite_revision_id").ShouldNotBe(firefox.Id("suite_revision_id"));

        var chromiumHash = await VariantHashAsync(world, chromium.Id("suite_revision_id"));
        var firefoxHash = await VariantHashAsync(world, firefox.Id("suite_revision_id"));
        firefoxHash.ShouldNotBe(chromiumHash);

        var diff = await world.Client.SendAsync(
            HttpMethod.Get, $"/v1/suite-revisions/{firefox.Id("suite_revision_id")}/diff?against={chromium.Id("suite_revision_id")}", world.Admin);

        diff.Status.ShouldBe(HttpStatusCode.OK, diff.ErrorMessage);
        // A diff keys its members by (case revision, variant), so the API has no separate "variant" member: the same case
        // revision leaves under the old variant and arrives under the new one — one removal plus one addition, and
        // nothing else moves. Asserting that honestly is the whole of what the DTO exposes.
        MemberKeys(diff.Body, "members_added").ShouldBe(new List<(Guid Case, string Variant)> { (world.Revisions[0], firefoxHash) });
        MemberKeys(diff.Body, "members_removed").ShouldBe(new List<(Guid Case, string Variant)> { (world.Revisions[0], chromiumHash) });
        MemberKeys(diff.Body, "required_changed").ShouldBeEmpty();
        EdgeKeys(diff.Body, "edges_added").ShouldBeEmpty();
        EdgeKeys(diff.Body, "edges_removed").ShouldBeEmpty();
        diff.Body.GetProperty("shard_strategy").ValueKind.ShouldBe(JsonValueKind.Null);
        diff.Body.GetProperty("retry_policy").ValueKind.ShouldBe(JsonValueKind.Null);
        diff.Body.GetProperty("environment_requirements").ValueKind.ShouldBe(JsonValueKind.Null);

        // Positive control: the same revision against itself is empty, so the pair above is the change and not noise.
        var same = await RawAsync(
            world.Client,
            HttpMethod.Get,
            $"/v1/suite-revisions/{firefox.Id("suite_revision_id")}/diff?against={firefox.Id("suite_revision_id")}",
            world.Admin);
        same.Status.ShouldBe(HttpStatusCode.OK, same.Text);
        MemberKeys(same.Body, "members_added").ShouldBeEmpty();
        MemberKeys(same.Body, "members_removed").ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ AC7 diff: another tenant's revision

    [Fact]
    public async Task Diff_AgainstAnotherOrgsRevision_IsIndistinguishableFromMissing()
    {
        await using var world = await StartAsync();
        var rival = await StartRivalAsync(world);
        var mine = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0]) } });
        var theirs = await world.Client.SendAsync(
            HttpMethod.Post, $"/v1/suites/{rival.Suite}/revisions", rival.As, new { members = new[] { Member(rival.Revisions[0]) } }, NewKey());
        mine.Status.ShouldBe(HttpStatusCode.Created, mine.ErrorMessage);
        theirs.Status.ShouldBe(HttpStatusCode.Created, theirs.ErrorMessage);

        var mineId = mine.Id("suite_revision_id");
        var theirsId = theirs.Id("suite_revision_id");

        // Positive control first: each tenant diffs its own revision against itself and gets 200, so the 404s below are
        // about the other tenant's id and not about the route.
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/suite-revisions/{mineId}/diff?against={mineId}", world.Admin)).Status
            .ShouldBe(HttpStatusCode.OK);
        var rivalOwn = await world.Client.SendAsync(HttpMethod.Get, $"/v1/suite-revisions/{theirsId}/diff?against={theirsId}", rival.As);
        rivalOwn.Status.ShouldBe(HttpStatusCode.OK, rivalOwn.ErrorMessage);

        // The baseline: an id that never existed, answered by the same tenant that then asks about the other org's.
        var rivalMissing = await world.Client.SendAsync(
            HttpMethod.Get, $"/v1/suite-revisions/{theirsId}/diff?against={Guid.CreateVersion7()}", rival.As);
        var mineMissing = await world.Client.SendAsync(
            HttpMethod.Get, $"/v1/suite-revisions/{mineId}/diff?against={Guid.CreateVersion7()}", world.Admin);
        rivalMissing.Status.ShouldBe(HttpStatusCode.NotFound);
        rivalMissing.ErrorCode.ShouldNotBeNull();
        mineMissing.Status.ShouldBe(HttpStatusCode.NotFound);

        var answers = new (string Label, ApiAnswer Answer, ApiAnswer Baseline)[]
        {
            ("rival compares its own revision with ours", await world.Client.SendAsync(
                HttpMethod.Get, $"/v1/suite-revisions/{theirsId}/diff?against={mineId}", rival.As), rivalMissing),
            ("rival compares ours with its own", await world.Client.SendAsync(
                HttpMethod.Get, $"/v1/suite-revisions/{mineId}/diff?against={theirsId}", rival.As), rivalMissing),
            ("we compare ours with theirs", await world.Client.SendAsync(
                HttpMethod.Get, $"/v1/suite-revisions/{mineId}/diff?against={theirsId}", world.Admin), mineMissing),
            ("we compare theirs with ours", await world.Client.SendAsync(
                HttpMethod.Get, $"/v1/suite-revisions/{theirsId}/diff?against={mineId}", world.Admin), mineMissing),
        };

        foreach (var (label, answer, baseline) in answers)
        {
            answer.Status.ShouldBe(baseline.Status, label);
            answer.ErrorCode.ShouldBe(baseline.ErrorCode, label);
            answer.ErrorMessage.ShouldBe(baseline.ErrorMessage, label);

            // Nothing of the other tenant's content leaks into the refusal either.
            answer.Body.TryGetProperty("members_added", out _).ShouldBeFalse(label);
            answer.Body.GetProperty("error").GetProperty("details").GetArrayLength().ShouldBe(0, label);
        }
    }

    // ------------------------------------------------------------------ AC7 suite ETag

    [Fact]
    public async Task Suite_ETag_IsStrongQuotedAndStable()
    {
        await using var world = await StartAsync();
        var url = $"/v1/suites/{world.Suite}";

        var first = await RawAsync(world.Client, HttpMethod.Get, url, world.Admin);
        first.Status.ShouldBe(HttpStatusCode.OK, first.Text);
        first.Headers.ETag.ShouldNotBeNull();
        first.ETagIsWeak.ShouldBeFalse();
        var tag = first.ETag!;
        tag.ShouldStartWith("\"");
        tag.ShouldEndWith("\"");
        tag.ShouldBe("\"0-1\""); // head number - row version, before any revision

        // Two reads without a change: the same strong quoted tag and the same body.
        var again = await RawAsync(world.Client, HttpMethod.Get, url, world.Admin);
        again.ETag.ShouldBe(tag);
        again.ETagIsWeak.ShouldBeFalse();
        again.Text.ShouldBe(first.Text);

        async Task<Raw> Conditional(string ifNoneMatch) =>
            await RawAsync(world.Client, HttpMethod.Get, url, world.Admin, ifNoneMatch: ifNoneMatch);

        // A strong match, the wildcard and a list each answer 304 with an empty body and still carry the tag.
        foreach (var presented in new[] { tag, "*", $"\"999999-999999\", {tag}" })
        {
            var notModified = await Conditional(presented);
            notModified.Status.ShouldBe(HttpStatusCode.NotModified, presented);
            notModified.Text.ShouldBeEmpty(presented);
            notModified.ETag.ShouldBe(tag, presented);
            notModified.ETagIsWeak.ShouldBeFalse(presented);
        }

        // A mismatch is a full 200 with the tag, not a 304.
        var mismatch = await Conditional("\"999999-999999\"");
        mismatch.Status.ShouldBe(HttpStatusCode.OK);
        mismatch.Text.ShouldNotBeEmpty();
        mismatch.ETag.ShouldBe(tag);

        var created = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0]) } });
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        var moved = await RawAsync(world.Client, HttpMethod.Get, url, world.Admin);
        moved.Status.ShouldBe(HttpStatusCode.OK);
        moved.ETag.ShouldNotBe(tag);
        moved.ETagIsWeak.ShouldBeFalse();
        moved.Body.GetProperty("head_revision_no").GetInt32().ShouldBe(1);

        // The stale tag is no longer a match.
        (await Conditional(tag)).Status.ShouldBe(HttpStatusCode.OK);

        // A preview answers what a write would do without touching the suite row, so the tag stands.
        var preview = await RawAsync(
            world.Client,
            HttpMethod.Post,
            $"/v1/suites/{world.Suite}/revisions?dry_run=true",
            world.Admin,
            new { members = new[] { Member(world.Revisions[1], new { browser = "chromium" }, "p") } },
            NewKey());
        preview.Status.ShouldBe(HttpStatusCode.OK, preview.Text);
        preview.Body.GetProperty("status").GetString().ShouldBe("preview");

        var afterPreview = await RawAsync(world.Client, HttpMethod.Get, url, world.Admin);
        afterPreview.ETag.ShouldBe(moved.ETag);
        afterPreview.Text.ShouldBe(moved.Text);
        (await Conditional(moved.ETag!)).Status.ShouldBe(HttpStatusCode.NotModified);
    }

    // ------------------------------------------------------------------ T5.2 preview writes nothing

    [Fact]
    public async Task DryRun_WritesNoRowAnywhere()
    {
        await using var world = await StartAsync();
        var plain = new { members = new[] { Member(world.Revisions[0], new { browser = "chromium" }, "a") } };
        var rich = new
        {
            members = new[] { Member(world.Revisions[1], new { browser = "firefox" }, "b"), Member(world.Revisions[2], null, "c") },
            edges = new[] { new { parent = "b", child = "c" } },
            shard_strategy = new { kind = "count", count = 2, balance_by = "duration" },
            retry_policy = new { max_retries = 1, only_on_status = new[] { "failed" } },
        };

        var before = await CountsAsync(world);

        var plainPreview = await CreateRevisionAsync(world, plain, "?dry_run=true");
        plainPreview.Status.ShouldBe(HttpStatusCode.OK, plainPreview.ErrorMessage);
        (plainPreview.Text("status"), plainPreview.Body.GetProperty("created").GetBoolean(), plainPreview.Body.GetProperty("revision_no").GetInt32())
            .ShouldBe(("preview", false, 1));
        plainPreview.Body.GetProperty("suite_revision_id").ValueKind.ShouldBe(JsonValueKind.Null);

        // The same key twice: a preview claims no idempotency key, so the second call is a preview again, not a 409.
        var key = NewKey();
        var keyedFirst = await world.Client.SendAsync(HttpMethod.Post, $"/v1/suites/{world.Suite}/revisions?dry_run=true", world.Admin, rich, key);
        var keyedSecond = await world.Client.SendAsync(HttpMethod.Post, $"/v1/suites/{world.Suite}/revisions?dry_run=true", world.Admin, rich, key);
        keyedFirst.Status.ShouldBe(HttpStatusCode.OK, keyedFirst.ErrorMessage);
        keyedSecond.Status.ShouldBe(HttpStatusCode.OK, keyedSecond.ErrorMessage);
        keyedSecond.Text("revision_hash").ShouldBe(keyedFirst.Text("revision_hash"));

        // A refused preview writes nothing either.
        var refused = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0], new { password = "hunter2" }, "a") } }, "?dry_run=true");
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.ErrorCode.ShouldBe("variant_not_secret_free");

        var after = await CountsAsync(world);
        after.ShouldBe(before, "a preview writes no suite revision, member, variant, dependency, audit, outbox or idempotency row");

        // Positive control: the real creates move those counters, so "unchanged" above is a fact about the preview.
        var plainReal = await CreateRevisionAsync(world, plain);
        plainReal.Status.ShouldBe(HttpStatusCode.Created, plainReal.ErrorMessage);
        plainReal.Text("revision_hash").ShouldBe(plainPreview.Text("revision_hash"));

        var richReal = await CreateRevisionAsync(world, rich);
        richReal.Status.ShouldBe(HttpStatusCode.Created, richReal.ErrorMessage);
        richReal.Text("revision_hash").ShouldBe(keyedFirst.Text("revision_hash"));

        var created = await CountsAsync(world);
        created[0].ShouldBe(before[0] + 2, "suite_revision"); // one per real create
        created[1].ShouldBe(before[1] + 3, "suite_member"); // 1 + 2 authored members
        created[2].ShouldBe(before[2] + 3, "variant"); // chromium, firefox and the parameter-free variant
        created[3].ShouldBe(before[3] + 1, "case_dependency"); // the one authored edge
        created[4].ShouldBeGreaterThan(before[4], "audit_log"); // each successful create is audited
        created[5].ShouldBe(before[5], "outbox"); // the catalog route writes no outbox rows; the counter guards against one appearing
        created[6].ShouldBe(before[6] + 2, "idempotency_record"); // one record per keyed create
    }

    // ------------------------------------------------------------------ T5.2 preview standing

    [Fact]
    public async Task DryRun_NeedsTheSameStandingAsCreate()
    {
        await using var world = await StartAsync();
        var viewer = await AddMemberAsync(world, "viewer");
        var asViewer = TenancyApiFactory.Resolved(user: viewer, orgHeader: world.Org);

        var created = await CreateRevisionAsync(world, new { members = new[] { Member(world.Revisions[0]) } });
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var id = created.Id("suite_revision_id");

        // The viewer holds suite:read, so both reads succeed: the 403s below are about suite:write, not about access.
        (await world.Client.SendAsync(HttpMethod.Get, $"/v1/suites/{world.Suite}", asViewer)).Status.ShouldBe(HttpStatusCode.OK);
        var diff = await world.Client.SendAsync(HttpMethod.Get, $"/v1/suite-revisions/{id}/diff?against={id}", asViewer);
        diff.Status.ShouldBe(HttpStatusCode.OK, diff.ErrorMessage);

        var body = new { members = new[] { Member(world.Revisions[1]) } };
        var preview = await world.Client.SendAsync(HttpMethod.Post, $"/v1/suites/{world.Suite}/revisions?dry_run=true", asViewer, body, NewKey());
        var create = await world.Client.SendAsync(HttpMethod.Post, $"/v1/suites/{world.Suite}/revisions", asViewer, body, NewKey());

        preview.Status.ShouldBe(HttpStatusCode.Forbidden);
        preview.ErrorCode.ShouldBe("forbidden");
        create.Status.ShouldBe(HttpStatusCode.Forbidden);
        create.ErrorCode.ShouldBe("forbidden");
        preview.ErrorCode.ShouldBe(create.ErrorCode);
        preview.ErrorMessage.ShouldBe(create.ErrorMessage);

        // Neither attempt wrote anything.
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.suite_revision")).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.suite_member")).ShouldBe(1);

        // Positive control: the very same body is accepted for the writer, so the refusal is the standing alone.
        var accepted = await CreateRevisionAsync(world, body);
        accepted.Status.ShouldBe(HttpStatusCode.Created, accepted.ErrorMessage);
    }
}
