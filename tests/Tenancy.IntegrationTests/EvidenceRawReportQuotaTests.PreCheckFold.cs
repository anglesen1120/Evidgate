// The pre-check's one read and the fetch's byte cap: the deferred probe and the run's facts are a single statement that a
// first attempt never extends to the run, and the cap is the run's project's even when the artifact row names another.
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportQuotaTests
{
    [Fact]
    public async Task The_Byte_Cap_Is_The_Runs_Projects_When_The_Artifact_Names_Another_Project()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var otherProject = await SeedOtherProjectAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody, otherProject);

        // Only the artifact's own project carries an 8-byte cap: it must not bind a report of another project's run.
        await SeedRawReportCapAsync(world, otherProject, 8);

        var outcome = await ParseAsync(world, artifactId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        (await CountRawReportsAsync(world, artifactId)).ShouldBe(1);
    }

    [Fact]
    public async Task A_Cap_Of_The_Runs_Project_Binds_A_Report_Whose_Artifact_Names_Another_Project()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var otherProject = await SeedOtherProjectAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody, otherProject);
        await SeedRawReportCapAsync(world, world.Project, 8);

        var outcome = await ParseAsync(world, artifactId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        outcome.Reason.ShouldBe("too_large");
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
    }

    [Fact]
    public async Task A_Deferred_Report_Reads_The_Probe_And_The_Run_In_One_Statement_And_Still_Fetches_With_Room()
    {
        var gate = new ReportPreCheckGate();
        var world = await StartAsync(services => ReplaceQuotaGate(services, inner => gate.Wrap(inner)));
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 0m, limit: 10m);
        await SeedDeferredTaskAsync(world, artifactId);
        world.Host.Statements.Clear();

        var outcome = await ParseAsync(world, artifactId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        gate.Peeks.ShouldBe(1);
        var probes = world.Host.Statements.Snapshot()
            .Where(statement => statement.Contains("public.outbox as o", StringComparison.Ordinal))
            .ToList();
        probes.ShouldHaveSingleItem("the deferred probe and the run facts are one round trip");
        probes[0].ShouldContain("public.run r");
    }

    [Fact]
    public async Task A_Deferred_Report_Of_A_Closed_Run_Is_Not_Peeked_And_Is_Skipped_As_Blocked_Then_Closed()
    {
        var gate = new ReportPreCheckGate();
        var world = await StartAsync(services => ReplaceQuotaGate(services, inner => gate.Wrap(inner)));
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 10m, limit: 10m);
        await SeedDeferredTaskAsync(world, artifactId);
        await RunStateWalk.MoveAsync(_fixture, world.Database, world.RunId, "complete");
        var log = new CapturedLog();

        var outcome = await ParseAsync(world, artifactId, log);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Skipped);
        gate.Peeks.ShouldBe(0, "a closed run's own refusal is the loss to report, so the counter is not asked");
        log.Lines.ShouldContain(line => line.Contains("quota_blocked_then_closed", StringComparison.Ordinal));
        (await ReadCounterAsync(counters, world)).ShouldNotBeNull().BlockedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_First_Attempt_Asks_The_Probe_In_One_Statement_And_Is_Never_Peeked()
    {
        var gate = new ReportPreCheckGate();
        var world = await StartAsync(services => ReplaceQuotaGate(services, inner => gate.Wrap(inner)));
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 0m, limit: 10m);
        world.Host.Statements.Clear();

        var outcome = await ParseAsync(world, artifactId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        gate.Peeks.ShouldBe(0);
        world.Host.Statements.Snapshot()
            .Count(statement => statement.Contains("public.outbox as o", StringComparison.Ordinal))
            .ShouldBe(1, "the probe is one round trip even when the attempt was never deferred");
    }

    private Task SeedRawReportCapAsync(World world, Guid projectId, long maxBytes) =>
        _fixture.ExecuteAsync(
            world.Database,
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity) "
            + $"values ('{Guid.CreateVersion7():D}', '{world.Org:D}', '{projectId:D}', 'raw_report', {maxBytes}, "
            + $"array['{XmlType}'], 'normal')");
}
