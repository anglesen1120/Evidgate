using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Application.Reports;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;
using static Tenancy.IntegrationTests.ReportTestWorld;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class QAReportSnapshotTests(PostgresTenancyFixture fixture)
{
    [Fact]
    public async Task Build_SourceChangesAfterPublication_KeepTheOldPayloadHashAndPinsAndChangeTheNewReport()
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var targets = await RequirementLinkTestData.SeedAsync(world);
        var node = await fixture.ScalarAsync<Guid>(world.Database, $"select feature_node_id from public.checklist_item where id = '{targets.Item:D}'");
        var head = await world.CreatedAsync(world.Create(title: "Original requirement", featureNode: node));
        await RequirementLinkTestData.LinkAsync(world, head, item: targets.Item);
        var firstJob = await CreateAsync(world, head.RequirementId, ReportSectionFixture.AllSections);
        var firstPayload = await QueuedPayloadAsync(world, firstJob);
        await PublishAsync(world, firstPayload);
        var first = await DetailAsync(world, firstJob);
        var originalPayload = first.Body.GetProperty("payload").GetRawText();
        var originalHash = first.Text("content_hash");
        var originalPins = await PinsAsync(world, firstJob);
        Section(first, "traceability").GetProperty("counts").GetProperty("covered_requirements").GetInt32().ShouldBe(1);
        Section(first, "test_cases").GetProperty("groups")[0].GetProperty("cases")[0].GetProperty("title").GetString().ShouldBe("Manual link case");

        await EditSourceAsync(world, head.RequirementId, targets.ApprovedCase);
        var secondJob = await CreateAsync(world, head.RequirementId, ReportSectionFixture.AllSections);
        await PublishAsync(world, await QueuedPayloadAsync(world, secondJob));
        await PublishAsync(world, firstPayload);

        var retained = await DetailAsync(world, firstJob);
        retained.Body.GetProperty("payload").GetRawText().ShouldBe(originalPayload);
        retained.Text("content_hash").ShouldBe(originalHash);
        (await PinsAsync(world, firstJob)).ShouldBe(originalPins);
        CanonicalHash.Compute(retained.Body.GetProperty("payload")).ShouldBe(originalHash);
        var current = await DetailAsync(world, secondJob);
        current.Text("content_hash").ShouldNotBe(originalHash);
        Section(current, "test_plan").GetProperty("requirements").GetProperty("items")[0].GetProperty("title").GetString().ShouldBe("Changed requirement");
        var changedCase = Section(current, "test_cases").GetProperty("groups")[0].GetProperty("cases")[0];
        changedCase.GetProperty("title").GetString().ShouldBe("Changed manual case");
        changedCase.GetProperty("steps")[0].GetProperty("data").GetString().ShouldBe("Changed data");
        changedCase.GetProperty("steps")[0].GetProperty("expected").GetString().ShouldBe("Changed expectation");
        var counts = Section(current, "traceability").GetProperty("counts");
        counts.GetProperty("covered_requirements").GetInt32().ShouldBe(0);
        counts.GetProperty("stale_links").GetInt32().ShouldBe(2);
        (await fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.test_report where job_id = '{firstJob:D}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Build_ASectionFailsAfterAnEarlierSection_ExposesNoPartialReportAndCanBeRetried()
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var head = await world.CreatedAsync(world.Create());
        var job = await CreateAsync(world, head.RequirementId, ["test_plan"]);
        var payload = await QueuedPayloadAsync(world, job);
        var broken = payload with { Sections = ["test_plan", "not_registered"] };

        var error = await Should.ThrowAsync<ReportBuildException>(() => PublishAsync(world, broken));

        error.Message.ShouldStartWith(ReportBuildException.Failed + ":");
        (await fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.test_report where job_id = '{job:D}'")).ShouldBe(0);
        var history = await world.Client.SendAsync(HttpMethod.Get, ReportPath(world.Project), Caller(world));
        history.Status.ShouldBe(HttpStatusCode.OK, history.ErrorMessage);
        history.Body.GetProperty("items").GetArrayLength().ShouldBe(0);
        await PublishAsync(world, payload);
        (await DetailAsync(world, job)).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("en", "No manual execution recorded")]
    [InlineData("vi", "Chưa ghi nhận lượt thực thi thủ công nào")]
    public async Task Build_ExecutionOnlyThroughProductionRegistration_ShowsNoCountsOrUnselectedSections(string language, string emptyNote)
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var head = await world.CreatedAsync(world.Create());
        var job = await CreateAsync(world, head.RequirementId, ["execution"], language);

        await PublishAsync(world, await QueuedPayloadAsync(world, job));

        var report = await DetailAsync(world, job);
        report.Body.GetProperty("sections").EnumerateArray().Select(section => section.GetString()).ShouldBe(["execution"]);
        report.Body.GetProperty("payload").GetProperty("sections").GetArrayLength().ShouldBe(1);
        var section = Section(report, "execution");
        section.GetProperty("release_decision").GetProperty("state").GetString().ShouldBe("not_applicable");
        section.GetProperty("release_decision").TryGetProperty("decision", out _).ShouldBeFalse();
        section.GetProperty("manual").GetProperty("note").GetString().ShouldBe(emptyNote);
        section.GetProperty("manual").TryGetProperty("counts", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Store_SnapshotAtTheAdvertisedPayloadByteLimit_PublishesLikeTheSmallerControl()
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var payload = new ReportJobPayload(
            world.Org, world.Project, Guid.CreateVersion7(), null,
            ReportScopeKinds.FeatureNodes, [ReportSectionFixture.Feature], ["traceability"], "en", null, null);
        await PublishAsync(world, payload, QAReportBoundaryTests.SnapshotWithBytes(1024));
        var edgePayload = payload with { JobId = Guid.CreateVersion7() };

        await PublishAsync(world, edgePayload, QAReportBoundaryTests.SnapshotWithBytes(TestReportLimits.MaxPayloadBytes));

        (await fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.test_report where job_id = '{edgePayload.JobId:D}'")).ShouldBe(1);
        // The builder's measure is the table check's: the stored row is exactly at the advertised limit.
        (await fixture.ScalarAsync<int>(world.Database, $"select octet_length(payload::text) from public.test_report where job_id = '{edgePayload.JobId:D}'"))
            .ShouldBe(TestReportLimits.MaxPayloadBytes);
    }

    [Fact]
    public async Task TaskHandler_MissingOrMismatchedTenantScope_RefusesBeforePublication()
    {
        await using var world = await ReportTestWorld.StartAsync(fixture);
        var head = await world.CreatedAsync(world.Create());
        var job = await CreateAsync(world, head.RequirementId, ["traceability"]);
        var payload = await QueuedPayloadAsync(world, job);
        var handler = new TestReportTaskHandler(world.Api.Services.GetRequiredService<IServiceScopeFactory>());
        var token = TestContext.Current.CancellationToken;
        await using var services = world.Api.Services.CreateAsyncScope();
        await using (var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, world.Project, false, token))
        {

            await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleAsync(payload, null, token).AsTask());
            var mismatched = payload with { OrgId = Guid.CreateVersion7() };
            await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleAsync(mismatched, scope, token).AsTask());
            (await fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.test_report where job_id = '{job:D}'")).ShouldBe(0);

            await handler.HandleAsync(payload, scope, token);
            await scope.CommitAsync(token);
        }
        (await DetailAsync(world, job)).Status.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task EditSourceAsync(RequirementTestWorld world, Guid requirement, Guid manualCase)
    {
        var path = $"/v1/projects/{world.Project:D}/authoring/requirements/{requirement:D}";
        var original = await world.Client.SendAsync(HttpMethod.Get, path, Caller(world));
        original.Status.ShouldBe(HttpStatusCode.OK, original.ErrorMessage);
        var edited = await world.Client.SendAsync(
            HttpMethod.Patch, path, Caller(world), new { title = "Changed requirement", body = "Changed requirement body" }, NewKey(), original.ETag);
        edited.Status.ShouldBe(HttpStatusCode.OK, edited.ErrorMessage);
        edited.Body.GetProperty("version_no").GetInt32().ShouldBe(2);
        await world.Fixture.ExecuteAsync(world.Database, $$"""
            update public.manual_test_case
               set title = 'Changed manual case',
                   steps = '{"steps":[{"no":1,"action":"Changed action","data":"Changed data","expected":"Changed expectation"}]}'
             where id = '{{manualCase:D}}';
            """);
    }

    private static async Task<Guid> CreateAsync(RequirementTestWorld world, Guid requirement, string[] sections, string language = "en")
    {
        var accepted = await world.Client.SendAsync(HttpMethod.Post, ReportPath(world.Project), Caller(world), new
        {
            scope_kind = ReportScopeKinds.Requirements,
            selected_ids = new[] { requirement },
            sections,
            language,
        }, NewKey());
        accepted.Status.ShouldBe(HttpStatusCode.Accepted, accepted.ErrorMessage);
        return accepted.Id("job_id");
    }

    private static async Task PublishAsync(RequirementTestWorld world, ReportJobPayload payload, ReportSnapshot? snapshot = null)
    {
        var token = TestContext.Current.CancellationToken;
        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(payload.OrgId, payload.ProjectId, false, token);
        if (snapshot is null)
        {
            await ActivatorUtilities.CreateInstance<TestReportBuildJob>(services.ServiceProvider).RunAsync(payload, token);
        }
        else
        {
            (await services.ServiceProvider.GetRequiredService<TestReportStore>().InsertAsync(payload, snapshot, token)).ShouldBeTrue();
        }

        await scope.CommitAsync(token);
    }

    private static async Task<ApiAnswer> DetailAsync(RequirementTestWorld world, Guid job)
    {
        var report = await world.Fixture.ScalarAsync<Guid>(world.Database, $"select id from public.test_report where job_id = '{job:D}'");
        var answer = await world.Client.SendAsync(HttpMethod.Get, ReportPath(world.Project, $"/{report:D}"), Caller(world));
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        return answer;
    }

    private static JsonElement Section(ApiAnswer report, string id) => report.Body.GetProperty("payload").GetProperty("sections")
        .EnumerateArray().Single(section => section.GetProperty("id").GetString() == id).GetProperty("data");

    private static Task<string> PinsAsync(RequirementTestWorld world, Guid job) => world.Fixture.ScalarAsync<string>(
        world.Database, $"select pinned_versions::text from public.test_report where job_id = '{job:D}'");
}
