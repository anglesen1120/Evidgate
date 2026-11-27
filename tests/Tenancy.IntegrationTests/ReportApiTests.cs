using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Application.Reports;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;
using static Tenancy.IntegrationTests.ReportTestWorld;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The report routes and the build job through the real host and PostgreSQL: what a create refuses before it queues
/// anything, that one job publishes at most one identical snapshot, and that an interrupted or failed build leaves no row.
/// </summary>
[Trait("Category", "Docker")]
public sealed class ReportApiTests(PostgresTenancyFixture fixture)
{
    private static readonly string[] OneSection = [TestReportSectionBuilder.Id];

    [Fact]
    public async Task Create_QueuesOneJob_AndTheJobReadsPending_WithNoCustomerTextInTheAudit()
    {
        await using var world = await StartAsync(fixture);
        var seed = await SeedAsync(world);

        var created = await PostAsync(world, Body(seed.Node, strategy: "Secret strategy"));

        created.Status.ShouldBe(HttpStatusCode.Accepted, created.ErrorMessage);
        var job = created.Id("job_id");
        var queued = await QueuedPayloadAsync(world, job);
        queued.JobId.ShouldBe(job);
        queued.ProjectId.ShouldBe(world.Project);
        queued.SelectedIds.ShouldBe([seed.Node]);
        queued.Strategy.ShouldBe("Secret strategy");
        (await Scalar<long>(world, $"select count(*) from public.outbox where org_id = '{world.Org:D}' and task_type = '{TestReportTasks.TaskType}'"))
            .ShouldBe(1);

        var state = await world.Client.SendAsync(HttpMethod.Get, ReportPath(world.Project, $"/jobs/{job:D}"), Caller(world));
        state.Status.ShouldBe(HttpStatusCode.OK, state.ErrorMessage);
        state.Text("state").ShouldBe("pending");
        state.Body.GetProperty("report_id").ValueKind.ShouldBe(JsonValueKind.Null);

        var audit = await Scalar<string>(world, $"""
            select json_agg(json_build_object('action', action, 'project', project_id, 'object', object_id, 'metadata', metadata))::text
              from public.audit_log where org_id = '{world.Org:D}' and action = 'authoring.report.create'
            """);
        audit.ShouldNotContain("Secret");
        audit.ShouldContain(job.ToString("D"));
        audit.ShouldContain(world.Project.ToString("D"));
    }

    [Fact]
    public async Task Create_RefusesWhatTheBuildCannotServe_AndQueuesNothing()
    {
        await using var world = await StartAsync(fixture);
        var seed = await SeedAsync(world);
        var foreign = await SeedAsync(world, world.OtherProject);
        var before = await Scalar<long>(world, "select count(*) from public.outbox");

        await Refused(world, Body(seed.Node, scope: "manual_run"), "/scope_kind", "unsupported_scope");
        await Refused(world, Body(seed.Node, scope: "release"), "/scope_kind", "unsupported_scope");
        await Refused(world, Body(seed.Node, sections: ["execution"]), "/sections/0", "unsupported_section");
        await Refused(world, Body(seed.Node, sections: [TestReportSectionBuilder.Id, "execution"]), "/sections/1", "unsupported_section");
        await Refused(world, Body(Guid.CreateVersion7()), "/selected_ids", "unknown_selection");
        await Refused(world, Body(foreign.Node), "/selected_ids", "unknown_selection");
        await Refused(world, Body(seed.Node, scope: "requirements"), "/selected_ids", "unknown_selection");
        await Refused(world, new { scope_kind = "feature_nodes", selected_ids = Array.Empty<Guid>(), sections = OneSection, language = "vi" }, "/selected_ids", "out_of_range");

        (await Scalar<long>(world, "select count(*) from public.outbox")).ShouldBe(before);
    }

