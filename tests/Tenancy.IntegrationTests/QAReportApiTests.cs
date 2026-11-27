using System.Net;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.Modules.Authoring.Application.Reports;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;
using static Tenancy.IntegrationTests.ReportTestWorld;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class QAReportApiTests(PostgresTenancyFixture fixture)
{
    [Theory]
    [InlineData("feature_nodes")]
    [InlineData("requirements")]
    public async Task Create_MixedLiveAndSiblingProjectSelections_RefusesTheWholeRequestWithoutQueuing(string scopeKind)
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var (own, sibling) = await SelectionsAsync(world, scopeKind);
        var before = await OutboxCountAsync(world);

        var denied = await world.Client.SendAsync(HttpMethod.Post, ReportPath(world.Project), Caller(world), Body(scopeKind, [own, sibling]), NewKey());

        denied.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, denied.ErrorMessage);
        var detail = denied.Body.GetProperty("error").GetProperty("details")[0];
        detail.GetProperty("path").GetString().ShouldBe("/selected_ids");
        detail.GetProperty("reason").GetString().ShouldBe("unknown_selection");
        (await OutboxCountAsync(world)).ShouldBe(before);
        (await fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.test_report where org_id = '{world.Org:D}'")).ShouldBe(0);

        var accepted = await world.Client.SendAsync(HttpMethod.Post, ReportPath(world.Project), Caller(world), Body(scopeKind, [own]), NewKey());
        accepted.Status.ShouldBe(HttpStatusCode.Accepted, accepted.ErrorMessage);
        (await OutboxCountAsync(world)).ShouldBe(before + 1);
    }

    [Fact]
    public async Task History_ReportsHaveTheSameTimestamp_VisitsEveryIdOnceAndTerminates()
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var ids = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
        foreach (var id in ids)
        {
            await fixture.ExecuteAsync(world.Database, $$"""
                insert into public.test_report
                    (id, org_id, project_id, job_id, scope_kind, selected_ids, sections, language,
                     schema_version, pinned_versions, payload, content_hash, created_at)
                values ('{{id:D}}', '{{world.Org:D}}', '{{world.Project:D}}', '{{Guid.CreateVersion7():D}}',
                        'feature_nodes', '["{{ReportSectionFixture.Feature:D}}"]', '["traceability"]', 'en',
                        1, '{}', '{}', '{{CanonicalHash.Compute("{}")}}', '2026-11-27T00:00:00Z');
                """);
        }

        var expected = ids.OrderDescending().ToArray();
        string? cursor = null;
        var visited = new List<Guid>();
        for (var page = 0; page < expected.Length; page++)
        {
            var suffix = cursor is null ? "?limit=1" : "?limit=1&cursor=" + Uri.EscapeDataString(cursor);
            var answer = await world.Client.SendAsync(HttpMethod.Get, ReportPath(world.Project) + suffix, Caller(world));
            answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
            var row = answer.Body.GetProperty("items").EnumerateArray().Single();
            row.GetProperty("report_id").GetGuid().ShouldBe(expected[page]);
            row.TryGetProperty("payload", out _).ShouldBeFalse();
            visited.Add(row.GetProperty("report_id").GetGuid());
            cursor = answer.Body.GetProperty("next_cursor").GetString();
            if (page < expected.Length - 1)
            {
                cursor.ShouldNotBeNullOrEmpty();
            }
        }

        visited.ShouldBe(expected);
        visited.Distinct().Count().ShouldBe(ids.Length);
        cursor.ShouldBeNull();
        var sibling = await world.Client.SendAsync(HttpMethod.Get, ReportPath(world.OtherProject), Caller(world));
        sibling.Status.ShouldBe(HttpStatusCode.OK, sibling.ErrorMessage);
        sibling.Body.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Theory]
    [InlineData("report_too_large")]
    [InlineData("build_failed")]
    public async Task Job_DeadLetterQueueRow_ReturnsOnlyTheStableReasonAndNoReport(string reason)
    {
        await using var world = await RequirementTestWorld.StartAsync(fixture);
        var head = await world.CreatedAsync(world.Create());
        var created = await world.Client.SendAsync(
            HttpMethod.Post, ReportPath(world.Project), Caller(world), Body(ReportScopeKinds.Requirements, [head.RequirementId]), NewKey());
        created.Status.ShouldBe(HttpStatusCode.Accepted, created.ErrorMessage);
        var job = created.Id("job_id");
        var path = ReportPath(world.Project, $"/jobs/{job:D}");
        var pending = await world.Client.SendAsync(HttpMethod.Get, path, Caller(world));
        pending.Status.ShouldBe(HttpStatusCode.OK, pending.ErrorMessage);
        pending.Text("state").ShouldBe("pending");

        await fixture.ExecuteAsync(world.Database, $"""
            insert into public.task
                (id, org_id, queue, task_type, payload, idempotency_key, state, attempts, max_attempts, last_error)
            select '{Guid.CreateVersion7():D}', org_id, 'report-outcome', task_type, payload,
                   'outbox:' || id::text, 'dead_letter', 1, 1,
                   'ReportBuildException: {reason}: internal build diagnostic'
              from public.outbox
             where org_id = '{world.Org:D}' and idempotency_key = '{TestReportTasks.OutboxKey(world.Project, job)}';
            update public.outbox set dispatched_at = now()
             where org_id = '{world.Org:D}' and idempotency_key = '{TestReportTasks.OutboxKey(world.Project, job)}';
            """);

        var failed = await world.Client.SendAsync(HttpMethod.Get, path, Caller(world));
        failed.Status.ShouldBe(HttpStatusCode.OK, failed.ErrorMessage);
        failed.Text("state").ShouldBe("failed");
        failed.Text("failure_reason").ShouldBe(reason);
        failed.Body.GetProperty("report_id").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        failed.Body.GetRawText().ShouldNotContain("internal build diagnostic");
        (await fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.test_report where job_id = '{job:D}'")).ShouldBe(0);
        var history = await world.Client.SendAsync(HttpMethod.Get, ReportPath(world.Project), Caller(world));
        history.Status.ShouldBe(HttpStatusCode.OK, history.ErrorMessage);
        history.Body.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    private static object Body(string scopeKind, Guid[] ids) => new
    {
        scope_kind = scopeKind,
        selected_ids = ids,
        sections = new[] { "traceability" },
        language = "en",
    };

    private static async Task<(Guid Own, Guid Sibling)> SelectionsAsync(RequirementTestWorld world, string scopeKind)
    {
        if (scopeKind == ReportScopeKinds.Requirements)
        {
            var ownRequirement = await world.CreatedAsync(world.Create());
            var siblingRequirement = await world.CreatedAsync(world.Create(project: world.OtherProject));
            return (ownRequirement.RequirementId, siblingRequirement.RequirementId);
        }

        var own = Guid.CreateVersion7();
        var sibling = Guid.CreateVersion7();
        await world.Fixture.ExecuteAsync(world.Database, $"""
            insert into public.feature_node (id, org_id, project_id, kind, title, position)
            values ('{own:D}', '{world.Org:D}', '{world.Project:D}', 'module', 'Own', 0),
                   ('{sibling:D}', '{world.Org:D}', '{world.OtherProject:D}', 'module', 'Sibling', 0);
            """);
        return (own, sibling);
    }

    private static Task<long> OutboxCountAsync(RequirementTestWorld world) => world.Fixture.ScalarAsync<long>(
        world.Database, $"select count(*) from public.outbox where org_id = '{world.Org:D}' and task_type = '{TestReportTasks.TaskType}'");
}
