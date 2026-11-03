// QA for 05.3.1-T1 (independent of the implementer): the negative and boundary cases the implementer's suite
// does not pin. The row-lock proof the QA agent also wrote now lives in RunStateTransitionTests' two races,
// which observe the loser parked on the winner's lock in pg_stat_activity before the winner commits, so it is
// not repeated here.
#pragma warning disable CA1861
using System.Net;
using System.Net.Http;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class RunStateTransitionQaTests
{
    /// <summary>The outbox task type an applied transition writes (00 §5.3, 05.3.1 §7).</summary>
    private const string StateChangedTask = "orchestrator.run.state_changed";

    private readonly PostgresTenancyFixture _fixture;

    public RunStateTransitionQaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>A negative expected version is never equal to the stored version, so it is a stale write.</summary>
    [Fact]
    public async Task TransitionRun_NegativeExpectedVersion_IsStale412_AndNotApplied()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        var result = await TransitionAsync(world, runId, "running", expectedVersion: -1);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Status.ShouldBe(412);
        (await StateAsync(world, runId)).ShouldBe("queued");
        (await VersionAsync(world, runId)).ShouldBe(0);
        (await OutboxTargetsAsync(world, runId)).ShouldBeEmpty();
    }

    /// <summary>The vocabulary is the exact 00 §8.1 spelling; a differently cased <c>ToState</c> is not a state.</summary>
    [Fact]
    public async Task TransitionRun_ToStateIsCaseSensitive_RequestInvalid()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        var result = await TransitionAsync(world, runId, "Running");

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Status.ShouldBe(422);
        (await StateAsync(world, runId)).ShouldBe("queued");
        (await VersionAsync(world, runId)).ShouldBe(0);
    }

    /// <summary>A non-GUID <c>{r}</c> must be a client error; it must never reach the database as a 5xx.</summary>
    [Fact]
    public async Task GetRun_NonGuidId_IsAClientError_NotAServerError()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await world.Client.SendAsync(HttpMethod.Get, "/v1/runs/not-a-guid", world.Admin);

        Assert.InRange((int)answer.Status, 400, 499);
    }

    private static async Task<Guid> CreateQueuedRunAsync(RunCreationTests.World world)
    {
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id("run_id");
    }

    /// <summary>One committed transition in its own tenant transaction (the audit flush and commit only on success).</summary>
    private static async Task<Result<TransitionResult>> TransitionAsync(
        RunCreationTests.World world,
        Guid runId,
        string to,
        int? expectedVersion = null)
    {
        await using var scope = await RunTransitionScope.BeginAsync(world.Api, world.Org, world.Project);
        var result = await scope.TransitionAsync(new TransitionRunStateCommand(runId, to, TransitionActor.System, expectedVersion));

        if (result.IsSuccess)
        {
            await scope.FlushAuditAsync();
            await scope.CommitAsync();
        }

        return result;
    }

    private Task<string> StateAsync(RunCreationTests.World world, Guid runId) =>
        _fixture.ScalarAsync<string>(world.Database, $"select state from public.run where id = '{runId}'");

    private Task<int> VersionAsync(RunCreationTests.World world, Guid runId) =>
        _fixture.ScalarAsync<int>(world.Database, $"select version from public.run where id = '{runId}'");

    private Task<IReadOnlyList<string>> OutboxTargetsAsync(RunCreationTests.World world, Guid runId) =>
        _fixture.ColumnAsync<string>(
            world.Database,
            $"select payload ->> 'to' from public.outbox where aggregate_id = '{runId}' and task_type = '{StateChangedTask}' "
            + "order by created_at, id");
}