    [Fact]
    public async Task Create_ForeignOrDeletedId_IsAnsweredLikeAMissingOne()
    {
        await using var world = await StartAsync(fixture);
        var seed = await SeedAsync(world);
        await fixture.ExecuteAsync(world.Database, $"update public.feature_node set deleted_at = now() where id = '{seed.Node:D}'");

        var deleted = await PostAsync(world, Body(seed.Node));
        var missing = await PostAsync(world, Body(Guid.CreateVersion7()));

        deleted.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        deleted.Body.GetProperty("error").GetProperty("details")[0].GetProperty("reason").GetString().ShouldBe("unknown_selection");
        deleted.Body.GetProperty("error").GetProperty("message").GetString()
            .ShouldBe(missing.Body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Build_PublishesTheSnapshot_AndTheRoutesReadIt()
    {
        await using var world = await StartAsync(fixture);
        var seed = await SeedAsync(world);
        var job = await CreateJobAsync(world, Body(seed.Node));

        await RunJobAsync(world, await QueuedPayloadAsync(world, job));

        var state = await world.Client.SendAsync(HttpMethod.Get, ReportPath(world.Project, $"/jobs/{job:D}"), Caller(world));
        state.Text("state").ShouldBe("complete");
        var report = state.Id("report_id");
        var detail = await world.Client.SendAsync(HttpMethod.Get, ReportPath(world.Project, $"/{report:D}"), Caller(world));
        detail.Status.ShouldBe(HttpStatusCode.OK, detail.ErrorMessage);
        detail.Id("job_id").ShouldBe(job);
        detail.Text("scope_kind").ShouldBe("feature_nodes");
        detail.Body.GetProperty("schema_version").GetInt32().ShouldBe(1);
        var section = detail.Body.GetProperty("payload").GetProperty("sections")[0];
        section.GetProperty("id").GetString().ShouldBe(TestReportSectionBuilder.Id);
        section.GetProperty("data").GetProperty("cases").GetArrayLength().ShouldBe(2);
        section.GetProperty("data").GetProperty("requirements").GetArrayLength().ShouldBe(1);
        section.GetProperty("data").GetProperty("links").GetArrayLength().ShouldBe(1);
        CanonicalHash.Compute(detail.Body.GetProperty("payload")).ShouldBe(detail.Text("content_hash"));

        var list = await world.Client.SendAsync(HttpMethod.Get, ReportPath(world.Project) + "?limit=1", Caller(world));
        list.Status.ShouldBe(HttpStatusCode.OK, list.ErrorMessage);
        list.Body.GetProperty("items")[0].GetProperty("report_id").GetGuid().ShouldBe(report);
        list.Body.GetProperty("items")[0].TryGetProperty("payload", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Build_ReadsTheTreeBelowTheSelection_AndOnlyLiveRows()
    {
        await using var world = await StartAsync(fixture);
        var seed = await SeedAsync(world);
        var child = Guid.CreateVersion7();
        var hidden = Guid.CreateVersion7();
        await fixture.ExecuteAsync(world.Database, $"""
            insert into public.feature_node (id, org_id, project_id, parent_id, kind, title, position)
            values ('{child}', '{world.Org}', '{world.Project}', '{seed.Node}', 'module', 'Child', 1),
                   ('{hidden}', '{world.Org}', '{world.Project}', '{seed.Node}', 'module', 'Hidden', 2);
            update public.feature_node set deleted_at = now() where id = '{hidden}';
            """);
        var job = await CreateJobAsync(world, Body(seed.Node));

        await RunJobAsync(world, await QueuedPayloadAsync(world, job));

        var data = await DataAsync(world, job);
        data.GetProperty("nodes").EnumerateArray().Select(n => n.GetGuid()).ShouldBe([seed.Node, child], ignoreOrder: true);
    }

    [Fact]
    public async Task Build_SameInputTwice_GivesTheSameHash_AndAnotherLanguageADifferentOne()
    {
        await using var world = await StartAsync(fixture);
        var seed = await SeedAsync(world);
        var first = await CreateJobAsync(world, Body(seed.Node));
        var second = await CreateJobAsync(world, Body(seed.Node));
        var english = await CreateJobAsync(world, Body(seed.Node, language: "en"));

        foreach (var job in new[] { first, second, english })
        {
            await RunJobAsync(world, await QueuedPayloadAsync(world, job));
        }

        var one = await HashAsync(world, first);
        (await HashAsync(world, second)).ShouldBe(one);
        (await HashAsync(world, english)).ShouldNotBe(one);
        (await Scalar<long>(world, $"select count(*) from public.test_report where org_id = '{world.Org:D}'")).ShouldBe(3);
    }

    [Fact]
    public async Task Build_RedeliveredJob_PublishesOneReport()
    {
        await using var world = await StartAsync(fixture);
        var seed = await SeedAsync(world);
        var job = await CreateJobAsync(world, Body(seed.Node));
        var payload = await QueuedPayloadAsync(world, job);

        await RunJobAsync(world, payload);
        var once = await HashAsync(world, job);
        await RunJobAsync(world, payload);

        (await Scalar<long>(world, $"select count(*) from public.test_report where job_id = '{job:D}'")).ShouldBe(1);
        (await HashAsync(world, job)).ShouldBe(once);
    }

    [Fact]
    public async Task Build_InterruptedAttempt_LeavesNoRow_AndTheNextAttemptPublishes()
    {
        await using var world = await StartAsync(fixture);
        var seed = await SeedAsync(world);
        var job = await CreateJobAsync(world, Body(seed.Node));
        var payload = await QueuedPayloadAsync(world, job);

        await RunJobAsync(world, payload, commit: false);
        (await Scalar<long>(world, $"select count(*) from public.test_report where job_id = '{job:D}'")).ShouldBe(0);

        await RunJobAsync(world, payload);
        (await Scalar<long>(world, $"select count(*) from public.test_report where job_id = '{job:D}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Build_TooLargeSnapshot_FailsWithItsReason_AndPublishesNothing()
    {
        await using var world = await StartAsync(fixture);
        var seed = await SeedAsync(world);
        var payload = (await QueuedPayloadAsync(world, await CreateJobAsync(world, Body(seed.Node)))) with { Strategy = "oversize" };

        var error = await Should.ThrowAsync<ReportBuildException>(() => RunJobAsync(world, payload));

        error.Message.ShouldStartWith(ReportBuildException.TooLarge);
        (await Scalar<long>(world, $"select count(*) from public.test_report where job_id = '{payload.JobId:D}'")).ShouldBe(0);
    }

    [Fact]
    public async Task Source_OverTheByteCap_IsRefusedBeforeItIsBuilt_AndUnderItIsRead()
    {
        await using var world = await StartAsync(fixture);
        var seed = await SeedAsync(world);
        var request = new ReportRequest(ReportScopeKinds.FeatureNodes, [seed.Node], OneSection, "vi", null, null);

        var read = await ReadSourceAsync(world, request, TestReportLimits.MaxSourceBytes);
        var error = await Should.ThrowAsync<ReportBuildException>(() => ReadSourceAsync(world, request, 1));

        read.Nodes.ShouldContain(node => node.Id == seed.Node);
        error.Message.ShouldStartWith(ReportBuildException.TooLarge);
    }

    [Fact]
    public async Task Source_ByteCap_CountsUncompressedBytes_SoACompressibleTextIsNotUndercounted()
    {
        await using var world = await StartAsync(fixture);
        var seed = await SeedAsync(world);
        // 4000 identical characters compress to a few dozen stored bytes, so pg_column_size would read this under 3000.
        await fixture.ExecuteAsync(world.Database, $"update public.checklist_item set description = repeat('a', 4000) where id = '{seed.Item:D}'");
        var request = new ReportRequest(ReportScopeKinds.FeatureNodes, [seed.Node], OneSection, "vi", null, null);

        var error = await Should.ThrowAsync<ReportBuildException>(() => ReadSourceAsync(world, request, 3000));
        var read = await ReadSourceAsync(world, request, 100_000);

        error.Message.ShouldStartWith(ReportBuildException.TooLarge);
        read.Items.ShouldContain(item => item.Id == seed.Item);
    }

    [Fact]
    public async Task Build_ForARequirementScope_ReadsTheHeadAndItsLinks()
    {
        await using var world = await StartAsync(fixture);
        var seed = await SeedAsync(world);
        var job = await CreateJobAsync(world, Body(seed.Requirement, scope: "requirements"));

        await RunJobAsync(world, await QueuedPayloadAsync(world, job));

        var data = await DataAsync(world, job);
        data.GetProperty("requirements").EnumerateArray().Select(r => r.GetString()).ShouldBe([$"{seed.Requirement:D}@1"]);
        data.GetProperty("items").EnumerateArray().Select(i => i.GetGuid()).ShouldBe([seed.Item]);
        data.GetProperty("nodes").EnumerateArray().Select(n => n.GetGuid()).ShouldContain(seed.Node);
    }

    [Fact]
    public async Task Routes_AnotherProjectsJobAndReport_AreNotFound()
    {
        await using var world = await StartAsync(fixture);
        var seed = await SeedAsync(world);
        var job = await CreateJobAsync(world, Body(seed.Node));
        await RunJobAsync(world, await QueuedPayloadAsync(world, job));
        var report = await Scalar<Guid>(world, $"select id from public.test_report where job_id = '{job:D}'");

        (await world.Client.SendAsync(HttpMethod.Get, ReportPath(world.OtherProject, $"/jobs/{job:D}"), Caller(world))).Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(HttpMethod.Get, ReportPath(world.OtherProject, $"/{report:D}"), Caller(world))).Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(HttpMethod.Get, ReportPath(world.Project, $"/jobs/{Guid.CreateVersion7():D}"), Caller(world))).Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(HttpMethod.Get, ReportPath(world.OtherProject), Caller(world))).Body.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Routes_AViewerReadsButCannotCreate_AndAStrangerSeesNothing()
    {
        await using var world = await StartAsync(fixture);
        var seed = await SeedAsync(world);
        var job = await CreateJobAsync(world, Body(seed.Node));
        await RunJobAsync(world, await QueuedPayloadAsync(world, job));
        var viewer = await ViewerAsync(world);
        var before = await Scalar<long>(world, "select count(*) from public.outbox");

        (await world.Client.SendAsync(HttpMethod.Get, ReportPath(world.Project), viewer)).Status.ShouldBe(HttpStatusCode.OK);
        var denied = await world.Client.SendAsync(HttpMethod.Post, ReportPath(world.Project), viewer, Body(seed.Node), NewKey());
        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        (await Scalar<long>(world, "select count(*) from public.outbox")).ShouldBe(before);

        var rivalUser = Guid.CreateVersion7();
        var rival = await CreateOrgAsync(world.Client, rivalUser, Unique.Slug("rival"));
        var foreign = await world.Client.SendAsync(
            HttpMethod.Get, ReportPath(world.Project), TenancyApiFactory.Resolved(user: rivalUser, orgHeader: rival));
        foreign.Status.ShouldBe(HttpStatusCode.NotFound);
    }

    private sealed record Seed(Guid Node, Guid Item, Guid Requirement);

    private static object Body(
        Guid id,
        string scope = "feature_nodes",
        string[]? sections = null,
        string language = "vi",
        string? strategy = null) =>
        new { scope_kind = scope, selected_ids = new[] { id }, sections = sections ?? [TestReportSectionBuilder.Id], language, strategy };

    private static Task<ApiAnswer> PostAsync(RequirementTestWorld world, object body) =>
        world.Client.SendAsync(HttpMethod.Post, ReportPath(world.Project), Caller(world), body, NewKey());

    private static async Task<Guid> CreateJobAsync(RequirementTestWorld world, object body)
    {
        var created = await PostAsync(world, body);
        created.Status.ShouldBe(HttpStatusCode.Accepted, created.ErrorMessage);

        return created.Id("job_id");
    }

    private static async Task Refused(RequirementTestWorld world, object body, string path, string reason)
    {
        var answer = await PostAsync(world, body);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        var detail = answer.Body.GetProperty("error").GetProperty("details")[0];
        detail.GetProperty("path").GetString().ShouldBe(path);
        detail.GetProperty("reason").GetString().ShouldBe(reason);
    }

    private static async Task<Seed> SeedAsync(RequirementTestWorld world, Guid? project = null)
    {
        var targets = await RequirementLinkTestData.SeedAsync(world, project);
        var node = await world.Fixture.ScalarAsync<Guid>(
            world.Database,
            $"select feature_node_id from public.checklist_item where id = '{targets.Item:D}'");
        var head = await world.CreatedAsync(world.Create(featureNode: node, project: project));
        await world.InScopeAsync((writer, token) => writer.CreateLinkAsync(
            world.Org,
            new RequirementLinkCreate(
                project ?? world.Project,
                head.RequirementId,
                targets.Item,
                null,
                head.VersionId,
                null),
            token), project);

        return new Seed(node, targets.Item, head.RequirementId);
    }

    private async Task<JsonElement> DataAsync(RequirementTestWorld world, Guid job)
    {
        var report = await Scalar<Guid>(world, $"select id from public.test_report where job_id = '{job:D}'");
        var detail = await world.Client.SendAsync(HttpMethod.Get, ReportPath(world.Project, $"/{report:D}"), Caller(world));

        return detail.Body.GetProperty("payload").GetProperty("sections")[0].GetProperty("data").Clone();
    }

    private Task<string> HashAsync(RequirementTestWorld world, Guid job) =>
        Scalar<string>(world, $"select content_hash from public.test_report where job_id = '{job:D}'");

    private Task<T> Scalar<T>(RequirementTestWorld world, string sql) => fixture.ScalarAsync<T>(world.Database, sql);

    private async Task<Action<System.Net.Http.Headers.HttpRequestHeaders>> ViewerAsync(RequirementTestWorld world)
    {
        var user = Guid.CreateVersion7();
        var member = Guid.CreateVersion7();
        await fixture.ExecuteAsync(world.Database, $"""
            insert into public."user" (id, email) values ('{user}', 'rep-{user:N}@example.test');
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{member}', '{world.Org}', '{user}', 'rep-{user:N}@example.test', 'active');
            """);
        var role = await Scalar<Guid>(world, "select id from public.role where org_id is null and code = 'viewer'");
        var granted = await world.Client.SendAsync(
            HttpMethod.Post, $"/v1/orgs/{world.Org:D}/memberships/{member:D}/roles", Caller(world), new { role_id = role }, NewKey());
        granted.Status.ShouldBe(HttpStatusCode.OK, granted.ErrorMessage);

        return TenancyApiFactory.Resolved(user: user, orgHeader: world.Org);
    }
}
