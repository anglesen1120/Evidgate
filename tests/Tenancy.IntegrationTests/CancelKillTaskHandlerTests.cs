// The kill handler behind the real consumer: an early claim, a request the tenant cannot see, a queue claim before the
// request's own deadline, a restart under an active lease, and a deleted organization.
using System.Net;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class CancelKillTaskHandlerTests(PostgresTenancyFixture fixture)
{
    [Fact]
    public async Task CancelKillTask_EarlyClaim_RetainsRequestThenKillsOnceAtDeadline()
    {
        await using var database = await fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(database, "00:00:02");
        var run = await CancelRunTestData.RunInStateAsync(fixture, world, "running");
        (await CancelRunTestData.CancelAsync(world, run)).Status.ShouldBe(HttpStatusCode.Accepted);
        var request = await RequestAsync(world, run);
        (await Consumer(world).PollOnceAsync("default", TestContext.Current.CancellationToken)).ShouldBe(0);
        var exception = await Should.ThrowAsync<InvalidOperationException>(() => InvokeAsync(world, world.Org, run, request));
        exception.Message.ShouldContain("claimed before kill_at");
        (await CancelRunTestData.RowAsync(fixture, world, run)).State.ShouldBe("requested");
        await WaitDueAsync(world, request);
        (await Consumer(world).PollOnceAsync("default", TestContext.Current.CancellationToken)).ShouldBe(1);
        (await CancelRunTestData.RowAsync(fixture, world, run)).State.ShouldBe("killed");
        var version = await RequestVersionAsync(world, request);
        await InvokeAsync(world, world.Org, run, request);
        (await RequestVersionAsync(world, request)).ShouldBe(version);
        (await Consumer(world).PollOnceAsync("default", TestContext.Current.CancellationToken)).ShouldBe(0);
        (await fixture.ScalarAsync<int>(database,
            $"select attempts from public.task where org_id = '{world.Org:D}' and task_type = '{CancelKillContract.TaskType}'"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task CancelKillTask_InvisibleOrMissingRequest_NoOpWithOwnerPositiveControl()
    {
        await using var database = await fixture.CreateDedicatedDatabaseAsync();
        await using var owner = await StartAsync(database, "00:00:01");
        await using var other = await StartAsync(database, "00:00:01");
        var run = await CancelRunTestData.RunInStateAsync(fixture, owner, "running");
        (await CancelRunTestData.CancelAsync(owner, run)).Status.ShouldBe(HttpStatusCode.Accepted);
        var request = await RequestAsync(owner, run);
        await WaitDueAsync(owner, request);
        var version = await RequestVersionAsync(owner, request);
        await InvokeAsync(owner, other.Org, run, request);
        await InvokeAsync(owner, Guid.CreateVersion7(), run, request);
        await InvokeAsync(owner, owner.Org, run, Guid.CreateVersion7());
        (await CancelRunTestData.RowAsync(fixture, owner, run)).State.ShouldBe("requested");
        (await RequestVersionAsync(owner, request)).ShouldBe(version);
        await InvokeAsync(owner, owner.Org, run, request);
        (await CancelRunTestData.RowAsync(fixture, owner, run)).State.ShouldBe("killed");
    }

    [Fact]
    public async Task CancelKillTask_QueueClaimsBeforeRequestDeadline_RetriesWithoutKilling()
    {
        await using var database = await fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(database, "01:00:00");
        var run = await CancelRunTestData.RunInStateAsync(fixture, world, "running");
        (await CancelRunTestData.CancelAsync(world, run)).Status.ShouldBe(HttpStatusCode.Accepted);
        await fixture.ExecuteAsync(database,
            $"update public.task set available_at = now() where org_id = '{world.Org:D}' "
            + $"and task_type = '{CancelKillContract.TaskType}'");
        (await Consumer(world).PollOnceAsync("default", TestContext.Current.CancellationToken)).ShouldBe(1);
        (await CancelRunTestData.RowAsync(fixture, world, run)).State.ShouldBe("requested");
        (await fixture.ScalarAsync<string>(database,
            $"select state from public.task where org_id = '{world.Org:D}' and task_type = '{CancelKillContract.TaskType}'"))
            .ShouldBe("retry");
        (await fixture.ScalarAsync<int>(database,
            $"select attempts from public.task where org_id = '{world.Org:D}' and task_type = '{CancelKillContract.TaskType}'"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task CancelKillTask_RestartWithActiveLease_ReclaimsPersistedRowOnlyOnce()
    {
        await using var database = await fixture.CreateDedicatedDatabaseAsync();
        Guid run;
        Guid org;
        Guid task;
        await using (var before = await StartAsync(database, "00:00:01"))
        {
            org = before.Org;
            run = await CancelRunTestData.RunInStateAsync(fixture, before, "running");
            (await CancelRunTestData.CancelAsync(before, run)).Status.ShouldBe(HttpStatusCode.Accepted);
            await WaitDueAsync(before, await RequestAsync(before, run));
            var claims = await before.Api.Services.GetRequiredService<ITaskClaimer>()
                .ClaimAsync("default", [CancelKillContract.TaskType], 1, TestContext.Current.CancellationToken);
            var claim = claims.ShouldHaveSingleItem();
            claim.Attempts.ShouldBe(1);
            claim.OrgId.ShouldBe(org);
            task = claim.Id;
        }

        (await fixture.ScalarAsync<string>(database,
            $"select state from public.task where id = '{task:D}'")).ShouldBe("active");
        await fixture.ExecuteAsync(database,
            $"update public.task set locked_until = now() - interval '1 second' where id = '{task:D}'");
        await using var after = await StartAsync(database, "00:00:01");
        (await Consumer(after).PollOnceAsync("default", TestContext.Current.CancellationToken)).ShouldBe(1);
        (await CancelRunTestData.RowAsync(fixture, database, org, run)).State.ShouldBe("killed");
        (await fixture.ScalarAsync<string>(database,
            $"select state from public.task where id = '{task:D}'")).ShouldBe("done");
        (await fixture.ScalarAsync<int>(database,
            $"select attempts from public.task where id = '{task:D}'")).ShouldBe(2);
        (await Consumer(after).PollOnceAsync("default", TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task CancelKillTask_DeletedOrganization_CompletesScopedCleanupWithoutReopeningRun()
    {
        await using var database = await fixture.CreateDedicatedDatabaseAsync();
        await using var owner = await StartAsync(database, "00:00:01");
        await using var other = await StartAsync(database, "00:00:01");
        var run = await CancelRunTestData.RunInStateAsync(fixture, owner, "running");
        (await CancelRunTestData.CancelAsync(owner, run)).Status.ShouldBe(HttpStatusCode.Accepted);
        var request = await RequestAsync(owner, run);
        await fixture.ExecuteAsync(database,
            $"update public.organization set status = 'deleted' where id = '{owner.Org:D}'");
        await WaitDueAsync(owner, request);
        await InvokeAsync(owner, other.Org, run, request);
        (await CancelRunTestData.RowAsync(fixture, owner, run)).State.ShouldBe("requested");
        await InvokeAsync(owner, owner.Org, run, request);
        (await CancelRunTestData.RowAsync(fixture, owner, run)).State.ShouldBe("killed");
        (await CancelRunTestData.StateAsync(fixture, owner, run)).ShouldBe("canceled");
    }

    private Task<RunCreationTests.World> StartAsync(string database, string kill) => RunCreationTests.StartAsync(
        fixture, database: database,
        configuration: CancelRunKillScheduleTests.Windows("00:00:00.001", kill),
        services: services => services.AddEvidgateTasks(new ConfigurationBuilder().Build()));

    private static TaskConsumer Consumer(RunCreationTests.World world) => world.Api.Services.GetRequiredService<TaskConsumer>();

    private Task<Guid> RequestAsync(RunCreationTests.World world, Guid run) => fixture.ScalarAsync<Guid>(world.Database,
        $"select id from public.run_cancel_request where org_id = '{world.Org:D}' and run_id = '{run:D}'");

    private Task<int> RequestVersionAsync(RunCreationTests.World world, Guid request) => fixture.ScalarAsync<int>(world.Database,
        $"select version from public.run_cancel_request where org_id = '{world.Org:D}' and id = '{request:D}'");

    private static async Task InvokeAsync(RunCreationTests.World world, Guid org, Guid run, Guid request)
    {
        var token = TestContext.Current.CancellationToken;
        var invoker = world.Api.Services.GetServices<ITaskHandlerInvoker>().Single(x => x.TaskType == CancelKillContract.TaskType);
        var factory = world.Api.Services.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await factory.BeginAsync(org, cancellationToken: token);
        await invoker.InvokeAsync(CancelKillContract.PayloadJson(run, request), scope, token);
        await scope.CommitAsync(token);
    }

    private async Task WaitDueAsync(RunCreationTests.World world, Guid request)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (!await fixture.ScalarAsync<bool>(world.Database,
            $"select now() >= kill_at from public.run_cancel_request where id = '{request:D}'"))
        {
            await Task.Delay(20, deadline.Token);
        }
    }
}
