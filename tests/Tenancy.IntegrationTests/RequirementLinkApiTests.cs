using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The requirement link and coverage routes through the real host and PostgreSQL: the 201/200 answers, every refusal
/// (and that it writes nothing), the permission and project-scope gates, the keyset page and the lock-timeout 503.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RequirementLinkApiTests(PostgresTenancyFixture fixture)
{
    [Fact]
    public async Task Link_ChecklistItem_Is201ThenRepeatIs200WithoutDoubleCount()
    {
        await using var scene = await SceneAsync();
        var (world, caller) = (scene.World, scene.Caller);
        var before = await Audits(world);

        var created = await world.Client.SendAsync(HttpMethod.Post, LinksPath(scene), caller, ItemBody(scene), NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        created.Body.GetProperty("changed").GetBoolean().ShouldBeTrue();
        created.Body.GetProperty("version_id").GetGuid().ShouldBe(scene.Version);
        created.Body.GetProperty("version_no").GetInt32().ShouldBe(1);
        created.Body.GetProperty("manual_test_case_id").ValueKind.ShouldBe(JsonValueKind.Null);
        (await Audits(world)).ShouldBe(before + 1);

        var repeat = await world.Client.SendAsync(HttpMethod.Post, LinksPath(scene), caller, ItemBody(scene), NewKey());
        repeat.Status.ShouldBe(HttpStatusCode.OK, repeat.ErrorMessage);
        repeat.Body.GetProperty("changed").GetBoolean().ShouldBeFalse();
        repeat.Id("link_id").ShouldBe(created.Id("link_id"));
        (await Audits(world)).ShouldBe(before + 1);

        var coverage = await CoverageAsync(scene);
        coverage.GetProperty("link_count").GetInt32().ShouldBe(1);
        coverage.GetProperty("linked_case_count").GetInt32().ShouldBe(2);
        coverage.GetProperty("approved_case_count").GetInt32().ShouldBe(1);
        coverage.GetProperty("stale_link_count").GetInt32().ShouldBe(0);
        Cases(coverage).ShouldAllBe(item => item.GetProperty("link_path").GetString() == "via_item");
    }

    [Fact]
    public async Task Coverage_NoResultSource_SaysUnavailableNeverZero()
    {
        await using var scene = await SceneAsync();
        (await scene.World.Client.SendAsync(HttpMethod.Post, LinksPath(scene), scene.Caller, ItemBody(scene), NewKey()))
            .Status.ShouldBe(HttpStatusCode.Created);

        var coverage = await CoverageAsync(scene);

        coverage.GetProperty("result_source_available").GetBoolean().ShouldBeFalse();
        Cases(coverage).Count.ShouldBeGreaterThan(0);
        Cases(coverage).ShouldAllBe(item => item.GetProperty("result_availability").GetString() == "unavailable");
        coverage.EnumerateObject().Select(member => member.Name).ShouldNotContain(name => name.Contains("pass", StringComparison.Ordinal) || name.Contains("fail", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Link_DirectAndViaItem_ListsTheCaseOnceWithPathBoth()
    {
        await using var scene = await SceneAsync();
        var client = scene.World.Client;
        (await client.SendAsync(HttpMethod.Post, LinksPath(scene), scene.Caller, ItemBody(scene), NewKey()))
            .Status.ShouldBe(HttpStatusCode.Created);
        var direct = await client.SendAsync(HttpMethod.Post, LinksPath(scene), scene.Caller, CaseBody(scene, scene.Targets.ApprovedCase), NewKey());
        direct.Status.ShouldBe(HttpStatusCode.Created, direct.ErrorMessage);

        var coverage = await CoverageAsync(scene);

        coverage.GetProperty("link_count").GetInt32().ShouldBe(2);
        coverage.GetProperty("linked_case_count").GetInt32().ShouldBe(2);
        var cases = Cases(coverage);
        cases.Count.ShouldBe(2);
        cases.Single(item => item.GetProperty("case_id").GetGuid() == scene.Targets.ApprovedCase).GetProperty("link_path").GetString().ShouldBe("both");
        cases.Single(item => item.GetProperty("case_id").GetGuid() == scene.Targets.DraftCase).GetProperty("link_path").GetString().ShouldBe("via_item");
    }

    [Fact]
    public async Task Link_NeitherBothOrMalformedTarget_Is422AndWritesNothing()
    {
        await using var scene = await SceneAsync();
        var client = scene.World.Client;
        var before = await Audits(scene.World);

        var neither = await client.SendAsync(HttpMethod.Post, LinksPath(scene), scene.Caller, new { }, NewKey());
        neither.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        neither.ErrorCode.ShouldBe(ApiErrorCode.RequestInvalid);

        var both = await client.SendAsync(
            HttpMethod.Post, LinksPath(scene), scene.Caller,
            new { checklist_item_id = scene.Targets.Item, manual_test_case_id = scene.Targets.ApprovedCase }, NewKey());
        both.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var malformed = await client.SendAsync(HttpMethod.Post, LinksPath(scene), scene.Caller, new { checklist_item_id = "not-a-uuid" }, NewKey());
        malformed.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        malformed.Body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetProperty("path").GetString()).ShouldContain("/checklist_item_id");

        var unknown = await client.SendAsync(HttpMethod.Post, LinksPath(scene), scene.Caller, new { checklist_item_id = scene.Targets.Item, extra = 1 }, NewKey());
        unknown.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);

        (await LinkCount(scene)).ShouldBe(0);
        (await Audits(scene.World)).ShouldBe(before);
        (await client.SendAsync(HttpMethod.Post, LinksPath(scene), scene.Caller, ItemBody(scene), NewKey()))
            .Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Link_TargetOfAnotherProject_IsRefusedAndWritesNothing()
    {
        await using var scene = await SceneAsync();
        var foreign = await RequirementLinkTestData.SeedAsync(scene.World, scene.World.OtherProject, 5);

        var item = await scene.World.Client.SendAsync(
            HttpMethod.Post, LinksPath(scene), scene.Caller, new { checklist_item_id = foreign.Item, expected_version_id = scene.Version }, NewKey());
        item.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, item.ErrorMessage);
        var manual = await scene.World.Client.SendAsync(HttpMethod.Post, LinksPath(scene), scene.Caller, CaseBody(scene, foreign.ApprovedCase), NewKey());
        manual.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, manual.ErrorMessage);

        (await LinkCount(scene)).ShouldBe(0);
        (await scene.World.Client.SendAsync(HttpMethod.Post, LinksPath(scene), scene.Caller, ItemBody(scene), NewKey()))
            .Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Routes_UnknownOtherProjectOrOtherTenantRequirement_Are404()
    {
        await using var scene = await SceneAsync();
        await using var stranger = await SceneAsync();
        var world = scene.World;
        var elsewhere = await CreateRequirementAsync(world, scene.Caller, world.OtherProject);
        var body = ItemBody(scene);

        foreach (var requirement in new[] { Guid.CreateVersion7(), elsewhere.Id("requirement_id"), stranger.Requirement })
        {
            var path = RequirementPath(world.Project, requirement);
            (await world.Client.SendAsync(HttpMethod.Post, path + "/links", scene.Caller, body, NewKey())).Status.ShouldBe(HttpStatusCode.NotFound);
            (await world.Client.SendAsync(HttpMethod.Get, path + "/coverage", scene.Caller)).Status.ShouldBe(HttpStatusCode.NotFound);
            (await world.Client.SendAsync(HttpMethod.Delete, path + $"/links/{Guid.CreateVersion7():D}", scene.Caller, null, NewKey()))
                .Status.ShouldBe(HttpStatusCode.NotFound);
        }

        (await LinkCount(scene)).ShouldBe(0);
        (await world.Client.SendAsync(HttpMethod.Get, RequirementPath(world.Project, scene.Requirement) + "/coverage", scene.Caller))
            .Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Link_StaleExpectedVersion_Is412AndCurrentHeadIsAccepted()
    {
        await using var scene = await SceneAsync();
        var client = scene.World.Client;
        var edited = await client.SendAsync(
            HttpMethod.Patch, RequirementPath(scene.World.Project, scene.Requirement), scene.Caller, new { body = "second" }, NewKey(), scene.ETag);
        edited.Status.ShouldBe(HttpStatusCode.OK, edited.ErrorMessage);

        var stale = await client.SendAsync(HttpMethod.Post, LinksPath(scene), scene.Caller, ItemBody(scene), NewKey());
        stale.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        stale.ErrorCode.ShouldBe(ApiErrorCode.PreconditionFailed);
        var details = stale.Body.GetProperty("error").GetProperty("details").EnumerateArray().ToList();
        details.ShouldContain(detail => detail.GetProperty("path").GetString() == "/expected_version_id");
        details.Single(detail => detail.GetProperty("path").GetString() == "/head_version_id").GetProperty("reason").GetString()
            .ShouldBe(edited.Id("version_id").ToString("D"));
        (await LinkCount(scene)).ShouldBe(0);

        var current = await client.SendAsync(
            HttpMethod.Post, LinksPath(scene), scene.Caller,
            new { checklist_item_id = scene.Targets.Item, expected_version_id = edited.Id("version_id") }, NewKey());
        current.Status.ShouldBe(HttpStatusCode.Created, current.ErrorMessage);
        current.Body.GetProperty("version_no").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Link_TargetPinnedAtOlderVersion_Is409AndCoverageCountsItStale()
    {
        await using var scene = await SceneAsync();
        var client = scene.World.Client;
        (await client.SendAsync(HttpMethod.Post, LinksPath(scene), scene.Caller, ItemBody(scene), NewKey()))
            .Status.ShouldBe(HttpStatusCode.Created);
        var edited = await client.SendAsync(
            HttpMethod.Patch, RequirementPath(scene.World.Project, scene.Requirement), scene.Caller, new { body = "second" }, NewKey(), scene.ETag);
        edited.Status.ShouldBe(HttpStatusCode.OK, edited.ErrorMessage);

        var older = await client.SendAsync(
            HttpMethod.Post, LinksPath(scene), scene.Caller,
            new { checklist_item_id = scene.Targets.Item, expected_version_id = edited.Id("version_id") }, NewKey());

        older.Status.ShouldBe(HttpStatusCode.Conflict);
        older.ErrorCode.ShouldBe(ApiErrorCode.InvalidTransition);
        var coverage = await CoverageAsync(scene);
        coverage.GetProperty("link_count").GetInt32().ShouldBe(1);
        coverage.GetProperty("stale_link_count").GetInt32().ShouldBe(1);
        coverage.GetProperty("targets").EnumerateArray().Single().GetProperty("stale").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Unlink_RemovesTheLinkOnceAndRefusesAnotherRequirementsLink()
    {
        await using var scene = await SceneAsync();
        var world = scene.World;
        var other = await CreateRequirementAsync(world, scene.Caller, world.Project);
        var otherPath = RequirementPath(world.Project, other.Id("requirement_id"));
        var mine = await world.Client.SendAsync(HttpMethod.Post, LinksPath(scene), scene.Caller, ItemBody(scene), NewKey());
        var theirs = await world.Client.SendAsync(
            HttpMethod.Post, otherPath + "/links", scene.Caller,
            new { manual_test_case_id = scene.Targets.DraftCase, expected_version_id = other.Id("version_id") }, NewKey());
        theirs.Status.ShouldBe(HttpStatusCode.Created, theirs.ErrorMessage);
        var before = await Audits(world);

        (await world.Client.SendAsync(HttpMethod.Delete, LinksPath(scene) + $"/{theirs.Id("link_id"):D}", scene.Caller, null, NewKey()))
            .Status.ShouldBe(HttpStatusCode.NotFound);
        (await LinkCount(scene)).ShouldBe(2);

        var removed = await world.Client.SendAsync(HttpMethod.Delete, LinksPath(scene) + $"/{mine.Id("link_id"):D}", scene.Caller, null, NewKey());
        removed.Status.ShouldBe(HttpStatusCode.NoContent, removed.ErrorMessage);
        (await Audits(world)).ShouldBe(before + 1);
        (await fixture.ScalarAsync<string>(world.Database, $"select action from public.audit_log where org_id = '{world.Org}' order by seq desc limit 1"))
            .ShouldBe("authoring.requirement_link.remove");
        (await world.Client.SendAsync(HttpMethod.Delete, LinksPath(scene) + $"/{mine.Id("link_id"):D}", scene.Caller, null, NewKey()))
            .Status.ShouldBe(HttpStatusCode.NotFound);
        (await Audits(world)).ShouldBe(before + 1);
        (await LinkCount(scene)).ShouldBe(1);
        (await CoverageAsync(scene)).GetProperty("link_count").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Unlink_AfterTheRequirementWasDeleted_StillRemovesTheLink()
    {
        await using var scene = await SceneAsync();
        var world = scene.World;
        var link = await world.Client.SendAsync(HttpMethod.Post, LinksPath(scene), scene.Caller, ItemBody(scene), NewKey());
        (await world.Client.SendAsync(HttpMethod.Delete, RequirementPath(world.Project, scene.Requirement), scene.Caller, null, NewKey(), scene.ETag))
            .Status.ShouldBe(HttpStatusCode.NoContent);

        (await world.Client.SendAsync(HttpMethod.Get, RequirementPath(world.Project, scene.Requirement) + "/coverage", scene.Caller))
            .Status.ShouldBe(HttpStatusCode.OK);
        (await world.Client.SendAsync(HttpMethod.Post, LinksPath(scene), scene.Caller, CaseBody(scene, scene.Targets.DraftCase), NewKey()))
            .Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(HttpMethod.Delete, LinksPath(scene) + $"/{link.Id("link_id"):D}", scene.Caller, null, NewKey()))
            .Status.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Routes_ViewerWithoutAuthoringWrite_Gets403OnWritesAndCoverageStaysReadable()
    {
        await using var scene = await SceneAsync();
        var world = scene.World;
        var viewer = await ViewerAsync(world);
        var link = await world.Client.SendAsync(HttpMethod.Post, LinksPath(scene), scene.Caller, ItemBody(scene), NewKey());
        link.Status.ShouldBe(HttpStatusCode.Created);

        var create = await world.Client.SendAsync(HttpMethod.Post, LinksPath(scene), viewer, CaseBody(scene, scene.Targets.DraftCase), NewKey());
        create.Status.ShouldBe(HttpStatusCode.Forbidden);
        create.ErrorCode.ShouldBe(ApiErrorCode.Forbidden);
        var remove = await world.Client.SendAsync(HttpMethod.Delete, LinksPath(scene) + $"/{link.Id("link_id"):D}", viewer, null, NewKey());
        remove.Status.ShouldBe(HttpStatusCode.Forbidden);

        (await LinkCount(scene)).ShouldBe(1);
        (await world.Client.SendAsync(HttpMethod.Get, CoveragePath(scene), viewer)).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Routes_CallerNarrowedToAnotherProject_Is403ProjectScopeMismatch()
    {
        await using var scene = await SceneAsync();
        var world = scene.World;
        var narrowed = TenancyApiFactory.Caller(org: world.Org, user: world.User, project: world.OtherProject);
        var inScope = TenancyApiFactory.Caller(org: world.Org, user: world.User, project: world.Project);
        var link = await world.Client.SendAsync(HttpMethod.Post, LinksPath(scene), inScope, ItemBody(scene), NewKey());
        link.Status.ShouldBe(HttpStatusCode.Created, link.ErrorMessage);

        var create = await world.Client.SendAsync(HttpMethod.Post, LinksPath(scene), narrowed, CaseBody(scene, scene.Targets.DraftCase), NewKey());
        var remove = await world.Client.SendAsync(HttpMethod.Delete, LinksPath(scene) + $"/{link.Id("link_id"):D}", narrowed, null, NewKey());
        var read = await world.Client.SendAsync(HttpMethod.Get, CoveragePath(scene), narrowed);

        foreach (var answer in new[] { create, remove, read })
        {
            answer.Status.ShouldBe(HttpStatusCode.Forbidden);
            answer.ErrorCode.ShouldBe(ApiErrorCode.ProjectScopeMismatch);
        }

        (await LinkCount(scene)).ShouldBe(1);
        (await world.Client.SendAsync(HttpMethod.Get, CoveragePath(scene), inScope)).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Coverage_BadLimitOrCursor_Is422AndKeysetPagesWalkEveryCaseOnce()
    {
        await using var scene = await SceneAsync();
        var client = scene.World.Client;
        (await client.SendAsync(HttpMethod.Post, LinksPath(scene), scene.Caller, ItemBody(scene), NewKey()))
            .Status.ShouldBe(HttpStatusCode.Created);

        foreach (var query in new[] { "?limit=0", "?limit=201", "?limit=abc", "?after_code=" + new string('x', 65) })
        {
            (await client.SendAsync(HttpMethod.Get, CoveragePath(scene) + query, scene.Caller)).Status
                .ShouldBe(HttpStatusCode.UnprocessableEntity, query);
        }

        var first = await client.SendAsync(HttpMethod.Get, CoveragePath(scene) + "?limit=1", scene.Caller);
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        var firstCode = Cases(first.Body).Single().GetProperty("code").GetString();
        var next = first.Text("next_code");
        next.ShouldBe(firstCode);

        var second = await client.SendAsync(HttpMethod.Get, CoveragePath(scene) + $"?limit=1&after_code={next}", scene.Caller);
        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        var secondCode = Cases(second.Body).Single().GetProperty("code").GetString();
        string.CompareOrdinal(secondCode, firstCode).ShouldBeGreaterThan(0);
        var third = await client.SendAsync(HttpMethod.Get, CoveragePath(scene) + $"?limit=1&after_code={secondCode}", scene.Caller);
        Cases(third.Body).Count.ShouldBe(0);
        third.Body.GetProperty("next_code").ValueKind.ShouldBe(JsonValueKind.Null);
        third.Body.GetProperty("linked_case_count").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Link_RequirementRowLockedPastLockTimeout_Is503ThenRetrySucceeds()
    {
        await using var scene = await SceneAsync();
        var world = scene.World;

        // An update of the requirement holds its row exclusively; the link's share lock waits and the writer's bounded
        // lock_timeout turns the wait into the retryable 503.
        await using var holder = await TenantScope.BeginAsync(fixture.OwnerConnectionString(world.Database), world.Org, world.Project);
        await holder.ExecuteAsync($"select 1 from public.requirement where id = '{scene.Requirement:D}' for update");

        var blocked = await world.Client.SendAsync(HttpMethod.Post, LinksPath(scene), scene.Caller, ItemBody(scene), NewKey());

        blocked.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        blocked.ErrorCode.ShouldBe(ApiErrorCode.Unavailable);
        (await LinkCount(scene)).ShouldBe(0);

        await holder.CommitAsync();
        (await world.Client.SendAsync(HttpMethod.Post, LinksPath(scene), scene.Caller, ItemBody(scene), NewKey()))
            .Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Unlink_LinkRowLockedPastLockTimeout_Is503AndTheLinkSurvives()
    {
        await using var scene = await SceneAsync();
        var world = scene.World;
        var link = await world.Client.SendAsync(HttpMethod.Post, LinksPath(scene), scene.Caller, ItemBody(scene), NewKey());
        link.Status.ShouldBe(HttpStatusCode.Created);

        await using var holder = await TenantScope.BeginAsync(fixture.OwnerConnectionString(world.Database), world.Org, world.Project);
        await holder.ExecuteAsync($"select 1 from public.requirement_link where id = '{link.Id("link_id"):D}' for update");

        var blocked = await world.Client.SendAsync(HttpMethod.Delete, LinksPath(scene) + $"/{link.Id("link_id"):D}", scene.Caller, null, NewKey());

        blocked.Status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        blocked.ErrorCode.ShouldBe(ApiErrorCode.Unavailable);
        (await LinkCount(scene)).ShouldBe(1);

        await holder.CommitAsync();
        (await world.Client.SendAsync(HttpMethod.Delete, LinksPath(scene) + $"/{link.Id("link_id"):D}", scene.Caller, null, NewKey()))
            .Status.ShouldBe(HttpStatusCode.NoContent);
    }

    private static string RequirementPath(Guid project, Guid requirement) =>
        $"/v1/projects/{project:D}/authoring/requirements/{requirement:D}";

    private static string LinksPath(Scene scene) => RequirementPath(scene.World.Project, scene.Requirement) + "/links";

    private static string CoveragePath(Scene scene) => RequirementPath(scene.World.Project, scene.Requirement) + "/coverage";

    private static List<JsonElement> Cases(JsonElement coverage) => [.. coverage.GetProperty("cases").EnumerateArray()];

    // Every link create names the head version it read; the scene's requirement is still at its first version.
    private static object ItemBody(Scene scene) => new { checklist_item_id = scene.Targets.Item, expected_version_id = scene.Version };

    private static object CaseBody(Scene scene, Guid manualCase) => new { manual_test_case_id = manualCase, expected_version_id = scene.Version };

    private static async Task<ApiAnswer> CreateRequirementAsync(
        RequirementTestWorld world, Action<HttpRequestHeaders> caller, Guid project)
    {
        var answer = await world.Client.SendAsync(
            HttpMethod.Post, $"/v1/projects/{project:D}/authoring/requirements", caller,
            new { kind = "user_story", title = "Pay by card", body = RequirementTestWorld.Body }, NewKey());
        answer.Status.ShouldBe(HttpStatusCode.Created, answer.ErrorMessage);

        return answer;
    }

    private async Task<Scene> SceneAsync()
    {
        var world = await RequirementTestWorld.StartAsync(fixture);
        var caller = TenancyApiFactory.Resolved(user: world.User, orgHeader: world.Org);
        var targets = await RequirementLinkTestData.SeedAsync(world);
        var created = await CreateRequirementAsync(world, caller, world.Project);

        return new Scene(world, caller, targets, created.Id("requirement_id"), created.Id("version_id"), created.ETag!);
    }

    private static async Task<JsonElement> CoverageAsync(Scene scene)
    {
        var answer = await scene.World.Client.SendAsync(HttpMethod.Get, CoveragePath(scene), scene.Caller);
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        return answer.Body;
    }

    private Task<long> LinkCount(Scene scene) => fixture.ScalarAsync<long>(
        scene.World.Database,
        $"select count(*) from public.requirement_link where project_id = '{scene.World.Project}'");

    private Task<long> Audits(RequirementTestWorld world) => fixture.ScalarAsync<long>(
        world.Database, $"select count(*) from public.audit_log where org_id = '{world.Org}'");

    private async Task<Action<HttpRequestHeaders>> ViewerAsync(RequirementTestWorld world)
    {
        var user = Guid.CreateVersion7();
        var member = Guid.CreateVersion7();
        await fixture.ExecuteAsync(world.Database, $"""
            insert into public."user" (id, email) values ('{user}', 'req-{user:N}@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{member}', '{world.Org}', '{user}', 'req-{user:N}@example.test', 'active');
            """);
        var role = await fixture.ScalarAsync<Guid>(world.Database, "select id from public.role where org_id is null and code = 'viewer'");
        var granted = await world.Client.SendAsync(
            HttpMethod.Post, $"/v1/orgs/{world.Org:D}/memberships/{member:D}/roles",
            TenancyApiFactory.Resolved(user: world.User, orgHeader: world.Org), new { role_id = role }, NewKey());
        granted.Status.ShouldBe(HttpStatusCode.OK, granted.ErrorMessage);

        return TenancyApiFactory.Resolved(user: user, orgHeader: world.Org);
    }

    /// <summary>One tenant with two projects, the seeded link targets of the first and one requirement at version 1.</summary>
    private sealed record Scene(
        RequirementTestWorld World,
        Action<HttpRequestHeaders> Caller,
        RequirementLinkTargets Targets,
        Guid Requirement,
        Guid Version,
        string ETag) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => World.DisposeAsync();
    }
}
