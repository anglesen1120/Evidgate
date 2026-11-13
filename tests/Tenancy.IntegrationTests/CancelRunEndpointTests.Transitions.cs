// 05.4.1-T1 AC3/AC4/AC6: every non-terminal source state, and the reconciler verdict that loses to a cancel.
#pragma warning disable CA1861
using System.Net;
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class CancelRunEndpointTests
{
    /// <summary>
    /// AC3: a run in <c>created</c> (the state no production writer inserts) or <c>queued</c> cancels straight
    /// to <c>canceled</c> — nothing is executing, so no soft-stop window is needed to reach the terminal state.
    /// </summary>
    [Theory]
    [InlineData("created")]
    [InlineData("queued")]
    public async Task CancelRun_FromCreatedOrQueued_StraightToCanceled(string from)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CancelRunTestData.RunInStateAsync(_fixture, world, from);
        (await CancelRunTestData.StateAsync(_fixture, world, runId)).ShouldBe(from);

        var answer = await CancelRunTestData.CancelAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);
        (await CancelRunTestData.StateAsync(_fixture, world, runId)).ShouldBe("canceled");
        (await CancelRunTestData.VersionAsync(_fixture, world, runId)).ShouldBe(1);
        (await CancelRunTestData.RowCountAsync(_fixture, world, runId)).ShouldBe(1);
    }

    /// <summary>
    /// AC4 (plan 05:115): a run in <c>running</c> or <c>collecting</c> becomes <c>canceled</c> as soon as the
    /// request is accepted — the soft-stop window acts on the execution, never on <c>run.state</c> — and the
    /// request row carries the deadlines the stop schedule uses.
    /// </summary>
    [Theory]
    [InlineData("running")]
    [InlineData("collecting")]
    public async Task CancelRun_FromRunningOrCollecting_TransitionsToCanceled(string from)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CancelRunTestData.RunInStateAsync(_fixture, world, from);
        var version = await CancelRunTestData.VersionAsync(_fixture, world, runId);

        var answer = await CancelRunTestData.CancelAsync(world, runId, reason: "operator stopped a hung run");

        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);
        (await CancelRunTestData.StateAsync(_fixture, world, runId))
            .ShouldBe("canceled", "the cancel is recorded, not 'stopped' (plan 05:115)");
        (await CancelRunTestData.VersionAsync(_fixture, world, runId)).ShouldBe(version + 1);

        IReadOnlyList<string> expected = from == "running"
            ? new[] { "running", "canceled" }
            : new[] { "running", "collecting", "canceled" };
        (await CancelRunTestData.OutboxTargetsAsync(_fixture, world, runId)).ShouldBe(expected);
        (await CancelRunTestData.RowCountAsync(_fixture, world, runId)).ShouldBe(1);
        (await CancelRunTestData.RowAsync(_fixture, world, runId)).Reason.ShouldBe("operator stopped a hung run");
    }

    /// <summary>
    /// AC6: a reconciler verdict that runs after the cancel committed finds the run terminal and no-ops, so a
    /// canceled run never finishes <c>complete</c> and no second state change is written. The ordering is pinned
    /// because the HTTP handler owns its own commit boundary; the overlapping-transactions variant of the same
    /// race, where the verdict is provably parked on the cancel's row lock, is 05.3.1's
    /// <c>TransitionRun_CancelRaceWithReconcilerVerdict_StaysCanceled</c>.
    /// </summary>
    [Fact]
    public async Task CancelRun_RaceWithReconcilerComplete_StaysCanceled()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CancelRunTestData.RunInStateAsync(_fixture, world, "collecting");

        var canceled = await CancelRunTestData.CancelAsync(world, runId, reason: "cancel wins the race");
        canceled.Status.ShouldBe(HttpStatusCode.Accepted, canceled.ErrorMessage);
        var version = await CancelRunTestData.VersionAsync(_fixture, world, runId);

        var verdict = await CancelRunTestData.TransitionAsync(world, runId, "complete");

        verdict.Outcome.ShouldBe(TransitionOutcome.NoOp, "the terminal no-op rule blocks the losing transition");
        verdict.State.ShouldBe("canceled");
        (await CancelRunTestData.StateAsync(_fixture, world, runId))
            .ShouldBe("canceled", "a canceled run never becomes complete");
        (await CancelRunTestData.VersionAsync(_fixture, world, runId)).ShouldBe(version);
        (await CancelRunTestData.OutboxTargetsAsync(_fixture, world, runId))
            .ShouldBe(["running", "collecting", "canceled"]);
        (await CancelRunTestData.RowCountAsync(_fixture, world, runId))
            .ShouldBe(1, "the losing verdict does not add a cancel row");

        // The run is terminal for good: a further verdict and a further cancel both stay no-ops.
        (await CancelRunTestData.TransitionAsync(world, runId, "complete")).Outcome.ShouldBe(TransitionOutcome.NoOp);
        (await CancelRunTestData.CancelAsync(world, runId)).Status.ShouldBe(HttpStatusCode.Accepted);
        (await CancelRunTestData.RowCountAsync(_fixture, world, runId)).ShouldBe(1);
        (await CancelRunTestData.StateAsync(_fixture, world, runId)).ShouldBe("canceled");
    }
}
