using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 11.5.1-T5.2 (AC3) through the real pipeline: <c>GET /v1/orgs/{o}/security-events</c> is a keyset page of
/// the tenant's own <c>security_event</c> rows, newest first on <c>(created_at, id)</c>, filtered by
/// <c>project_id</c>, <c>type</c>, <c>severity</c>, <c>from</c> (inclusive) and <c>to</c> (exclusive), read
/// with <c>security_event:read</c>. A caller who is not a member of the org named in <c>{o}</c> is refused at
/// tenant resolution (403, the same answer for an org that does not exist), and the handler's org-mismatch
/// branch answers 404 <c>not_found</c> when the resolved tenant differs from <c>{o}</c> — the branch a
/// host-published tenant reaches.
/// </summary>
/// <remarks>
/// Rows are seeded through the owner connection inside a tenant scope: the migrated table is under FORCE row
/// level security (migration 0053), so no seeded row exists that the tenant could not have written itself.
/// The expected page order is computed here from the seeded values with PostgreSQL's uuid ordering (the
/// canonical 32 hex digits, which is ordinal on <c>ToString("N")</c>), never with <see cref="Guid.CompareTo"/>
/// — .NET compares a Guid field-wise, so the two orders disagree for real ids.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class SecurityEventApiTests
{
    private readonly PostgresTenancyFixture _fixture;

    public SecurityEventApiTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>One seeded row; every field the API does not default is named here.</summary>
    private sealed record Seed(
        Guid Id,
        string Type,
        string Severity,
        DateTimeOffset CreatedAt,
        Guid? ProjectId = null,
        string Actor = """{"kind":"user","id":null}""",
        string Details = """{"count":1}""",
        string? RequestId = null);

    /// <summary>Five rows, three of them on the same instant: two pages of two, no repeat, no gap, ties stable.</summary>
    [Fact]
    public async Task Page_Keyset_TwoPages_NoDuplicateNoGap_AndTiesKeepOrder()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var caller = TenancyApiFactory.Resolved(user: user);

        var tie = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        Seed[] seeds =
        [
            new(Guid.CreateVersion7(), "auth_failure", "warning", tie.AddMinutes(2)),
            new(Guid.CreateVersion7(), "cross_tenant_probe", "critical", tie),
            new(Guid.CreateVersion7(), "secret_scanner_alert", "critical", tie),
            new(Guid.CreateVersion7(), "kill_switch_used", "info", tie),
            new(Guid.CreateVersion7(), "support_access", "info", tie.AddMinutes(-5)),
        ];
        await SeedAsync(database, org, seeds);

        var seen = new List<Guid>();
        var pages = 0;
        var lastPageHadCursor = true;
        string? cursor = null;

        do
        {
            var url = $"/v1/orgs/{org}/security-events?limit=2"
                + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            var page = await client.SendAsync(HttpMethod.Get, url, caller);
            page.Status.ShouldBe(HttpStatusCode.OK);
            seen.AddRange(page.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
            cursor = page.Body.GetProperty("next_cursor").GetString();
            lastPageHadCursor = cursor is not null;
            pages++;
        }
        while (cursor is not null);

        pages.ShouldBe(3);
        lastPageHadCursor.ShouldBeFalse("the last page of the walk must answer next_cursor = null");

        seen.Count.ShouldBe(seeds.Length);
        seen.Distinct().Count().ShouldBe(seeds.Length);
        seen.ShouldBe(ExpectedOrder(seeds), "order is (created_at, id) descending, ties included");

        // The tie is real: three rows share one instant, so the id is what orders them.
        seeds.Select(seed => seed.CreatedAt).Distinct().Count().ShouldBeLessThan(seeds.Length);
    }

    /// <summary>Each filter alone narrows the page, and the filters compose.</summary>
    [Fact]
    public async Task Filters_NarrowAlone_AndCombined()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var caller = TenancyApiFactory.Resolved(user: user);
        var projectA = await CreateProjectAsync(client, user, org, "alpha");
        var projectB = await CreateProjectAsync(client, user, org, "beta");

        var early = new DateTimeOffset(2026, 4, 1, 8, 0, 0, TimeSpan.Zero);
        var late = early.AddHours(2);
        Seed[] seeds =
        [
            new(Guid.CreateVersion7(), "auth_failure", "warning", early, projectA),
            new(Guid.CreateVersion7(), "kill_switch_used", "critical", early.AddMinutes(30), projectA),
            new(Guid.CreateVersion7(), "auth_failure", "info", late, projectB),
            new(Guid.CreateVersion7(), "support_access", "info", late.AddMinutes(5)),
        ];
        await SeedAsync(database, org, seeds);

        async Task<IReadOnlyList<Guid>> IdsAsync(string query)
        {
            var answer = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/security-events?{query}", caller);
            answer.Status.ShouldBe(HttpStatusCode.OK);
            return [.. answer.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];
        }

        (await IdsAsync($"project_id={projectA}")).ShouldBe(ExpectedOrder(seeds.Where(s => s.ProjectId == projectA)));
        (await IdsAsync("type=auth_failure")).ShouldBe(ExpectedOrder(seeds.Where(s => s.Type == "auth_failure")));
        (await IdsAsync("severity=critical")).ShouldBe(ExpectedOrder(seeds.Where(s => s.Severity == "critical")));
        (await IdsAsync("severity=info")).ShouldBe(ExpectedOrder(seeds.Where(s => s.Severity == "info")));
        (await IdsAsync("type=cross_tenant_probe")).ShouldBeEmpty();

        // from/to compose with the exact filters: only the early project-A auth failure fits.
        var from = early.ToString("O", CultureInfo.InvariantCulture);
        var to = late.ToString("O", CultureInfo.InvariantCulture);
        (await IdsAsync($"type=auth_failure&from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}"))
            .ShouldBe(ExpectedOrder(seeds.Where(s => s.Type == "auth_failure" && s.CreatedAt < late)));
        (await IdsAsync($"project_id={projectA}&severity=critical"))
            .ShouldBe(ExpectedOrder(seeds.Where(s => s.ProjectId == projectA && s.Severity == "critical")));
        (await IdsAsync($"project_id={projectA}&type=auth_failure&severity=info")).ShouldBeEmpty();
    }

    /// <summary><c>from</c> includes its instant; <c>to</c> excludes it (00 §9.1 half-open window).</summary>
    [Fact]
    public async Task FromIsInclusive_AndToIsExclusive()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var caller = TenancyApiFactory.Resolved(user: user);

        var first = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);
        var second = first.AddSeconds(1);
        Seed[] seeds =
        [
            new(Guid.CreateVersion7(), "auth_failure", "info", first),
            new(Guid.CreateVersion7(), "auth_failure", "info", second),
        ];
        await SeedAsync(database, org, seeds);

        async Task<IReadOnlyList<Guid>> IdsAsync(string query)
        {
            var answer = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/security-events?{query}", caller);
            answer.Status.ShouldBe(HttpStatusCode.OK);
            return [.. answer.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];
        }

        var atFirst = Uri.EscapeDataString(first.ToString("O", CultureInfo.InvariantCulture));

        (await IdsAsync($"from={atFirst}")).ShouldBe(ExpectedOrder(seeds), "from is inclusive: the row at the instant is on the page");
        (await IdsAsync($"to={atFirst}")).ShouldBeEmpty("to is exclusive: the row at the instant is off the page");

        var atSecond = Uri.EscapeDataString(second.ToString("O", CultureInfo.InvariantCulture));
        (await IdsAsync($"from={atFirst}&to={atSecond}")).ShouldBe([seeds[0].Id]);
        (await IdsAsync($"from={atSecond}")).ShouldBe([seeds[1].Id]);
        (await IdsAsync($"to={atSecond}")).ShouldBe([seeds[0].Id]);
    }

    /// <summary>00 §9.1: no <c>limit</c> means 50, 200 is accepted, and 0 or 201 is refused at the edge.</summary>
    [Fact]
    public async Task Limit_DefaultsTo50_Accepts200_AndRefusesZeroAnd201()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var caller = TenancyApiFactory.Resolved(user: user);

        var baseAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await SeedAsync(
            database,
            org,
            [.. Enumerable.Range(0, 60).Select(index =>
                new Seed(Guid.CreateVersion7(), "auth_failure", "info", baseAt.AddMinutes(index)))]);

        var defaulted = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/security-events", caller);
        defaulted.Status.ShouldBe(HttpStatusCode.OK);
        defaulted.Body.GetProperty("items").GetArrayLength().ShouldBe(50);
        defaulted.Body.GetProperty("next_cursor").GetString().ShouldNotBeNullOrEmpty();

        var largest = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/security-events?limit=200", caller);
        largest.Status.ShouldBe(HttpStatusCode.OK);
        largest.Body.GetProperty("items").GetArrayLength().ShouldBe(60);
        largest.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);

        foreach (var bad in new[] { "0", "201" })
        {
            var refused = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/security-events?limit={bad}", caller);
            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
            refused.ErrorCode.ShouldBe("request_invalid");
            DetailPaths(refused).ShouldContain("limit");
        }
    }

    /// <summary>
    /// A present but invalid filter, cursor or limit is 422 <c>request_invalid</c> with <c>details[].path</c>
    /// naming the parameter (00 §9.2). The plan's AC3 line says 400; the edge-input rule every other read
    /// on this host follows — and the contract's own description — is 422, so that is what is pinned here.
    /// </summary>
    /// <param name="query">The offending query string.</param>
    /// <param name="path">The <c>details[].path</c> the answer must name.</param>
    [Theory]
    [InlineData("project_id=not-a-uuid", "project_id")]
    [InlineData("type=injection_attempt", "type")]
    [InlineData("severity=debug", "severity")]
    [InlineData("from=not-a-date", "from")]
    [InlineData("to=not-a-date", "to")]
    [InlineData("limit=abc", "limit")]
    [InlineData("cursor=garbage", "cursor")]
    public async Task BadInput_Is422RequestInvalid_WithDetailsPath(string query, string path)
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var caller = TenancyApiFactory.Resolved(user: user);

        var answer = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/security-events?{query}", caller);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        DetailPaths(answer).ShouldContain(path);
    }

    /// <summary>
    /// The cursor is a position in <c>(created_at, id)</c> only, so another org's cursor is not a foreign row
    /// oracle: fed to this org it pages this org's own rows below that position, and no other org's id leaks.
    /// </summary>
    [Fact]
    public async Task CursorMintedByAnotherOrg_ReturnsNoForeignRows()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var mineUser = Guid.CreateVersion7();
        var mine = await CreateOrgAsync(client, mineUser, "acme");
        var otherUser = Guid.CreateVersion7();
        var other = await CreateOrgAsync(client, otherUser, "acme-2");

        var mineAt = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        Seed[] mineSeeds =
        [
            new(Guid.CreateVersion7(), "auth_failure", "info", mineAt),
            new(Guid.CreateVersion7(), "auth_failure", "info", mineAt.AddMinutes(1)),
        ];
        Seed[] otherSeeds =
        [
            new(Guid.CreateVersion7(), "cross_tenant_probe", "critical", mineAt.AddMinutes(-10)),
            new(Guid.CreateVersion7(), "cross_tenant_probe", "critical", mineAt.AddMinutes(5)),
        ];
        await SeedAsync(database, mine, mineSeeds);
        await SeedAsync(database, other, otherSeeds);

        var mineCaller = TenancyApiFactory.Resolved(user: mineUser);
        var otherCaller = TenancyApiFactory.Resolved(user: otherUser);

        var firstMinePage = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{mine}/security-events?limit=1", mineCaller);
        firstMinePage.Status.ShouldBe(HttpStatusCode.OK);
        firstMinePage.Body.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldBe(mineSeeds[1].Id);
        var mineCursor = firstMinePage.Body.GetProperty("next_cursor").GetString();
        mineCursor.ShouldNotBeNullOrEmpty();

        // Its own cursor: the older row of the org, nothing else.
        (await IdsAsync(client, mine, mineCursor!, mineCaller)).ShouldBe([mineSeeds[0].Id]);

        var firstOtherPage = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{other}/security-events?limit=1", otherCaller);
        firstOtherPage.Status.ShouldBe(HttpStatusCode.OK);
        firstOtherPage.Body.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldBe(otherSeeds[1].Id);
        var otherCursor = firstOtherPage.Body.GetProperty("next_cursor").GetString();
        otherCursor.ShouldNotBeNullOrEmpty();

        // The other org's cursor (minted at mineAt + 5 min) fed to this org: accepted, and it pages THIS org's
        // two rows below that position — never the id the cursor was minted from, never any other org's row.
        var withForeignCursor = await IdsAsync(client, mine, otherCursor!, mineCaller);
        withForeignCursor.ShouldBe(ExpectedOrder(mineSeeds));
        withForeignCursor.ShouldNotContain(id => otherSeeds.Any(seed => seed.Id == id));

        // And the reverse: this org's cursor (minted at mineAt + 1 min) on the other org's page yields only that
        // org's older row, never a row of the org the cursor came from.
        var otherWithForeignCursor = await IdsAsync(client, other, mineCursor!, otherCaller);
        otherWithForeignCursor.ShouldBe([otherSeeds[0].Id]);
        otherWithForeignCursor.ShouldNotContain(id => mineSeeds.Any(seed => seed.Id == id));
    }

    /// <summary>The ids of one whole page read at <paramref name="cursor"/>, asserting the 200.</summary>
    private static async Task<List<Guid>> IdsAsync(HttpClient client, Guid org, string cursor, Action<HttpRequestHeaders> caller)
    {
        var answer = await client.SendAsync(
            HttpMethod.Get,
            $"/v1/orgs/{org}/security-events?limit=10&cursor={Uri.EscapeDataString(cursor)}",
            caller);
        answer.Status.ShouldBe(HttpStatusCode.OK);
        return [.. answer.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];
    }

    /// <summary>A member holding no role gets 403 <c>forbidden</c>; the org's <c>tenant_admin</c> reads its page.</summary>
    [Fact]
    public async Task MemberWithoutPermission_Is403_AndTenantAdminIs200()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var founder = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, founder, "acme");
        await SeedAsync(database, org, [new Seed(Guid.CreateVersion7(), "auth_failure", "info", DateTimeOffset.UtcNow)]);

        // An active member with no role holds no permission.
        var bare = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public."user" (id, email) values ('{bare}', 'bare@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status) values (gen_random_uuid(), '{org}', '{bare}', 'bare@example.test', 'active')
            """);

        var denied = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/security-events", TenancyApiFactory.Resolved(user: bare));
        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        denied.ErrorCode.ShouldBe("forbidden");

        var admin = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/security-events", TenancyApiFactory.Resolved(user: founder));
        admin.Status.ShouldBe(HttpStatusCode.OK);
        admin.Body.GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    /// <summary>
    /// Another org's id in <c>{o}</c>: a member of this org only is refused at tenant resolution (403, the
    /// same answer for an org that does not exist — TenantResolver.cs:66-70), never a row of that org.
    /// </summary>
    [Fact]
    public async Task ForeignOrgInPath_IsRefused_AndNeverLeaksItsRows()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var mineUser = Guid.CreateVersion7();
        var mine = await CreateOrgAsync(client, mineUser, "acme");
        var otherUser = Guid.CreateVersion7();
        var other = await CreateOrgAsync(client, otherUser, "acme-2");
        var otherSeed = new Seed(Guid.CreateVersion7(), "cross_tenant_probe", "critical", DateTimeOffset.UtcNow);
        await SeedAsync(database, other, [otherSeed]);

        var answer = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{other}/security-events", TenancyApiFactory.Resolved(user: mineUser));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("forbidden");
        answer.Body.ToString().ShouldNotContain(otherSeed.Id.ToString("D", CultureInfo.InvariantCulture));

        // The random-id half: the same status and code, so no org-existence oracle.
        var random = await client.SendAsync(
            HttpMethod.Get, $"/v1/orgs/{Guid.CreateVersion7()}/security-events", TenancyApiFactory.Resolved(user: mineUser));
        random.Status.ShouldBe(answer.Status);
        random.ErrorCode.ShouldBe(answer.ErrorCode);
        random.ErrorMessage.ShouldBe(answer.ErrorMessage);
    }

    /// <summary>
    /// A path <c>{o}</c> that is not the request tenant is the handler's 404: the tenant is published (here by the
    /// test host, as a member of org A) and the query's own org id disagrees, so no statement is sent.
    /// </summary>
    [Fact]
    public async Task PathOrgThatIsNotTheRequestTenant_Is404()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var user = Guid.CreateVersion7();
        Guid org;
        Guid project;

        await using (var setup = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database)))
        {
            using var setupClient = setup.CreateClient();
            org = await CreateOrgAsync(setupClient, user, "acme");
            project = await CreateProjectAsync(setupClient, user, org, "alpha");
        }

        var other = Guid.CreateVersion7();
        await using var api = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(database),
            org,
            project,
            RequestPrincipal.Member(user, "acme@example.test"));
        using var client = api.CreateClient();

        var answer = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{other}/security-events", _ => { });

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
    }

    /// <summary>An unknown or foreign <c>project_id</c> filter is an empty page, not 404 (11.5.1 §5).</summary>
    [Fact]
    public async Task UnknownProjectFilter_IsEmptyPage()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var caller = TenancyApiFactory.Resolved(user: user);
        var project = await CreateProjectAsync(client, user, org, "alpha");
        await SeedAsync(database, org, [new Seed(Guid.CreateVersion7(), "auth_failure", "info", DateTimeOffset.UtcNow, project)]);

        var answer = await client.SendAsync(
            HttpMethod.Get, $"/v1/orgs/{org}/security-events?project_id={Guid.CreateVersion7()}", caller);

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("items").GetArrayLength().ShouldBe(0);
        answer.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>The page carries exactly the eight wire fields of the item and the two of the page.</summary>
    [Fact]
    public async Task Response_CarriesOnlyTheEightItemFields()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var caller = TenancyApiFactory.Resolved(user: user);
        var project = await CreateProjectAsync(client, user, org, "alpha");
        var row = new Seed(
            Guid.CreateVersion7(),
            "support_access",
            "critical",
            DateTimeOffset.UtcNow,
            project,
            """{"kind":"platform_operator","id":null}""",
            """{"grant_id":"6f2a","reason_hash":"ab12"}""",
            "req_01J9");
        await SeedAsync(database, org, [row]);

        var answer = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/security-events", caller);
        answer.Status.ShouldBe(HttpStatusCode.OK);

        answer.Body.EnumerateObject().Select(member => member.Name)
            .ShouldBe(["items", "next_cursor"], ignoreOrder: true);

        var item = answer.Body.GetProperty("items")[0];
        item.EnumerateObject().Select(member => member.Name).ShouldBe(
            ["id", "project_id", "type", "severity", "actor", "details", "request_id", "created_at"],
            ignoreOrder: true);

        item.GetProperty("id").GetGuid().ShouldBe(row.Id);
        item.GetProperty("project_id").GetGuid().ShouldBe(project);
        item.GetProperty("type").GetString().ShouldBe("support_access");
        item.GetProperty("severity").GetString().ShouldBe("critical");
        item.GetProperty("request_id").GetString().ShouldBe("req_01J9");
        item.GetProperty("created_at").GetDateTimeOffset().ShouldBe(row.CreatedAt, TimeSpan.FromMilliseconds(1));
        item.GetProperty("actor").GetProperty("kind").GetString().ShouldBe("platform_operator");
        item.GetProperty("details").GetProperty("reason_hash").GetString().ShouldBe("ab12");

        // The internals never leave the tenant boundary.
        item.TryGetProperty("org_id", out _).ShouldBeFalse();
        item.TryGetProperty("updated_at", out _).ShouldBeFalse();
        item.TryGetProperty("version", out _).ShouldBeFalse();
    }

    /// <summary>
    /// 00 §9.1's half-open window with a <c>to</c> before its <c>from</c>: no instant is on both sides, so the page
    /// is empty — and the answer is still 200 with a null cursor. An inverted window is a filter, not bad input.
    /// </summary>
    [Fact]
    public async Task ToBeforeFrom_IsAnEmptyPage()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var caller = TenancyApiFactory.Resolved(user: user);

        var at = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
        await SeedAsync(
            database,
            org,
            [
                new Seed(Guid.CreateVersion7(), "auth_failure", "info", at),
                new Seed(Guid.CreateVersion7(), "auth_failure", "info", at.AddHours(1)),
            ]);

        var from = Uri.EscapeDataString(at.AddHours(2).ToString("O", CultureInfo.InvariantCulture));
        var to = Uri.EscapeDataString(at.ToString("O", CultureInfo.InvariantCulture));

        var answer = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/security-events?from={from}&to={to}", caller);

        answer.Status.ShouldBe(HttpStatusCode.OK);
        answer.Body.GetProperty("items").GetArrayLength().ShouldBe(0, "to < from names no instant at all");
        answer.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// A page whose size is exactly <c>limit</c> is the last page: <c>next_cursor</c> is null, because the handler
    /// fetches <c>limit + 1</c> rows to decide. A page one row short of the limit is not the last page.
    /// </summary>
    [Fact]
    public async Task PageExactlyEqualToLimit_AnswersNullCursor()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var caller = TenancyApiFactory.Resolved(user: user);

        var at = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        Seed[] seeds =
        [
            new(Guid.CreateVersion7(), "auth_failure", "info", at),
            new(Guid.CreateVersion7(), "auth_failure", "info", at.AddMinutes(1)),
            new(Guid.CreateVersion7(), "auth_failure", "info", at.AddMinutes(2)),
        ];
        await SeedAsync(database, org, seeds);

        var exact = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/security-events?limit={seeds.Length}", caller);
        exact.Status.ShouldBe(HttpStatusCode.OK);
        exact.Body.GetProperty("items").GetArrayLength().ShouldBe(seeds.Length);
        exact.Body.GetProperty("next_cursor").ValueKind.ShouldBe(
            JsonValueKind.Null,
            "a page that consumed the whole result set is the last page, so it must not mint a cursor");

        var oneShort = await client.SendAsync(HttpMethod.Get, $"/v1/orgs/{org}/security-events?limit={seeds.Length - 1}", caller);
        oneShort.Status.ShouldBe(HttpStatusCode.OK);
        oneShort.Body.GetProperty("items").GetArrayLength().ShouldBe(seeds.Length - 1);
        oneShort.Body.GetProperty("next_cursor").GetString().ShouldNotBeNullOrEmpty(
            "one row is left over, so the page must carry the cursor that reaches it");
    }

    /// <summary>
    /// A <c>{o}</c> that is not a uuid never reaches the tenant resolver or the handler: minimal API binding fails
    /// and the framework answers its own 400 — the same answer <c>GET /v1/orgs/not-a-guid</c> gives
    /// (TenantResolutionTests.MalformedOrgRoute_IsLeftToTheFramework400), and never a page.
    /// </summary>
    [Fact]
    public async Task NonUuidOrgInPath_IsTheFramework400()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();

        var answer = await client.SendAsync(
            HttpMethod.Get, "/v1/orgs/not-a-guid/security-events", TenancyApiFactory.Resolved(user: Guid.CreateVersion7()));

        answer.Status.ShouldBe(HttpStatusCode.BadRequest);
        answer.Body.TryGetProperty("items", out _).ShouldBeFalse("the framework's binding failure answers no page");
        answer.Body.TryGetProperty("next_cursor", out _).ShouldBeFalse();
    }

    /// <summary>
    /// The cursor is a position in <c>(created_at, id)</c>, not a stored query: reusing one under a DIFFERENT filter
    /// is accepted (200, no error) and pages only rows of the new filter that sit below that position — a row the
    /// new filter excludes never appears, and a row above the position stays off the page.
    /// </summary>
    [Fact]
    public async Task CursorReusedAfterChangingAFilter_AnswersNoErrorAndNoForeignRows()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var caller = TenancyApiFactory.Resolved(user: user);

        var baseAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        Seed[] authFailures =
        [
            new(Guid.CreateVersion7(), "auth_failure", "warning", baseAt.AddMinutes(30)),
            new(Guid.CreateVersion7(), "auth_failure", "warning", baseAt.AddMinutes(20)),
            new(Guid.CreateVersion7(), "auth_failure", "warning", baseAt.AddMinutes(10)),
        ];
        Seed[] killSwitches =
        [
            new(Guid.CreateVersion7(), "kill_switch_used", "critical", baseAt.AddMinutes(40)),
            new(Guid.CreateVersion7(), "kill_switch_used", "critical", baseAt.AddMinutes(5)),
        ];
        await SeedAsync(database, org, [.. authFailures, .. killSwitches]);

        // Mint the cursor under `type=auth_failure`: its position is the row of minute 20 (page [30, 20]).
        var minted = await client.SendAsync(
            HttpMethod.Get, $"/v1/orgs/{org}/security-events?type=auth_failure&limit=2", caller);
        minted.Status.ShouldBe(HttpStatusCode.OK);
        minted.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())
            .ShouldBe([authFailures[0].Id, authFailures[1].Id]);
        var cursor = minted.Body.GetProperty("next_cursor").GetString();
        cursor.ShouldNotBeNullOrEmpty();

        // The same position, a different filter: accepted, and only the kill switch below minute 20 (minute 5).
        var reused = await client.SendAsync(
            HttpMethod.Get,
            $"/v1/orgs/{org}/security-events?type=kill_switch_used&limit=10&cursor={Uri.EscapeDataString(cursor!)}",
            caller);
        reused.Status.ShouldBe(HttpStatusCode.OK);
        reused.Body.TryGetProperty("error", out _).ShouldBeFalse("reusing a cursor under another filter is not an error");

        var reusedIds = reused.Body.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).ToList();
        reusedIds.ShouldBe([killSwitches[1].Id], "only the new filter's row below the cursor position belongs on the page");
        reusedIds.ShouldNotContain(id => authFailures.Any(seed => seed.Id == id));
        reusedIds.ShouldNotContain(killSwitches[0].Id, "the minute-40 row sits above the cursor position");
    }

    /// <summary>
    /// The seeded page order: <c>created_at</c> descending, then the id descending in PostgreSQL's uuid order —
    /// the canonical hex digits, which is ordinal on <c>"N"</c> (and not <see cref="Guid.CompareTo"/>'s order).
    /// </summary>
    private static List<Guid> ExpectedOrder(IEnumerable<Seed> seeds) =>
        [.. seeds
            .OrderByDescending(seed => seed.CreatedAt)
            .ThenByDescending(seed => seed.Id.ToString("N"), StringComparer.Ordinal)
            .Select(seed => seed.Id)];

    private static IReadOnlyList<string> DetailPaths(ApiAnswer answer) =>
        answer.Body.TryGetProperty("error", out var error) && error.TryGetProperty("details", out var details)
            && details.ValueKind == JsonValueKind.Array
                ? [.. details.EnumerateArray().Select(detail => detail.GetProperty("path").GetString()!)]
                : [];

    private async Task SeedAsync(string database, Guid org, IEnumerable<Seed> seeds)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database), org);

        foreach (var seed in seeds)
        {
            await scope.ExecuteAsync(
                """
                insert into public.security_event
                    (id, org_id, project_id, type, severity, actor, details, request_id, created_at)
                values
                    (@id, @org_id, @project_id, @type, @severity, @actor::jsonb, @details::jsonb, @request_id, @created_at)
                """,
                ("id", seed.Id),
                ("org_id", org),
                ("project_id", (object?)seed.ProjectId ?? DBNull.Value),
                ("type", seed.Type),
                ("severity", seed.Severity),
                ("actor", seed.Actor),
                ("details", seed.Details),
                ("request_id", (object?)seed.RequestId ?? DBNull.Value),
                ("created_at", seed.CreatedAt));
        }

        await scope.CommitAsync();
    }
}
