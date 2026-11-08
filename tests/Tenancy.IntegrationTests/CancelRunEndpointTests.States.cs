// Every source state under a repeated cancel, concurrent distinct keys, and the deny cases that must leave the
// queue untouched.
#pragma warning disable CA1861
using System.Net;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class CancelRunEndpointTests
{
    /// <summary>
    /// A repeated cancel of one run answers <c>202</c> both times, records one request row and queues one kill for a
    /// non-terminal source state, and preserves the terminal run — no row, no version bump, no kill — whichever
    /// terminal state it reached.
    /// </summary>
    [Theory]
    [InlineData("created", false)]
    [InlineData("queued", false)]
    [InlineData("running", false)]
    [InlineData("collecting", false)]
    [InlineData("complete", true)]
    [InlineData("incomplete", true)]
    [InlineData("canceled", true)]
    public async Task CancelRun_EveryState_RepeatedCallPreservesSingleSchedule(string state, bool terminal)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var run = await CancelRunTestData.RunInStateAsync(_fixture, world, state);
        var version = await CancelRunTestData.VersionAsync(_fixture, world, run);
        (await CancelRunTestData.CancelAsync(world, run)).Status.ShouldBe(HttpStatusCode.Accepted);
        (await CancelRunTestData.CancelAsync(world, run, reason: "second caller")).Status.ShouldBe(HttpStatusCode.Accepted);
        (await CancelRunTestData.StateAsync(_fixture, world, run)).ShouldBe(terminal ? state : "canceled");
        (await CancelRunTestData.VersionAsync(_fixture, world, run)).ShouldBe(version + (terminal ? 0 : 1));
        (await CancelRunTestData.RowCountAsync(_fixture, world, run)).ShouldBe(terminal ? 0 : 1);
        (await KillCountAsync(world, run)).ShouldBe(terminal ? 0 : 1);
    }

    /// <summary>
    /// Eight callers with distinct idempotency keys race the same run; the request row's unique key is the
    /// arbiter, so all eight accept and exactly one cancel row and one kill row exist.
    /// </summary>
    [Fact]
    public async Task CancelRun_ConcurrentDistinctKeys_QueuesOneKill()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var run = await CancelRunTestData.RunInStateAsync(_fixture, world, "running");
        var calls = Enumerable.Range(0, 8)
            .Select(index => CancelRunTestData.CancelAsync(world, run, reason: $"caller {index}"))
            .ToArray();
        foreach (var response in await Task.WhenAll(calls))
        {
            response.Status.ShouldBe(HttpStatusCode.Accepted, response.ErrorMessage);
        }

        (await CancelRunTestData.StateAsync(_fixture, world, run)).ShouldBe("canceled");
        (await CancelRunTestData.RowCountAsync(_fixture, world, run)).ShouldBe(1);
        (await KillCountAsync(world, run)).ShouldBe(1);
    }

    /// <summary>
    /// A viewer without <c>run:cancel</c>, another tenant's run and an unknown id all leave the run untouched
    /// and queue nothing; the member cancel that follows is the positive control.
    /// </summary>
    [Fact]
    public async Task CancelRun_ForeignTenantOrViewer_DoesNotEnqueueKill()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var run = await CancelRunTestData.RunInStateAsync(_fixture, world, "running");
        var viewer = await AddMemberAsync(_fixture, world.Database, world.Org, "viewer");
        (await CancelRunTestData.CancelAsync(world, run,
            TenancyApiFactory.Resolved(user: viewer, orgHeader: world.Org))).Status.ShouldBe(HttpStatusCode.Forbidden);
        var foreignUser = Guid.CreateVersion7();
        var foreignOrg = await CreateOrgAsync(world.Client, foreignUser, Unique.Slug("cancel-foreign"));
        var denied = await CancelRunTestData.CancelAsync(world, run,
            TenancyApiFactory.Resolved(user: foreignUser, orgHeader: foreignOrg));
        var unknown = await CancelRunTestData.CancelAsync(world, Guid.CreateVersion7());
        denied.Status.ShouldBe(HttpStatusCode.NotFound);
        CancelRunTestData.Envelope(denied).ShouldBe(CancelRunTestData.Envelope(unknown));
        (await KillCountAsync(world, run)).ShouldBe(0);
        (await CancelRunTestData.RowCountAsync(_fixture, world, run)).ShouldBe(0);
        (await CancelRunTestData.StateAsync(_fixture, world, run)).ShouldBe("running");
        (await CancelRunTestData.CancelAsync(world, run)).Status.ShouldBe(HttpStatusCode.Accepted);
        (await KillCountAsync(world, run)).ShouldBe(1);
    }

    private Task<long> KillCountAsync(RunCreationTests.World world, Guid run) => _fixture.ScalarAsync<long>(
        world.Database,
        $"select count(*) from public.task where org_id = '{world.Org:D}' and task_type = '{CancelKillContract.TaskType}' "
        + $"and payload->>'run_id' = '{run:D}'");
}
