// The kill row is the schedule of an accepted cancel, so where it lands decides whether a Worker ever fires it. The Api
// composes the producer half of the queue (options and routing, never a consumer), and these tests boot that production
// composition with a non-default deployment routing and read the row the real route wrote.
using System.Net;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed class CancelKillQueueRoutingTests(PostgresTenancyFixture fixture)
{
    /// <summary>The queue a Worker polls when the deployment configures no route for the kill type.</summary>
    [Fact]
    public async Task CancelRun_NonDefaultDispatchQueue_QueuesKillThere()
    {
        await using var world = await RunCreationTests.StartAsync(
            fixture,
            configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Tasks:DispatchDefaultQueue"] = "jobs",
            });

        var runId = await CancelRunTestData.RunInStateAsync(fixture, world, "running");
        (await CancelRunTestData.CancelAsync(world, runId)).Status.ShouldBe(HttpStatusCode.Accepted);

        (await KillQueueAsync(world.Database, runId)).ShouldBe(
            "jobs",
            "an unrouted kill follows EVIDGATE__TASKS__DISPATCHDEFAULTQUEUE; 'default' is not polled by this deployment");
    }

    /// <summary>The route of the kill task type wins over the default queue, the same rule the outbox dispatcher applies.</summary>
    [Fact]
    public async Task CancelRun_QueueRouteForKillType_WinsOverDefaultQueue()
    {
        await using var world = await RunCreationTests.StartAsync(
            fixture,
            configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Tasks:DispatchDefaultQueue"] = "jobs",
                ["EVIDGATE:Tasks:QueueRoutes:0:TaskType"] = CancelKillContract.TaskType,
                ["EVIDGATE:Tasks:QueueRoutes:0:Queue"] = "kills",
            });

        var runId = await CancelRunTestData.RunInStateAsync(fixture, world, "running");
        (await CancelRunTestData.CancelAsync(world, runId)).Status.ShouldBe(HttpStatusCode.Accepted);

        (await KillQueueAsync(world.Database, runId)).ShouldBe(
            "kills",
            "EVIDGATE__TASKS__QUEUEROUTES__0 names the kill type's queue, and the default queue must not override it");
    }

    /// <summary>The one kill row an accepted cancel queued for this run, by the key that names the run and the request.</summary>
    private Task<string> KillQueueAsync(string database, Guid runId) =>
        fixture.ScalarAsync<string>(
            database,
            $"select queue from public.task where task_type = '{CancelKillContract.TaskType}' "
            + $"and idempotency_key like 'orchestrator:run:{runId:D}:cancel:kill:%'");
}
