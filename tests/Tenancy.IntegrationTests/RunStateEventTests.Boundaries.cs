using System.Net;
using System.Text.Json;
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class RunStateEventTests
{
    /// <summary>
    /// Each edge of the one chain the state machine declares, committed one transaction at a time, so the history names
    /// every applied edge in order with no sequence gap. One run can take at most three further edges
    /// (<c>queued → running → collecting → complete</c>); the creation row plus those three is its whole history.
    /// </summary>
    [Fact]
    public async Task CommittedStateChanges_RecordEveryEdgeWithoutSequenceGaps()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);
        var expected = new List<string> { "(insert)->queued" };
        var previous = "queued";
        foreach (var target in new[] { "running", "collecting", "complete" })
        {
            await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
            (await scope.ExecuteAsync($"update public.run set state = '{target}' where id = '{runId:D}'")).ShouldBe(1);
            await scope.CommitAsync();
            expected.Add($"{previous}->{target}");
            previous = target;
        }

        (await SequencesAsync(world, runId)).ShouldBe(Enumerable.Range(1, expected.Count));
        (await HistoryAsync(world, runId)).ShouldBe(string.Join(',', expected));
    }

    [Fact]
    public async Task ConcurrentSameEdge_RecordsOneAppliedTransitionAndNineNoOps()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = 0;
        var noOps = 0;
        var writers = Enumerable.Range(0, 10).Select(_ => Detached(async () =>
        {
            await start.Task.WaitAsync(TestContext.Current.CancellationToken);
            var result = await TransitionAsync(world, runId, "running");
            result.IsSuccess.ShouldBeTrue(result.Error?.Message);
            result.Value.State.ShouldBe("running");
            if (result.Value.Outcome == TransitionOutcome.Applied)
            {
                Interlocked.Increment(ref applied);
            }
            else
            {
                result.Value.Outcome.ShouldBe(TransitionOutcome.NoOp);
                Interlocked.Increment(ref noOps);
            }
        }, TestContext.Current.CancellationToken)).ToArray();
        start.SetResult();
        await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        applied.ShouldBe(1);
        noOps.ShouldBe(9);
        (await SequencesAsync(world, runId)).ShouldBe(Enumerable.Range(1, 2));
        (await HistoryAsync(world, runId)).ShouldBe("(insert)->queued,queued->running");
    }

    [Fact]
    public async Task StaleExpectedVersion_DoesNotAppendHistory()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await RunningRunAsync(world);
        var before = await HistoryAsync(world, runId);
        await using (var scope = await RunTransitionScope.BeginAsync(world.Api, world.Org, world.Project))
        {
            var result = await scope.TransitionAsync(new TransitionRunStateCommand(runId, "collecting", ExpectedVersion: 0));
            result.IsSuccess.ShouldBeFalse();
            result.Error.ShouldNotBeNull().Status.ShouldBe(412);
        }

        (await HistoryAsync(world, runId)).ShouldBe(before);
        (await TransitionAsync(world, runId, "collecting")).Value.Outcome.ShouldBe(TransitionOutcome.Applied);
        (await RowCountAsync(world, runId)).ShouldBe(3);
    }

    [Fact]
    public async Task RolledBackTransition_DiscardsItsHistoryAlongsideStateAuditAndOutbox()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);
        await using (var scope = await RunTransitionScope.BeginAsync(world.Api, world.Org, world.Project))
        {
            (await scope.TransitionAsync(new TransitionRunStateCommand(runId, "running"))).IsSuccess.ShouldBeTrue();
            await scope.FlushAuditAsync();
            (await scope.ScalarAsync<long>($"select count(*) from public.run_state_event where run_id = '{runId:D}'"))
                .ShouldBe(2);
            (await RowCountAsync(world, runId)).ShouldBe(1);
        }

        (await HistoryAsync(world, runId)).ShouldBe("(insert)->queued");
        (await _fixture.ScalarAsync<string>(world.Database, $"select state from public.run where id = '{runId:D}'"))
            .ShouldBe("queued");
        (await _fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.outbox where aggregate_id = '{runId:D}' and task_type = 'orchestrator.run.state_changed'"))
            .ShouldBe(0);
        (await _fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.audit_log where object_id = '{runId:D}' and action = 'run.state_changed'"))
            .ShouldBe(0);
        (await TransitionAsync(world, runId, "running")).IsSuccess.ShouldBeTrue();
        (await SequencesAsync(world, runId)).ShouldBe(Enumerable.Range(1, 2));
    }

    [Fact]
    public async Task RunCreatedWithoutCapture_StaysUnknownAfterItsFirstRecordedTransition()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await RunCreationTests.StartAsync(_fixture, database: database.Name);
        Guid runId;
        await _fixture.ExecuteAsync(world.Database, "alter table public.run disable trigger run_state_event_created");
        try
        {
            runId = await CreateQueuedRunAsync(world);
        }
        finally
        {
            await _fixture.ExecuteAsync(world.Database, "alter table public.run enable trigger run_state_event_created");
        }

        (await RowCountAsync(world, runId)).ShouldBe(0);
        var before = await GetRunAsync(world, runId);
        before.Status.ShouldBe(HttpStatusCode.OK, before.ErrorMessage);
        before.Body.GetProperty("state_history").ValueKind.ShouldBe(JsonValueKind.Null);
        (await TransitionAsync(world, runId, "running")).IsSuccess.ShouldBeTrue();
        (await HistoryAsync(world, runId)).ShouldBe("queued->running");
        var after = await GetRunAsync(world, runId);
        after.Status.ShouldBe(HttpStatusCode.OK, after.ErrorMessage);
        after.Body.GetProperty("state_history").ValueKind.ShouldBe(JsonValueKind.Null);
        var known = await CreateQueuedRunAsync(world);
        (await GetRunAsync(world, known)).Body.GetProperty("state_history").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task ForeignOrgRunId_HasNoVisibleHistoryAndReturnsNotFound()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await using var rival = await RunCreationTests.StartAsync(_fixture);
        var runId = await RunningRunAsync(world);
        await using (var own = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await own.ScalarAsync<long>($"select count(*) from public.run_state_event where run_id = '{runId:D}'"))
                .ShouldBe(2);
        }

        await using (var foreign = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), rival.Org))
        {
            (await foreign.ScalarAsync<long>($"select count(*) from public.run_state_event where run_id = '{runId:D}'"))
                .ShouldBe(0);
        }

        var answer = await world.Client.SendAsync(HttpMethod.Get, $"/v1/runs/{runId:D}",
            TenancyApiFactory.Resolved(user: rival.User, orgHeader: rival.Org));
        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
        answer.Body.TryGetProperty("state_history", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("update public.run_state_event set to_state = 'complete'")]
    [InlineData("delete from public.run_state_event")]
    public async Task AppHistoryMutation_IsDeniedWithoutChangingReadableRows(string statement)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await RunningRunAsync(world);
        var before = await HistoryAsync(world, runId);
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ScalarAsync<long>($"select count(*) from public.run_state_event where run_id = '{runId:D}'"))
                .ShouldBe(2);
            (await scope.CaptureErrorAsync(statement + $" where run_id = '{runId:D}'")).SqlState.ShouldBe("42501");
        }

        (await HistoryAsync(world, runId)).ShouldBe(before);
    }
}
