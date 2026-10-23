using Evidgate.Modules.Identity.Ci;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// 02.4.1-T4.1 (TL T7): the daily purge removes <c>ci_token_issue</c> rows once their replay window
/// (token lifetime + 10 minutes) has closed, leaves younger rows alone, and fires once per schedule time.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class CiTokenIssuePurgeTaskTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly PostgresTasksFixture _fixture;

    public CiTokenIssuePurgeTaskTests(PostgresTasksFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Purge_DeletesOnlyRowsPastTheirReplayWindow()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await SeedAsync(owner);

        await TaskSql.SeedTaskAsync(
            owner, EvidgateSweeps.MaintenanceQueue, CiTokenIssueSweeps.TaskType, payload: """{"limit":2}""", orgId: null, idempotencyKey: "purge-1");
        await using var host = Start(database);
        (await host.Consumer.PollOnceAsync(EvidgateSweeps.MaintenanceQueue, Token)).ShouldBe(1);

        // Four expired rows, batch of two: the loop drains them (two full batches then an empty one).
        var left = await TaskSql.QueryAsync<string>(owner, "select jti from public.ci_token_issue order by jti");
        left.ShouldBe(["still-valid", "within-grace"]);
        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where idempotency_key = 'purge-1'")).ShouldBe("done");
    }

    [Fact]
    public async Task Purge_Schedule_FiresOncePerTime_AtTheDocumentedCadence()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await using var host = Start(database);
        var at = new DateTimeOffset(2026, 10, 3, 3, 20, 30, TimeSpan.Zero);

        var fired = await Task.WhenAll(host.Runner.RunOnceAsync(at, Token), host.Runner.RunOnceAsync(at, Token));
        fired.Sum().ShouldBe(1);
        (await host.Runner.RunOnceAsync(at, Token)).ShouldBe(0);
        (await TaskSql.ScalarAsync<long>(
            owner, "select count(*) from public.task where schedule_name = @n", ("n", CiTokenIssueSweeps.TaskType))).ShouldBe(1);
        CiTokenIssueSweeps.Cron.ShouldBe("20 * * * *");
    }

    private TasksTestHost Start(string database) =>
        TasksTestHost.Start(
            _fixture,
            database,
            services: services =>
            {
                services.AddTaskHandler<CiTokenIssuePurgePayload, CiTokenIssuePurgeTaskHandler>();
                services.AddTaskSchedule(CiTokenIssueSweeps.Purge);
            });

    private static async Task SeedAsync(string owner)
    {
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var binding = Guid.CreateVersion7();
        await TaskSql.ExecuteAsync(
            owner,
            """
            insert into public.organization (id, name, slug) values (@org, 'acme', 'acme');
            insert into public.project (id, org_id, name, slug) values (@project, @org, 'pp', 'pp');
            insert into public.ci_binding (id, org_id, project_id, provider, repository_id, repository_full_name, allowed_ref_patterns, allowed_workflow_refs, status)
              values (@binding, @org, @project, 'github_actions', 1, 'acme/web', array['refs/heads/main'], array['acme/web/.github/workflows/ci.yml@refs/heads/main'], 'active');
            """,
            ("org", org),
            ("project", project),
            ("binding", binding));

        foreach (var (jti, expires) in new[]
                 {
                     ("old-1", "now() - interval '2 hours'"),
                     ("old-2", "now() - interval '3 hours'"),
                     ("old-3", "now() - interval '1 day'"),
                     ("old-4", "now() - interval '11 minutes'"),
                     ("within-grace", "now() - interval '5 minutes'"),
                     ("still-valid", "now() + interval '30 minutes'"),
                 })
        {
            await TaskSql.ExecuteAsync(
                owner,
                $"""
                insert into public.ci_token_issue (id, org_id, project_id, binding_id, provider, jti, repository_id, repository_full_name, ref, sha, run_id, run_attempt, job_workflow_ref, expires_at)
                values (@id, @org, @project, @binding, 'github_actions', @jti, 1, 'acme/web', 'refs/heads/main', 'abc', 1, 1, 'wf', {expires})
                """,
                ("id", Guid.CreateVersion7()),
                ("org", org),
                ("project", project),
                ("binding", binding),
                ("jti", jti));
        }
    }
}
