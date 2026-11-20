// A complete run admits a report only inside its own late window, anchored on the instant its collecting → complete
// edge stamped. These cases own that half of the admission: inside the window the report is stored and marked late,
// outside it — or on a complete row that never got an instant — it is refused and writes nothing.
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportParseTests
{
    [Fact]
    public async Task Complete_Run_Inside_Its_Window_Admits_The_Report_And_Marks_It_Late()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        await RunStateWalk.MoveAsync(_fixture, world.Database, world.RunId, "complete");
        var completed = await CompletedAtAsync(world, world.RunId);

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(1);
        (await CountAsync(world, "event", artifactId)).ShouldBe(1);
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            $"select late from public.event where artifact_ids = array['{artifactId:D}'::uuid]"))
            .ShouldBeTrue("evidence the run admitted after completing is marked late");
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            $"select payload ? 'late_evidence' from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunReconcileContract.OutboxTaskType}'"))
            .ShouldBeTrue("a late report asks the reconciler for a late revision, not an ordinary shard comparison");
        (await OutboxRowsAsync(world, RunStateTransitionContract.OutboxTaskType))
            .ShouldBe(0, "late evidence never moves a complete run's terminal state");
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select state from public.run where id = '{world.RunId:D}'")).ShouldBe("complete");
        (await CompletedAtAsync(world, world.RunId))
            .ShouldBe(completed, "an accepted late report never moves the anchor its own window is measured from");
    }

    [Fact]
    public async Task Complete_Run_Outside_Its_Window_Refuses_The_Report_And_Writes_Nothing()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        await CloseCompleteRunAsync(world);

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Skipped);
        outcome.Reason.ShouldBe(RunAdmissionPolicy.RunClosed);
        await AssertNothingWrittenAsync(world, artifactId, 0);
    }

    [Fact]
    public async Task Complete_Run_Without_A_Completion_Instant_Refuses_The_Report_And_Writes_Nothing()
    {
        var world = await StartAsync();
        await using var _ = world;
        var runId = await InsertCompleteRunWithoutInstantAsync(world);
        var artifactId = await SeedArtifactAsync(world with { RunId = runId }, ReportBody);

        var outcome = await ParseAsync(world, artifactId, ProducerKind, runId: runId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Skipped);
        outcome.Reason.ShouldBe(RunAdmissionPolicy.RunClosed);
        (await CompletedAtAsync(world, runId)).ShouldBeNull("the case needs a complete row that never got an anchor");
        await AssertNothingWrittenAsync(world, artifactId, 0);
    }

    /// <summary>
    /// Completes the run and spends its window: the edge stamps <c>completed_at</c>, and a zero-minute window read by a
    /// later transaction (<c>now() &gt; completed_at</c>) is the state a complete run is in once its window has passed.
    /// The later statement's clock is the proof, so no case ever waits for wall-clock time.
    /// </summary>
    private async Task CloseCompleteRunAsync(World world)
    {
        await RunStateWalk.MoveAsync(_fixture, world.Database, world.RunId, "complete");
        await _fixture.ExecuteAsync(
            world.Database, $"update public.run set late_window_minutes = 0 where id = '{world.RunId:D}'");
        (await _fixture.ScalarAsync<bool>(
            world.Database, $"select now() > completed_at from public.run where id = '{world.RunId:D}'"))
            .ShouldBeTrue("the refusal needs the window spent, which the later transaction's clock proves");
    }

    /// <summary>
    /// One committed complete run without a completion instant, copied from the world's run so every parent key stays
    /// valid: the insert guard refuses a caller-supplied instant and leaves NULL alone, which is the shape a row that no
    /// backfill reached has.
    /// </summary>
    private async Task<Guid> InsertCompleteRunWithoutInstantAsync(World world)
    {
        var id = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.run (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id, "
            + "state, collection_deadline_at, late_window_minutes) "
            + $"select '{id:D}', org_id, project_id, manifest_id, suite_revision_id, environment_revision_id, "
            + $"'complete', collection_deadline_at, late_window_minutes from public.run where id = '{world.RunId:D}'");

        return id;
    }

    private Task<string?> CompletedAtAsync(World world, Guid runId) =>
        _fixture.ScalarAsync<string?>(
            world.Database, $"select completed_at::text from public.run where id = '{runId:D}'");
}
