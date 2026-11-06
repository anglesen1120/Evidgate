// The admission half of the reprocess path: which finished or canceled run still accepts a re-reading of its own stored
// report, and which reports end the attempt without a row. Expectations come from 04.4.2 §5 and the coordinator decision
// that a reprocess never moves the run's state (D-S267-Q1/Q2).
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReprocessReportTaskTests
{
    /// <summary>A well-formed report with no testcase at all: the document parses and maps nothing.</summary>
    private static readonly byte[] EmptyReportBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuite name="pytest" tests="0" failures="0" errors="0" skipped="0"/>
        """u8.ToArray();

    [Fact]
    public async Task Canceled_Run_Is_Reprocessed_As_Post_Cancel_Evidence_And_Keeps_Its_State()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        var sourceReportId = await SeedSourceReportAsync(world, artifactId);
        var parsingId = await SeedRequestAsync(world, artifactId, sourceReportId, PinVersion);
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set state = 'canceled' where id = '{world.RunId:D}'");

        var outcome = await ReprocessAsync(world, parsingId);

        outcome.Events.ShouldBe(1);
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            $"select after_cancel from public.event where org_id = '{world.Org:D}'")).ShouldBeTrue();
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select state from public.run where id = '{world.RunId:D}'")).ShouldBe("canceled");

        // A canceled run never moves again, so its re-read evidence only counts through a revision: the trigger carries
        // the reprocess marker, which is the one shape that lets the reconciler compare this run.
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            $"select payload ? 'reprocess_evidence' from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunReconcileContract.OutboxTaskType}'")).ShouldBeTrue();
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select count(*)::int from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunStateTransitionContract.OutboxTaskType}'")).ShouldBe(0);
    }

    [Fact]
    public async Task Complete_Run_Is_Reprocessed_As_Its_Next_Revision_And_Keeps_Its_State()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        var sourceReportId = await SeedSourceReportAsync(world, artifactId);
        var parsingId = await SeedRequestAsync(world, artifactId, sourceReportId, PinVersion);
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set state = 'complete' where id = '{world.RunId:D}'");

        // A complete run is terminal, so intake refuses every new producer; a re-reading of its own stored report is the
        // one evidence the plan keeps open for it, and the task must not strand the request the route accepted.
        var outcome = await ReprocessAsync(world, parsingId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(1);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select state from public.run where id = '{world.RunId:D}'")).ShouldBe("complete");

        // Neither marker of the ingest path applies: the revision, not the state, is what this evidence reaches.
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            $"select late or after_cancel from public.event where org_id = '{world.Org:D}'")).ShouldBeFalse();
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            $"select payload ? 'reprocess_evidence' from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunReconcileContract.OutboxTaskType}'")).ShouldBeTrue();
    }

    [Fact]
    public async Task Expired_Incomplete_Run_Still_Accepts_A_Reprocess_And_Keeps_Its_State()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        var sourceReportId = await SeedSourceReportAsync(world, artifactId);
        var parsingId = await SeedRequestAsync(world, artifactId, sourceReportId, PinVersion);

        // The run already finished incomplete and its late window is over: intake refuses evidence here, while a
        // reprocess of bytes the run already holds is the case the plan keeps open for a cancelled or incomplete run.
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set state = 'incomplete', late_window_minutes = 0, "
            + $"collection_deadline_at = now() - interval '1 day' where id = '{world.RunId:D}'");

        var outcome = await ReprocessAsync(world, parsingId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(1);
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            $"select late from public.event where org_id = '{world.Org:D}'")).ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            $"select after_cancel from public.event where org_id = '{world.Org:D}'")).ShouldBeFalse();

        // The trigger this task writes now carries the reprocess marker in every state it read (04.4.2 F4): that is the
        // shape the reconciler routes onto the late path for an incomplete run, so the freeze still records the new
        // revision without moving the run, and no ordinary settlement trigger is written beside it.
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            $"select payload ? 'reprocess_evidence' from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunReconcileContract.OutboxTaskType}'")).ShouldBeTrue();
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select count(*)::int from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunStateTransitionContract.OutboxTaskType}'")).ShouldBe(0);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select state from public.run where id = '{world.RunId:D}'")).ShouldBe("incomplete");
    }

    [Fact]
    public async Task Collecting_Run_Is_Reprocessed_Without_A_Settlement_Trigger()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        var sourceReportId = await SeedSourceReportAsync(world, artifactId);
        var parsingId = await SeedRequestAsync(world, artifactId, sourceReportId, PinVersion);
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set state = 'collecting' where id = '{world.RunId:D}'");

        var outcome = await ReprocessAsync(world, parsingId);

        outcome.Events.ShouldBe(1);

        // Every trigger this task writes carries the marker, whatever state it read: without it the queue's ordinary
        // shapes would settle this run from a re-read of evidence it is still collecting.
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            $"select payload ? 'reprocess_evidence' from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunReconcileContract.OutboxTaskType}'")).ShouldBeTrue();
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select count(*)::int from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunStateTransitionContract.OutboxTaskType}'")).ShouldBe(0);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select state from public.run where id = '{world.RunId:D}'")).ShouldBe("collecting");
    }

    [Fact]
    public async Task Missing_Stored_Object_Throws_And_Leaves_The_Request_Without_A_Report_Row()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactRowAsync(world, ReportBody);
        var sourceReportId = await SeedSourceReportAsync(world, artifactId);
        var parsingId = await SeedRequestAsync(world, artifactId, sourceReportId, PinVersion);

        var threw = false;
        try
        {
            await ReprocessAsync(world, parsingId);
        }
        catch (InvalidOperationException)
        {
            // The store could not answer, so nothing judged the report: the task retries and eventually dead-letters.
            threw = true;
        }

        threw.ShouldBeTrue();
        (await CountAsync(world, "event")).ShouldBe(0);
        (await CountAsync(world, "producer_slot")).ShouldBe(0);
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select count(*)::int from public.raw_report where id = '{parsingId:D}'")).ShouldBe(0);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select outcome from public.raw_report where id = '{sourceReportId:D}'")).ShouldNotBeNull();
    }

    [Fact]
    public async Task Doctype_Document_Is_Refused_As_Malformed_With_No_Event()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, DoctypeBody);
        var sourceReportId = await SeedSourceReportAsync(world, artifactId);
        var parsingId = await SeedRequestAsync(world, artifactId, sourceReportId, PinVersion);

        var outcome = await ReprocessAsync(world, parsingId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        outcome.Events.ShouldBe(0);
        (await CountAsync(world, "event")).ShouldBe(0);
        (await CountAsync(world, "case_attempt")).ShouldBe(0);
        (await CountAsync(world, "producer_slot")).ShouldBe(0);

        // The refusal is still one row, and its one trigger names this request's producer like the mapped path's does.
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select outcome from public.raw_report where id = '{parsingId:D}'"))
            .ShouldBe(RawReportOutcomes.Malformed);
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select count(*)::int from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunReconcileContract.OutboxTaskType}' "
            + $"and (payload ->> 'producer_id') = 'reprocess-{parsingId:D}'")).ShouldBe(1);
    }

    [Fact]
    public async Task A_Report_That_Maps_No_Case_Still_Asks_For_One_Comparison()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, EmptyReportBody);
        var sourceReportId = await SeedSourceReportAsync(world, artifactId);
        var parsingId = await SeedRequestAsync(world, artifactId, sourceReportId, PinVersion);

        var outcome = await ReprocessAsync(world, parsingId);

        // Nothing to write but the row: no slot, no event, no attempt.
        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(0);
        (await CountAsync(world, "event")).ShouldBe(0);
        (await CountAsync(world, "producer_slot")).ShouldBe(0);
        (await CountAsync(world, "case_attempt")).ShouldBe(0);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select outcome from public.raw_report where id = '{parsingId:D}'")).ShouldBe(RawReportOutcomes.Mapped);

        // The row alone would never be read: the trigger is what carries it to the reconciler, and it is the same marked,
        // distinctly keyed trigger a mapped attempt writes.
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select count(*)::int from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunReconcileContract.OutboxTaskType}' "
            + $"and payload ? 'reprocess_evidence' "
            + $"and (payload ->> 'producer_id') = 'reprocess-{parsingId:D}'"))
            .ShouldBe(1);
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select count(*)::int from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunStateTransitionContract.OutboxTaskType}'")).ShouldBe(0);
    }

    [Fact]
    public async Task Request_For_A_Release_No_Mapper_Implements_Throws()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        var sourceReportId = await SeedSourceReportAsync(world, artifactId);
        var parsingId = await SeedRequestAsync(world, artifactId, sourceReportId, "1.2.0");

        var threw = false;
        try
        {
            await ReprocessAsync(world, parsingId);
        }
        catch (InvalidOperationException)
        {
            // The request names a release the registry knows and this deployment does not ship: retrying cannot load it.
            threw = true;
        }

        threw.ShouldBeTrue();
        (await CountAsync(world, "event")).ShouldBe(0);
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select count(*)::int from public.raw_report where id = '{parsingId:D}'")).ShouldBe(0);
    }
}
