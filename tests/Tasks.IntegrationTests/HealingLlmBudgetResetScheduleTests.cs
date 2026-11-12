using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Healing.Application;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// The <c>llm-budget-reset</c> schedule through the real task path: the runner inserts one platform task per cron
/// occurrence, the consumer runs the registered handler, and the handler calls the definer as the sweep role.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class HealingLlmBudgetResetScheduleTests(PostgresTasksFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Two runners racing for one occurrence insert one task, a later pass of the same occurrence inserts none, and
    /// the one task rolls the budget once: one new row, one audit row. The next day's occurrence is a new fire.
    /// </summary>
    [Fact]
    public async Task LlmBudgetReset_UniqueFireKey_OnePerPeriod()
    {
        var database = await fixture.CreateDatabaseAsync();
        var owner = fixture.OwnerConnectionString(database);
        await using var host = Start(database);
        var project = await SeedYesterdaysBudgetAsync(owner);

        // Just after the registered daily fire (00:05 UTC).
        var at = new DateTimeOffset(2026, 10, 26, 0, 5, 30, TimeSpan.Zero);
        var fired = await Task.WhenAll(host.Runner.RunOnceAsync(at, Token), host.Runner.RunOnceAsync(at, Token));
        fired.Sum().ShouldBe(1, "two runners for one occurrence insert one task");
        (await host.Runner.RunOnceAsync(at, Token)).ShouldBe(0, "a later pass of the same occurrence inserts none");

        (await TaskSql.ScalarAsync<string>(
            owner,
            "select task_type from public.task where schedule_name = @n",
            ("n", HealingLlmBudgetResetSweeps.ScheduleName))).ShouldBe(HealingLlmBudgetResetSweeps.TaskType);
        (await TaskSql.ScalarAsync<bool>(
            owner,
            "select org_id is null from public.task where schedule_name = @n",
            ("n", HealingLlmBudgetResetSweeps.ScheduleName))).ShouldBeTrue("the roll spans every org: a platform task");

        (await host.Consumer.PollOnceAsync(EvidgateSweeps.MaintenanceQueue, Token)).ShouldBe(1);
        (await TaskSql.ScalarAsync<string>(
            owner,
            "select state from public.task where schedule_name = @n",
            ("n", HealingLlmBudgetResetSweeps.ScheduleName))).ShouldBe("done");

        (await RowCountAsync(owner, project)).ShouldBe(2, "yesterday's row plus the one the roll opened");
        (await TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.platform_sweep_audit where action = 'healing.llm_budget_reset'"))
            .ShouldBe(1);

        (await host.Runner.RunOnceAsync(at.AddDays(1), Token)).ShouldBe(1, "the next day's occurrence is its own fire");
    }

    private TasksTestHost Start(string database) =>
        TasksTestHost.Start(
            fixture,
            database,
            services: services =>
            {
                services.AddTaskHandler<HealingLlmBudgetResetPayload, HealingLlmBudgetResetTaskHandler>();
                services.AddTaskSchedule(HealingLlmBudgetResetSweeps.Reset);
            });

    private static async Task<Guid> SeedYesterdaysBudgetAsync(string owner)
    {
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        await TaskSql.ExecuteAsync(
            owner,
            """
            insert into public.organization (id, name, slug) values (@org, 'roll-org', 'roll-org');
            insert into public.project (id, org_id, name, slug) values (@project, @org, 'roll-pp', 'roll-pp');
            insert into public.healing_llm_budget
              (id, org_id, project_id, period, period_start, period_cap_cost, period_cap_tokens)
            values (@id, @org, @project, 'daily', (now() at time zone 'utc')::date - 1, 1.0, 5000);
            """,
            ("org", org),
            ("project", project),
            ("id", Guid.CreateVersion7()));
        return project;
    }

    private static Task<long> RowCountAsync(string owner, Guid project) =>
        TaskSql.ScalarAsync<long>(
            owner,
            "select count(*) from public.healing_llm_budget where project_id = @p",
            ("p", project));
}
