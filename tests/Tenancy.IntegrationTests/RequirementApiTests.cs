using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The requirement CRUD, history and restore routes through the real host and PostgreSQL: the happy paths, the
/// refusals that must write nothing, the version-history rules and the audit trail.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RequirementApiTests(PostgresTenancyFixture fixture)
{
    [Fact]
    public async Task Requirements_CreateReadListPatchDelete_RoundTrip()
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var caller = As(world);
        var created = await CreateAsync(world, caller, "first");
        var id = created.Body.GetProperty("requirement_id").GetGuid();
        created.Body.GetProperty("version_no").GetInt32().ShouldBe(1);

        var detail = await world.Client.SendAsync(HttpMethod.Get, Path(world.Project, id), caller);
        detail.Status.ShouldBe(HttpStatusCode.OK, detail.ErrorMessage);
        detail.Text("title").ShouldBe("Pay by card");

        var list = await world.Client.SendAsync(HttpMethod.Get, Path(world.Project) + "?limit=1", caller);
        list.Status.ShouldBe(HttpStatusCode.OK, list.ErrorMessage);
        list.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("requirement_id").GetGuid())
            .ShouldContain(id);

        var edited = await world.Client.SendAsync(
            HttpMethod.Patch, Path(world.Project, id), caller, new { title = "Pay by wallet" }, NewKey(), created.ETag);
        edited.Status.ShouldBe(HttpStatusCode.OK, edited.ErrorMessage);
        edited.Body.GetProperty("version_no").GetInt32().ShouldBe(2);
        (await Scalar<string>(world, $"select body from public.requirement_version where requirement_id = '{id}' and version_no = 2"))
            .ShouldBe("first");

        var deleted = await world.Client.SendAsync(
            HttpMethod.Delete, Path(world.Project, id), caller, null, NewKey(), edited.ETag);
        deleted.Status.ShouldBe(HttpStatusCode.NoContent, deleted.ErrorMessage);
        var after = await world.Client.SendAsync(HttpMethod.Get, Path(world.Project, id), caller);
        after.Status.ShouldBe(HttpStatusCode.OK);
        after.Body.GetProperty("deleted_at").ValueKind.ShouldNotBe(JsonValueKind.Null);
        (await world.Client.SendAsync(HttpMethod.Get, Path(world.Project), caller)).Body.GetProperty("items")
            .EnumerateArray().Select(item => item.GetProperty("requirement_id").GetGuid()).ShouldNotContain(id);
        (await world.Client.SendAsync(HttpMethod.Patch, Path(world.Project, id), caller, new { title = "x" }, NewKey(), edited.ETag))
            .Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Restore_FirstVersion_AppendsWithoutRewritingHistory()
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var caller = As(world);
        var created = await CreateAsync(world, caller, "first");
        var id = created.Body.GetProperty("requirement_id").GetGuid();
        var first = created.Body.GetProperty("version_id").GetGuid();
        var path = Path(world.Project, id);
        var edited = await world.Client.SendAsync(
            HttpMethod.Patch, path, caller, new { body = "second" }, NewKey(), created.ETag);
        edited.Status.ShouldBe(HttpStatusCode.OK, edited.ErrorMessage);
        var before = await Rows(world, id, "true");

        var stale = await world.Client.SendAsync(
            HttpMethod.Post, path + "/restore", caller, new { version_id = first, reason = "undo" }, NewKey(), created.ETag);
        stale.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await Rows(world, id, "true")).ShouldBe(before);

        var restored = await world.Client.SendAsync(
            HttpMethod.Post, path + "/restore", caller, new { version_id = first, reason = "undo" }, NewKey(), edited.ETag);
        restored.Status.ShouldBe(HttpStatusCode.OK, restored.ErrorMessage);
        restored.Body.GetProperty("version_no").GetInt32().ShouldBe(3);
        (await Rows(world, id, "version_no < 3")).ShouldBe(before);
        (await Scalar<string>(world, $"select body from public.requirement_version where requirement_id = '{id}' and version_no = 3"))
            .ShouldBe("first");
        (await Scalar<bool>(world,
            $"select head_version_no = (select max(version_no) from public.requirement_version where requirement_id = r.id) from public.requirement r where id = '{id}'"))
            .ShouldBeTrue();

        var history = await world.Client.SendAsync(HttpMethod.Get, path + "/versions?limit=2", caller);
        history.Status.ShouldBe(HttpStatusCode.OK, history.ErrorMessage);
        history.Body.GetProperty("items").GetArrayLength().ShouldBe(2);
        history.Body.GetProperty("next_cursor").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Restore_ForeignVersionOrDeletedRequirement_Is404AndWritesNothing()
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var caller = As(world);
        var mine = await CreateAsync(world, caller, "mine");
        var other = await CreateAsync(world, caller, "other");
        var mineId = mine.Body.GetProperty("requirement_id").GetGuid();

        var foreign = await world.Client.SendAsync(
            HttpMethod.Post, Path(world.Project, mineId) + "/restore", caller,
            new { version_id = other.Body.GetProperty("version_id").GetGuid() }, NewKey(), mine.ETag);
        foreign.Status.ShouldBe(HttpStatusCode.NotFound);
        (await Scalar<long>(world, $"select count(*) from public.requirement_version where requirement_id = '{mineId}'")).ShouldBe(1);

        var deleted = await world.Client.SendAsync(HttpMethod.Delete, Path(world.Project, mineId), caller, null, NewKey(), mine.ETag);
        deleted.Status.ShouldBe(HttpStatusCode.NoContent);
        (await world.Client.SendAsync(
            HttpMethod.Post, Path(world.Project, mineId) + "/restore", caller,
            new { version_id = mine.Body.GetProperty("version_id").GetGuid() }, NewKey(), mine.ETag))
            .Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Patch_ConcurrentSameETag_OnlyOneAppends()
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var caller = As(world);
        var created = await CreateAsync(world, caller, "first");
        var id = created.Body.GetProperty("requirement_id").GetGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => world.Client.SendAsync(
            HttpMethod.Patch, Path(world.Project, id), caller, new { body = "edit " + index }, NewKey(), created.ETag)));

        results.Count(result => result.Status == HttpStatusCode.OK).ShouldBe(1);
        results.Count(result => result.Status == HttpStatusCode.PreconditionFailed).ShouldBe(1);
        (await Scalar<long>(world, $"select count(*) from public.requirement_version where requirement_id = '{id}'")).ShouldBe(2);
    }

    [Fact]
    public async Task Patch_StaleOrMissingIfMatch_WritesNothing()
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var caller = As(world);
        var created = await CreateAsync(world, caller, "first");
        var id = created.Body.GetProperty("requirement_id").GetGuid();

        var missing = await world.Client.SendAsync(HttpMethod.Patch, Path(world.Project, id), caller, new { title = "x" }, NewKey());
        missing.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var stale = await world.Client.SendAsync(
            HttpMethod.Patch, Path(world.Project, id), caller, new { title = "x" }, NewKey(), "\"99\"");
        stale.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        var empty = await world.Client.SendAsync(
            HttpMethod.Patch, Path(world.Project, id), caller, new { }, NewKey(), created.ETag);
        empty.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);

        (await Scalar<string>(world, $"select title from public.requirement where id = '{id}'")).ShouldBe("Pay by card");
        (await Scalar<long>(world, $"select count(*) from public.requirement_version where requirement_id = '{id}'")).ShouldBe(1);
        (await world.Client.SendAsync(HttpMethod.Patch, Path(world.Project, id), caller, new { title = "x" }, NewKey(), created.ETag))
            .Status.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public async Task Create_TitleBoundary_AcceptsLimitAndRefusesOver(int length, bool accepted)
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var answer = await world.Client.SendAsync(
            HttpMethod.Post, Path(world.Project), As(world), new { kind = "epic", title = new string('t', length) }, NewKey());

        answer.Status.ShouldBe(accepted ? HttpStatusCode.Created : HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
    }

    [Theory]
    [InlineData(20000, true)]
    [InlineData(20001, false)]
    public async Task Create_BodyBoundary_AcceptsLimitAndRefusesOver(int length, bool accepted)
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var answer = await world.Client.SendAsync(
            HttpMethod.Post, Path(world.Project), As(world),
            new { kind = "epic", title = "Big", body = new string('b', length) }, NewKey());

        answer.Status.ShouldBe(accepted ? HttpStatusCode.Created : HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
    }

    [Fact]
    public async Task Create_InvalidKindUnknownFieldForeignReferences_Are422AndWriteNothing()
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var caller = As(world);
        var before = await Scalar<long>(world, $"select count(*) from public.requirement where project_id = '{world.Project}'");

        foreach (var body in new object[]
        {
            new { kind = "nonsense", title = "x" },
            new { kind = "epic", title = "x", surprise = 1 },
            new { kind = "epic", title = "x", parent_requirement_id = Guid.CreateVersion7() },
            new { kind = "epic", title = "x", feature_node_id = Guid.CreateVersion7() },
        })
        {
            var answer = await world.Client.SendAsync(HttpMethod.Post, Path(world.Project), caller, body, NewKey());
            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        }

        (await Scalar<long>(world, $"select count(*) from public.requirement where project_id = '{world.Project}'")).ShouldBe(before);
        (await world.Client.SendAsync(HttpMethod.Post, Path(world.Project), caller, new { kind = "epic", title = "ok" }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Patch_ReparentUnderDescendant_Is422Cycle()
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var caller = As(world);
        var parent = await CreateAsync(world, caller, "parent");
        var parentId = parent.Body.GetProperty("requirement_id").GetGuid();
        var child = await world.Client.SendAsync(
            HttpMethod.Post, Path(world.Project), caller,
            new { kind = "user_story", title = "child", parent_requirement_id = parentId }, NewKey());
        child.Status.ShouldBe(HttpStatusCode.Created, child.ErrorMessage);

        var cycle = await world.Client.SendAsync(
            HttpMethod.Patch, Path(world.Project, parentId), caller,
            new { parent_requirement_id = child.Body.GetProperty("requirement_id").GetGuid() }, NewKey(), parent.ETag);

        cycle.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, cycle.ErrorMessage);
        (await Scalar<long>(world, $"select count(*) from public.requirement_version where requirement_id = '{parentId}'")).ShouldBe(1);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    [InlineData("cursor=garbage")]
    public async Task List_BadPagingParameters_Are422(string query)
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var caller = As(world);

        (await world.Client.SendAsync(HttpMethod.Get, Path(world.Project) + "?" + query, caller))
            .Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await world.Client.SendAsync(HttpMethod.Get, Path(world.Project) + "?limit=200", caller))
            .Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Versions_BodiesOverByteCap_PageEarlyAndCursorContinues()
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var caller = As(world);
        var created = await CreateAsync(world, caller, new string('a', 20000));
        var id = created.Body.GetProperty("requirement_id").GetGuid();
        var etag = created.ETag;
        for (var index = 0; index < 15; index++)
        {
            var edited = await world.Client.SendAsync(
                HttpMethod.Patch, Path(world.Project, id), caller, new { body = new string((char)('b' + index), 20000) }, NewKey(), etag);
            edited.Status.ShouldBe(HttpStatusCode.OK, edited.ErrorMessage);
            etag = edited.ETag;
        }

        var page = await world.Client.SendAsync(HttpMethod.Get, Path(world.Project, id) + "/versions?limit=200", caller);
        page.Status.ShouldBe(HttpStatusCode.OK, page.ErrorMessage);
        var count = page.Body.GetProperty("items").GetArrayLength();
        count.ShouldBeLessThan(16);
        var cursor = page.Body.GetProperty("next_cursor").GetString();
        cursor.ShouldNotBeNullOrEmpty();
        var rest = await world.Client.SendAsync(HttpMethod.Get, Path(world.Project, id) + "/versions?limit=200&cursor=" + cursor, caller);
        rest.Status.ShouldBe(HttpStatusCode.OK, rest.ErrorMessage);
        (count + rest.Body.GetProperty("items").GetArrayLength()).ShouldBe(16);
    }

    [Fact]
    public async Task Requirements_ViewerReadsButCannotWrite_AndOtherTenantSees404()
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var admin = As(world);
        var created = await CreateAsync(world, admin, "first");
        var id = created.Body.GetProperty("requirement_id").GetGuid();
        var viewer = await ViewerAsync(world);

        (await world.Client.SendAsync(HttpMethod.Get, Path(world.Project, id), viewer)).Status.ShouldBe(HttpStatusCode.OK);
        var denied = await world.Client.SendAsync(
            HttpMethod.Patch, Path(world.Project, id), viewer, new { title = "x" }, NewKey(), created.ETag);
        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        (await world.Client.SendAsync(HttpMethod.Post, Path(world.Project), viewer, new { kind = "epic", title = "x" }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.Forbidden);
        (await world.Client.SendAsync(HttpMethod.Delete, Path(world.Project, id), viewer, null, NewKey(), created.ETag))
            .Status.ShouldBe(HttpStatusCode.Forbidden);
        (await Scalar<long>(world, $"select count(*) from public.requirement_version where requirement_id = '{id}'")).ShouldBe(1);

        var rivalUser = Guid.CreateVersion7();
        var rival = await CreateOrgAsync(world.Client, rivalUser, Unique.Slug("rival"));
        var foreign = await world.Client.SendAsync(
            HttpMethod.Get, Path(world.Project, id), TenancyApiFactory.Resolved(user: rivalUser, orgHeader: rival));
        foreign.Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Requirements_SiblingProject_Is404()
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var caller = As(world);
        var created = await CreateAsync(world, caller, "first");
        var id = created.Body.GetProperty("requirement_id").GetGuid();

        (await world.Client.SendAsync(HttpMethod.Get, Path(world.OtherProject, id), caller)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(
            HttpMethod.Patch, Path(world.OtherProject, id), caller, new { title = "x" }, NewKey(), created.ETag))
            .Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(HttpMethod.Get, Path(world.Project, id), caller)).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Requirements_EveryAcceptedWrite_AppendsOneAuditRow_AndRefusalsNone()
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var caller = As(world);
        var start = await Audits(world);

        var created = await CreateAsync(world, caller, "first");
        var id = created.Body.GetProperty("requirement_id").GetGuid();
        await Audits(world, "authoring.requirement.create", start + 1);

        var edited = await world.Client.SendAsync(
            HttpMethod.Patch, Path(world.Project, id), caller, new { body = "second" }, NewKey(), created.ETag);
        await Audits(world, "authoring.requirement.update", start + 2);

        (await world.Client.SendAsync(HttpMethod.Patch, Path(world.Project, id), caller, new { title = "x" }, NewKey(), created.ETag))
            .Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await Audits(world)).ShouldBe(start + 2);

        var restored = await world.Client.SendAsync(
            HttpMethod.Post, Path(world.Project, id) + "/restore", caller,
            new { version_id = created.Body.GetProperty("version_id").GetGuid() }, NewKey(), edited.ETag);
        restored.Status.ShouldBe(HttpStatusCode.OK, restored.ErrorMessage);
        await Audits(world, "authoring.requirement.restore", start + 3);

        (await world.Client.SendAsync(HttpMethod.Delete, Path(world.Project, id), caller, null, NewKey(), restored.ETag))
            .Status.ShouldBe(HttpStatusCode.NoContent);
        await Audits(world, "authoring.requirement.delete", start + 4);
    }

    private static Action<HttpRequestHeaders> As(RequirementTestWorld world) =>
        TenancyApiFactory.Resolved(user: world.User, orgHeader: world.Org);

    private static string Path(Guid project, Guid? id = null) =>
        $"/v1/projects/{project:D}/authoring/requirements" + (id is { } value ? $"/{value:D}" : string.Empty);

    private static async Task<ApiAnswer> CreateAsync(RequirementTestWorld world, Action<HttpRequestHeaders> caller, string body)
    {
        var answer = await world.Client.SendAsync(
            HttpMethod.Post, Path(world.Project), caller, new { kind = "user_story", title = "Pay by card", body, reason = "author" }, NewKey());
        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);

        return answer;
    }

    private Task<T> Scalar<T>(RequirementTestWorld world, string sql) => fixture.ScalarAsync<T>(world.Database, sql);

    private Task<string> Rows(RequirementTestWorld world, Guid id, string predicate) => Scalar<string>(
        world,
        $"select jsonb_agg(to_jsonb(v) order by version_no)::text from public.requirement_version v where requirement_id = '{id}' and {predicate}");

    /// <summary>Counts the org's audit rows; with an action, also pins the newest row's action and the count.</summary>
    private async Task<long> Audits(RequirementTestWorld world, string? action = null, long? expected = null)
    {
        var count = await Scalar<long>(world, $"select count(*) from public.audit_log where org_id = '{world.Org}'");
        if (action is not null)
        {
            count.ShouldBe(expected!.Value);
            (await Scalar<string>(world,
                $"select action from public.audit_log where org_id = '{world.Org}' order by seq desc limit 1"))
                .ShouldBe(action);
        }

        return count;
    }

    private async Task<Action<HttpRequestHeaders>> ViewerAsync(RequirementTestWorld world)
    {
        var user = Guid.CreateVersion7();
        var member = Guid.CreateVersion7();
        await fixture.ExecuteAsync(world.Database, $"""
            insert into public."user" (id, email) values ('{user}', 'req-{user:N}@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{member}', '{world.Org}', '{user}', 'req-{user:N}@example.test', 'active');
            """);
        var role = await Scalar<Guid>(world, "select id from public.role where org_id is null and code = 'viewer'");
        var granted = await world.Client.SendAsync(
            HttpMethod.Post, $"/v1/orgs/{world.Org:D}/memberships/{member:D}/roles", As(world), new { role_id = role }, NewKey());
        granted.Status.ShouldBe(HttpStatusCode.OK, granted.ErrorMessage);

        return TenancyApiFactory.Resolved(user: user, orgHeader: world.Org);
    }
}
