using System.Globalization;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Ingestion.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReprocessReportTaskTests
{
    /// <summary>
    /// The usage half of the reprocess path: the parse task meters the events it inserted, and a reprocess of the same
    /// stored report adds evidence without billing it a second time.
    /// </summary>
    /// <remarks>
    /// The parse this test runs first is the positive control, so the counting queries below are proven to bite: they
    /// name exactly the rows the parse path writes — the <c>usage.batch_accepted</c> outbox row built by
    /// <c>UsageBatchAcceptedContract</c>, the <c>event_batch</c> receipt it meters, and the task row the dispatcher
    /// would make of that outbox row. If the reprocess emitted the batch row, the parse's one row would become two and
    /// the assertion would fail.
    /// </remarks>
    [Fact]
    public async Task Reprocess_Meters_Nothing_While_The_Reparsed_Report_Was_Metered()
    {
        await using var world = await StartAsync();
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);

        var original = await ParseOriginalReportAsync(world, artifactId);
        original.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        original.Events.ShouldBe(1);
        var sourceReportId = await SourceReportIdAsync(world, artifactId);

        var batches = await MeteredReceiptsAsync(world);
        batches.ShouldBe(1, "the production parse meters the evidence it inserted");
        var meteringRows = await MeteringOutboxAsync(world);
        meteringRows.ShouldBe(1, "the production parse enqueues its accepted batch as a metering row");
        var meteringTasks = await MeteringTasksAsync(world);
        meteringTasks.ShouldBe(0, "nothing dispatched the enqueued row in this world");

        var parsingId = await SeedRequestAsync(world, artifactId, sourceReportId, PinVersion);

        var outcome = await ReprocessAsync(world, parsingId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(1);
        (await CountAsync(world, "raw_report")).ShouldBe(2, "the reprocess committed the report row it wrote evidence for");
        (await ReprocessTriggerAsync(world, parsingId)).ShouldBe(1, "the reprocess did enqueue its own reconcile trigger");

        (await MeteredReceiptsAsync(world)).ShouldBe(batches, "a reprocess writes no batch receipt of its own");
        (await MeteringOutboxAsync(world)).ShouldBe(meteringRows, "a reprocess bills nothing it re-parsed");
        (await MeteringTasksAsync(world)).ShouldBe(meteringTasks, "a reprocess queues no metering task");
        (await CountAsync(world, "usage_ledger_entry")).ShouldBe(0, "nothing consumed a metering row for this tenant");
    }

    /// <summary>The batch receipts the parse path's metering writes; one per accepted report, never one per reprocess.</summary>
    private Task<long> MeteredReceiptsAsync(World world) => CountAsync(world, "event_batch");

    /// <summary>The metering outbox rows: the exact task type the parse path enqueues for an accepted batch.</summary>
    private Task<long> MeteringOutboxAsync(World world) => TaskRowsAsync(world, "outbox");

    /// <summary>The queued metering tasks an outbox dispatch would create; a reprocess must add none.</summary>
    private Task<long> MeteringTasksAsync(World world) => TaskRowsAsync(world, "task");

    private Task<long> TaskRowsAsync(World world, string table) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.{table} where org_id = '{world.Org:D}' "
            + $"and task_type = '{UsageBatchAcceptedContract.TaskType}'");

    /// <summary>The reconcile trigger of one reprocess, so the metering assertions are not vacuous.</summary>
    /// <param name="world">The tenant of the reprocess.</param>
    /// <param name="parsingId">The request id whose synthetic producer the trigger names.</param>
    private Task<long> ReprocessTriggerAsync(World world, Guid parsingId) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where org_id = '{world.Org:D}' "
            + $"and (payload ->> 'producer_id') = 'reprocess-"
            + parsingId.ToString("D", CultureInfo.InvariantCulture) + "'");
}
