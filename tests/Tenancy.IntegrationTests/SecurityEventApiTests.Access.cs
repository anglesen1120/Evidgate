using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class SecurityEventApiTests
{
    /// <summary>A member holding no role gets 403 <c>forbidden</c>; the org's <c>tenant_admin</c> reads its page.</summary>
    [Fact]
    public async Task MemberWithoutPermission_Is403_AndTenantAdminIs200()
    {
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = _hosts.RealResolver(database);
        using var client = api.CreateClient();
        var founder = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, founder, Unique.Slug("acme"));
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
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = _hosts.RealResolver(database);
        using var client = api.CreateClient();
        var mineUser = Guid.CreateVersion7();
        var mine = await CreateOrgAsync(client, mineUser, Unique.Slug("acme"));
        var otherUser = Guid.CreateVersion7();
        var other = await CreateOrgAsync(client, otherUser, Unique.Slug("acme-2"));
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
        var database = await _fixture.SharedDatabaseAsync();
        var user = Guid.CreateVersion7();
        Guid org;
        Guid project;

        await using (var setup = _hosts.RealResolver(database))
        {
            using var setupClient = setup.CreateClient();
            org = await CreateOrgAsync(setupClient, user, Unique.Slug("acme"));
            project = await CreateProjectAsync(setupClient, user, org, "alpha");
        }

        var other = Guid.CreateVersion7();
        await using var api = _hosts.WithPrincipal(
            database,
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
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = _hosts.RealResolver(database);
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
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
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = _hosts.RealResolver(database);
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
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
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = _hosts.RealResolver(database);
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
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
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = _hosts.RealResolver(database);
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
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
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = _hosts.RealResolver(database);
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
        var database = await _fixture.SharedDatabaseAsync();
        await using var api = _hosts.RealResolver(database);
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
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
}
