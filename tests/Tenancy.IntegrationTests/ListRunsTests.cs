// Request bodies are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.2.2-T2 AC6 through the real host and a real PostgreSQL: <c>GET /v1/projects/{p}/runs</c> lists a project's runs
/// in <c>(created_at, id)</c> order, narrows them by the recorded identity (<c>ref_name</c>, <c>pr_number</c>,
/// <c>head_sha</c>, <c>trigger_event</c>, <c>is_fork</c>), pages with an opaque keyset cursor and answers the 00 §9.2
/// codes for a bad limit, a bad filter value, a cursor it did not issue and a project that is unknown or another
/// tenant's. The read is project-scoped and read-only: <c>run:read</c> is outside the service-role ceiling, so a
/// service credential is the permission stage's 403.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class ListRunsTests
{
    private const string ShaOne = "1111111111111111111111111111111111111111";

    private readonly PostgresTenancyFixture _fixture;

    public ListRunsTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>AC6: each filter selects exactly the run its identity field identifies.</summary>
    [Fact]
    public async Task ListRuns_FiltersEachField()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var branch = await CreateBranchAsync(world, "main", ShaOne);
        var pullRequest = await CreateRunAsync(
            world,
            $$$"""{"manifest_id":"{{{world.ManifestId}}}","ref":{"kind":"pull_request","name":"7/merge","pr_number":7,"trigger_event":"push"}}""");
        var fork = await CreateRunAsync(
            world,
            $$$"""{"manifest_id":"{{{world.ManifestId}}}","ref":{"kind":"branch","name":"feature","is_fork":true}}""");

        (await IdsAsync(world, "ref_name=main")).ShouldBe(new[] { branch }, ignoreOrder: true);
        (await IdsAsync(world, "pr_number=7")).ShouldBe(new[] { pullRequest }, ignoreOrder: true);
        (await IdsAsync(world, "head_sha=" + ShaOne)).ShouldBe(new[] { branch }, ignoreOrder: true);
        (await IdsAsync(world, "trigger_event=push")).ShouldBe(new[] { pullRequest }, ignoreOrder: true);
        (await IdsAsync(world, "is_fork=true")).ShouldBe(new[] { fork }, ignoreOrder: true);
        (await IdsAsync(world, "is_fork=false")).ShouldBe(new[] { branch, pullRequest }, ignoreOrder: true);
    }

    /// <summary>AC6: filters are AND-combined, so a pair that no row satisfies is an empty page.</summary>
    [Fact]
    public async Task ListRuns_FiltersCombined()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var branch = await CreateBranchAsync(world, "main", ShaOne);
        await CreateBranchAsync(world, "feature", ShaOne);

        (await IdsAsync(world, $"ref_name=main&head_sha={ShaOne}")).ShouldBe(new[] { branch }, ignoreOrder: true);
        (await IdsAsync(world, $"ref_name=nope&head_sha={ShaOne}")).ShouldBeEmpty();
    }

    /// <summary>AC6: a filter that matches nothing is a successful empty page, never a 404.</summary>
    [Fact]
    public async Task ListRuns_NoMatch_EmptyPage()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await CreateBranchAsync(world, "main", ShaOne);

        var answer = await ListAsync(world, "ref_name=does-not-exist");

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        Items(answer).ShouldBeEmpty();
        answer.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// AC6: rows that share <c>created_at</c> are ordered by <c>id</c>, and the keyset cursor walks that
    /// <c>(created_at, id)</c> order across pages without a repeat or a gap. The expected order comes from the
    /// database itself, so it is the uuid order PostgreSQL uses, not <c>Guid.CompareTo</c>'s.
    /// </summary>
    [Fact]
    public async Task ListRuns_Pagination_TiesOnCreatedAt_StableAcrossPages()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await CreateBranchAsync(world, "main", ShaOne);
        await CreateBranchAsync(world, "feature", ShaOne);
        await CreateBranchAsync(world, "release", ShaOne);
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set created_at = timestamptz '2026-01-02T03:04:05Z' where project_id = '{world.Project:D}'");

        var ordered = (await OrderedIdsAsync(world)).Select(Guid.Parse).ToArray();
        ordered.Length.ShouldBe(3);

        var pageOne = await ListAsync(world, "limit=2");
        pageOne.Status.ShouldBe(HttpStatusCode.OK, pageOne.ErrorMessage);
        OrderedIds(pageOne).ShouldBe(ordered[..2], ignoreOrder: false);
        var cursor = pageOne.Body.GetProperty("next_cursor").GetString();
        cursor.ShouldNotBeNullOrEmpty();

        var pageTwo = await ListAsync(world, $"limit=2&cursor={Uri.EscapeDataString(cursor!)}");
        pageTwo.Status.ShouldBe(HttpStatusCode.OK, pageTwo.ErrorMessage);
        OrderedIds(pageTwo).ShouldBe(ordered[2..], ignoreOrder: false);
        pageTwo.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// AC6: a row created between two pages sorts after the cursor (its <c>(created_at, id)</c> is greater), so it
    /// appears on the following page and no row already served is repeated.
    /// </summary>
    [Fact]
    public async Task ListRuns_InsertBetweenPages_NewRowSortsAfter()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var first = await CreateBranchAsync(world, "main", ShaOne);
        var second = await CreateBranchAsync(world, "feature", ShaOne);
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set created_at = timestamptz '2026-02-01T00:00:00Z' where id = '{first:D}'");
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set created_at = timestamptz '2026-02-02T00:00:00Z' where id = '{second:D}'");

        var pageOne = await ListAsync(world, "limit=1");
        OrderedIds(pageOne).ShouldBe(new[] { first }, ignoreOrder: false);
        var cursor = pageOne.Body.GetProperty("next_cursor").GetString()!;

        // A run created after the first page: its created_at is past the cursor, so it is neither skipped nor repeated.
        var late = await CreateBranchAsync(world, "late", ShaOne);
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set created_at = timestamptz '2026-02-03T00:00:00Z' where id = '{late:D}'");

        var pageTwo = await ListAsync(world, $"limit=10&cursor={Uri.EscapeDataString(cursor)}");
        pageTwo.Status.ShouldBe(HttpStatusCode.OK, pageTwo.ErrorMessage);
        OrderedIds(pageTwo).ShouldBe(new[] { second, late }, ignoreOrder: false);
        OrderedIds(pageOne).Concat(OrderedIds(pageTwo)).Distinct().Count().ShouldBe(3);
    }

    /// <summary>AC6: a cursor this endpoint did not issue is 422 <c>request_invalid</c> (<c>cursor</c>, <c>malformed</c>), never a guessed page.</summary>
    [Fact]
    public async Task ListRuns_TamperedCursor_422()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await CreateBranchAsync(world, "main", ShaOne);
        await CreateBranchAsync(world, "main", ShaOne);
        var issued = (await ListAsync(world, "limit=1")).Body.GetProperty("next_cursor").GetString()!;

        string[] cursors =
        [
            "not-a-cursor",
            "zzzz-11111111-1111-1111-1111-111111111111",
            issued[..(issued.IndexOf('-') + 1)] + "not-a-uuid",
        ];

        foreach (var cursor in cursors)
        {
            var answer = await ListAsync(world, $"limit=1&cursor={Uri.EscapeDataString(cursor)}");
            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, cursor);
            answer.ErrorCode.ShouldBe("request_invalid");
            Details(answer).ShouldContain(detail =>
                detail.GetProperty("path").GetString() == "cursor" && detail.GetProperty("reason").GetString() == "malformed");
        }
    }

    /// <summary>AC6, 00 §9.1: a limit outside 1..200 is 422 <c>request_invalid</c> (<c>limit</c>, <c>out_of_range</c>).</summary>
    [Fact]
    public async Task ListRuns_BadLimit_422()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await CreateBranchAsync(world, "main", ShaOne);

        foreach (var raw in new[] { "0", "201", "-1", "abc", "1.5" })
        {
            var answer = await ListAsync(world, $"limit={Uri.EscapeDataString(raw)}");
            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, raw);
            answer.ErrorCode.ShouldBe("request_invalid");
            Details(answer).ShouldContain(detail =>
                detail.GetProperty("path").GetString() == "limit" && detail.GetProperty("reason").GetString() == "out_of_range");
        }
    }

    /// <summary>
    /// AC6: a present filter whose value the run's identity shapes cannot hold — blank, a control character, a name
    /// over 255 code points, a <c>pr_number</c> below 1 / non-numeric / past <c>int</c>, an uppercase or short
    /// <c>head_sha</c>, an unknown <c>trigger_event</c>, a non-lowercase <c>is_fork</c> — is 422
    /// <c>request_invalid</c> with <c>details[].path</c> = the parameter and reason <c>invalid_value</c>.
    /// </summary>
    [Fact]
    public async Task ListRuns_BadFilterValues_422()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await CreateBranchAsync(world, "main", ShaOne);

        (string Parameter, string Value)[] cases =
        [
            ("ref_name", string.Empty),
            ("ref_name", "a\nb"),
            ("ref_name", new string('a', 256)),
            ("pr_number", "0"),
            ("pr_number", "-1"),
            ("pr_number", "abc"),
            ("pr_number", "9999999999"),
            ("head_sha", new string('A', 40)),
            ("head_sha", ShaOne[..39]),
            ("trigger_event", "deploy"),
            ("is_fork", "TRUE"),
            ("is_fork", "1"),
        ];

        foreach (var (parameter, value) in cases)
        {
            var answer = await ListAsync(world, $"{parameter}={Uri.EscapeDataString(value)}");
            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"{parameter}={value}");
            answer.ErrorCode.ShouldBe("request_invalid");
            Details(answer).ShouldContain(detail =>
                detail.GetProperty("path").GetString() == parameter && detail.GetProperty("reason").GetString() == "invalid_value");
        }

        // The values that are admissible still answer 200, so the loop above is not passing on a broken route.
        (await ListAsync(world, "ref_name=main")).Status.ShouldBe(HttpStatusCode.OK);
        (await ListAsync(world, "head_sha=" + ShaOne)).Status.ShouldBe(HttpStatusCode.OK);
        (await ListAsync(world, "is_fork=false")).Status.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>00 §5.2: an unknown project id is 404 <c>not_found</c> (a project that is not visible to this tenant).</summary>
    [Fact]
    public async Task ListRuns_UnknownProject_404()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await world.Client.SendAsync(
            HttpMethod.Get,
            $"/v1/projects/{Guid.CreateVersion7()}/runs",
            world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
    }

    /// <summary>00 §5.4: another tenant's project is invisible, and the answer is byte-for-byte the unknown-project one.</summary>
    [Fact]
    public async Task ListRuns_CrossTenant_404()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await CreateBranchAsync(world, "main", ShaOne);

        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");
        var foreign = await world.Client.SendAsync(
            HttpMethod.Get,
            $"/v1/projects/{world.Project}/runs",
            TenancyApiFactory.Resolved(user: otherUser, orgHeader: otherOrg));

        var unknown = await world.Client.SendAsync(
            HttpMethod.Get,
            $"/v1/projects/{Guid.CreateVersion7()}/runs",
            world.Admin);

        foreign.Status.ShouldBe(HttpStatusCode.NotFound);
        foreign.ErrorCode.ShouldBe(unknown.ErrorCode);
        foreign.ErrorMessage.ShouldBe(unknown.ErrorMessage, "an existence oracle is a leak even when only the code matches");
    }

    /// <summary>AC6/§5: <c>run:read</c> gates the route; a service credential can never hold it, so it is 403.</summary>
    [Fact]
    public async Task ListRuns_NoPermission_403()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await CreateBranchAsync(world, "main", ShaOne);
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, "other");

        var withoutPermission = await world.Client.SendAsync(
            HttpMethod.Get,
            $"/v1/projects/{world.Project}/runs",
            TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["run:create"]));
        withoutPermission.Status.ShouldBe(HttpStatusCode.Forbidden);

        var scopedToSibling = await world.Client.SendAsync(
            HttpMethod.Get,
            $"/v1/projects/{sibling}/runs",
            TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: sibling, servicePermissions: ["run:read"]));
        scopedToSibling.Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>AC6: every row carries the whole <c>ref</c> block and <c>unverified_ref</c>, so a client can label body-only identity.</summary>
    [Fact]
    public async Task ListRuns_RowsCarryRefAndUnverifiedRef()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var run = await CreateBranchAsync(world, "main", ShaOne);

        var answer = await ListAsync(world, "limit=1");
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var item = Items(answer).ShouldHaveSingleItem();

        item.GetProperty("run_id").GetGuid().ShouldBe(run);
        item.GetProperty("state").GetString().ShouldBe("queued");
        item.GetProperty("created_at").GetString().ShouldNotBeNullOrEmpty();
        item.GetProperty("unverified_ref").GetBoolean().ShouldBeTrue();

        var refBlock = item.GetProperty("ref");
        refBlock.GetProperty("kind").GetString().ShouldBe("branch");
        refBlock.GetProperty("name").GetString().ShouldBe("main");
        refBlock.GetProperty("head_sha").GetString().ShouldBe(ShaOne);
        refBlock.GetProperty("is_fork").GetBoolean().ShouldBeFalse();
        refBlock.GetProperty("pr_number").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>Creates one run of the world's manifest with a branch identity; returns its id.</summary>
    private static Task<Guid> CreateBranchAsync(RunCreationTests.World world, string name, string headSha) =>
        CreateRunAsync(
            world,
            $$$"""{"manifest_id":"{{{world.ManifestId}}}","ref":{"kind":"branch","name":"{{{name}}}","head_sha":"{{{headSha}}}"}}""");

    private static async Task<Guid> CreateRunAsync(RunCreationTests.World world, string body)
    {
        var created = await RunCreationTests.CreateRunAsync(world, body);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        return created.Id("run_id");
    }

    private static Task<ApiAnswer> ListAsync(RunCreationTests.World world, string query) =>
        world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/runs?{query}", world.Admin);

    private static async Task<Guid[]> IdsAsync(RunCreationTests.World world, string query)
    {
        var answer = await ListAsync(world, query);
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        return OrderedIds(answer);
    }

    private static Guid[] OrderedIds(ApiAnswer answer) =>
        [.. Items(answer).Select(item => item.GetProperty("run_id").GetGuid())];

    private static List<JsonElement> Items(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("items").EnumerateArray()];

    private static List<JsonElement> Details(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()];

    /// <summary>The project's run ids in the database's own <c>(created_at, id)</c> order.</summary>
    private async Task<string[]> OrderedIdsAsync(RunCreationTests.World world) =>
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select string_agg(id::text, ',' order by created_at, id) from public.run where project_id = '{world.Project:D}'")).Split(',');
}
