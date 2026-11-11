using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The admission half of the parse handler: the run's own state, its late window and its project's state decide whether
/// a report is accepted at all. A refused admission writes nothing, not even the report's refusal row.
/// </summary>
public sealed partial class EvidenceRawReportParseTests
{
    [Fact]
    public async Task Complete_Run_Refuses_The_Report_And_Writes_Nothing()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        await RunStateWalk.MoveAsync(_fixture, world.Database, world.RunId, "complete");

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Skipped);
        outcome.Reason.ShouldBe(RunAdmissionPolicy.RunClosed);
        await AssertNothingWrittenAsync(world, artifactId, 0);
    }

    [Fact]
    public async Task Incomplete_Run_Past_Its_Late_Window_Refuses_The_Report_And_Writes_Nothing()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        await SetRunStateAsync(
            world,
            "update public.run set state = 'incomplete', collection_deadline_at = now() - interval '2 days', late_window_minutes = 1");

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Skipped);
        outcome.Reason.ShouldBe(RunAdmissionPolicy.RunClosed);
        await AssertNothingWrittenAsync(world, artifactId, 0);
    }

    [Fact]
    public async Task Canceled_Run_Accepts_The_Report_And_Marks_It_After_Cancel()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        await SetRunStateAsync(world, "update public.run set state = 'canceled'");

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(1);
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            $"select after_cancel from public.event where artifact_ids = array['{artifactId:D}'::uuid]")).ShouldBeTrue();
        // A canceled run is never transitioned, so the commit leaves the reconcile trigger and the usage row.
        (await CountAsync(world, "outbox")).ShouldBe(2);
    }

    [Fact]
    public async Task Suspended_Project_Refuses_The_Report_And_Writes_Nothing()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        await _fixture.ExecuteAsync(world.Database, $"update public.project set status = 'suspended' where id = '{world.Project:D}'");

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Skipped);
        outcome.Reason.ShouldBe(RunAdmissionPolicy.ProjectSuspended);
        await AssertNothingWrittenAsync(world, artifactId, 0);
    }

    [Fact]
    public async Task Deleted_Project_Refuses_The_Report_And_Writes_Nothing()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        await _fixture.ExecuteAsync(world.Database, $"update public.project set status = 'deleted' where id = '{world.Project:D}'");

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Skipped);
        outcome.Reason.ShouldBe(RunAdmissionPolicy.ProjectDeleted);
        await AssertNothingWrittenAsync(world, artifactId, 0);
    }
}
