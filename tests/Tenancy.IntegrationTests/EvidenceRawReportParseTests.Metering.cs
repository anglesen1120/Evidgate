using System.Globalization;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Ingestion.Application.Commands;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// What one parse attempt commits besides its evidence: the run's <c>queued → running</c> edge and the metering of the
/// events it really inserted (one <c>event_batch</c> receipt and one <c>usage.batch_accepted</c> outbox row, the ingest
/// route's own shape). A replay inserts nothing and therefore adds neither, a report that mapped nothing meters nothing,
/// and a refusal rolls all of it back.
/// </summary>
public sealed partial class EvidenceRawReportParseTests
{
    /// <summary>The usage task the ingest route writes and this handler must write identically (04.8.1 §4).</summary>
    private const string UsageTask = "usage.batch_accepted";

    /// <summary>
    /// The first report of the partial replay: <c>probe</c> is planned (<c>ReportBody</c>'s first case), <c>ghost</c> is
    /// not, so exactly one event is stored. The second report below keeps <c>probe</c> byte-identical and adds a planned
    /// case, which is the one case a replay can still insert.
    /// </summary>
    private static readonly byte[] ProbeAndBetaBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites>
          <testsuite name="pytest" tests="2">
            <testcase name="probe" time="0.012"/>
            <testcase name="beta" time="0.004"/>
          </testsuite>
        </testsuites>
        """u8.ToArray();

    /// <summary>
    /// <c>beta</c> before <c>probe</c>: the case a later parse can still insert sits at the lower sequence, so the second
    /// batch's high-water mark equals the first one's — only the accepted batch's own identity tells them apart.
    /// </summary>
    private static readonly byte[] BetaThenProbeBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites>
          <testsuite name="pytest" tests="2">
            <testcase name="beta" time="0.004"/>
            <testcase name="probe" time="0.012"/>
          </testsuite>
        </testsuites>
        """u8.ToArray();

    [Fact]
    public async Task Parsed_Report_Writes_One_Receipt_And_One_Usage_Row_For_What_It_Inserted()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(1);

        // One receipt for the events this attempt inserted, named by the report it was built from.
        (await CountAsync(world, "event_batch")).ShouldBe(1);
        (await _fixture.ScalarAsync<int>(world.Database, Batch(world, "accepted"))).ShouldBe(outcome.Events);
        (await _fixture.ScalarAsync<int>(world.Database, Batch(world, "duplicate"))).ShouldBe(0);
        (await _fixture.ScalarAsync<int>(world.Database, Batch(world, "event_count"))).ShouldBe(outcome.Events);
        (await _fixture.ScalarAsync<string>(world.Database, Batch(world, "producer_id")))
            .ShouldBe("junit-" + artifactId.ToString("D", CultureInfo.InvariantCulture));
        (await _fixture.ScalarAsync<string>(world.Database, Batch(world, "request_id")))
            .ShouldBe("raw-report-parse:" + artifactId.ToString("D", CultureInfo.InvariantCulture));
        (await _fixture.ScalarAsync<long>(world.Database, Batch(world, "bytes_in"))).ShouldBe(ReportBody.LongLength);

        // One usage row, in the ingest route's exact shape, pointing at that receipt.
        var batchId = await _fixture.ScalarAsync<Guid>(world.Database, Batch(world, "id"));
        (await OutboxRowsAsync(world, UsageTask)).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(world.Database, Usage(world, "aggregate_type"))).ShouldBe("event_batch");
        (await _fixture.ScalarAsync<Guid>(world.Database, Usage(world, "aggregate_id"))).ShouldBe(batchId);
        (await _fixture.ScalarAsync<Guid>(world.Database, UsageJson(world, "event_batch_id", "uuid")))
            .ShouldBe(batchId);
        (await _fixture.ScalarAsync<string>(world.Database, UsageJson(world, "metric", "text"))).ShouldBe("event");
        (await _fixture.ScalarAsync<int>(world.Database, UsageJson(world, "quantity", "integer")))
            .ShouldBe(outcome.Events);
        (await _fixture.ScalarAsync<string>(world.Database, Usage(world, "idempotency_key")))
            .ShouldBe($"usage:batch:{batchId:D}:event");

        // Metering equals the evidence: the accepted count is the artifact's event rows, once.
        (await CountAsync(world, "event", artifactId)).ShouldBe(outcome.Events);
    }

    [Fact]
    public async Task A_Replay_Of_The_Same_Bytes_Adds_No_Receipt_And_No_Usage_Row()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        await ParseAsync(world, artifactId, ProducerKind);
        var requestId = await _fixture.ScalarAsync<string>(world.Database, Batch(world, "request_id"));

        var replay = await ParseAsync(world, artifactId, ProducerKind);

        replay.Events.ShouldBe(0);
        (await CountAsync(world, "event_batch")).ShouldBe(1);
        (await OutboxRowsAsync(world, UsageTask)).ShouldBe(1);
        // The receipt still names the artifact, so a replay is traceable to the batch it did not add.
        (await _fixture.ScalarAsync<string>(world.Database, Batch(world, "request_id"))).ShouldBe(requestId);
    }

    [Fact]
    public async Task A_Replay_That_Adds_A_Planned_Case_Meters_Only_What_It_Inserted()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        await SeedSecondCaseAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        (await ParseAsync(world, artifactId, ProducerKind)).Events.ShouldBe(1);

        // The same artifact re-stored with one more planned case: probe's identity and hash are unchanged (a duplicate),
        // beta is new. The receipt has to state both, and only the new one is metered.
        await StoreAsync(world, artifactId, ProbeAndBetaBody);
        var second = await ParseAsync(world, artifactId, ProducerKind);

        second.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        second.Events.ShouldBe(1);
        (await CountAsync(world, "event", artifactId)).ShouldBe(2);
        (await CountAsync(world, "event_batch")).ShouldBe(2);
        (await _fixture.ScalarAsync<int>(world.Database, LatestBatch(world, "event_count"))).ShouldBe(2);
        (await _fixture.ScalarAsync<int>(world.Database, LatestBatch(world, "accepted"))).ShouldBe(1);
        (await _fixture.ScalarAsync<int>(world.Database, LatestBatch(world, "duplicate"))).ShouldBe(1);
        (await OutboxRowsAsync(world, UsageTask)).ShouldBe(2);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select sum((payload ->> 'quantity')::int) from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{UsageTask}'")).ShouldBe(2);
    }

    [Fact]
    public async Task A_Second_Parse_Whose_New_Case_Precedes_The_Stored_One_Enqueues_A_Second_Trigger()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, BetaThenProbeBody);
        (await ParseAsync(world, artifactId, ProducerKind)).Events.ShouldBe(1);

        // The stored case already holds the report's last position, so the second parse inserts one event without
        // moving the document's high-water mark: only the accepted batch tells the two triggers apart.
        await SeedSecondCaseAsync(world);
        var second = await ParseAsync(world, artifactId, ProducerKind);

        second.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        second.Events.ShouldBe(1);
        (await CountAsync(world, "event", artifactId)).ShouldBe(2);
        (await CountAsync(world, "event_batch")).ShouldBe(2);
        (await OutboxRowsAsync(world, UsageTask)).ShouldBe(2);
        (await OutboxRowsAsync(world, RunReconcileContract.OutboxTaskType)).ShouldBe(2);
        // Two accepted batches of one producer: both triggers exist, under two distinct keys.
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(distinct idempotency_key) from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunReconcileContract.OutboxTaskType}'")).ShouldBe(2);
        // The run's edge belongs to the first batch alone: three rows from it and two from the second.
        (await OutboxRowsAsync(world, RunStateTransitionContract.OutboxTaskType)).ShouldBe(1);
        (await CountAsync(world, "outbox")).ShouldBe(5);
    }

    [Fact]
    public async Task A_Report_That_Maps_No_Case_Meters_Nothing()
    {
        var world = await StartAsync();
        await using var _ = world;
        var artifactId = await SeedArtifactAsync(world, ReportBody);

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(0);
        (await CountAsync(world, "event_batch")).ShouldBe(0);
        (await OutboxRowsAsync(world, UsageTask)).ShouldBe(0);
        // The run still moved on: the report is evidence of a finished parse, not of a metered event set.
        (await RunStateAsync(world)).ShouldBe("running");
    }

    [Fact]
    public async Task A_Queued_Run_Is_Moved_To_Running_Once_By_Its_Report()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        (await RunStateAsync(world)).ShouldBe("queued");

        await ParseAsync(world, artifactId, ProducerKind);
        await ParseAsync(world, artifactId, ProducerKind);

        var state = await RunStateAsync(world);
        state.ShouldBe("running");
        (await OutboxRowsAsync(world, RunStateTransitionContract.OutboxTaskType)).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select payload ->> 'from' from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunStateTransitionContract.OutboxTaskType}'")).ShouldBe("queued");
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select payload ->> 'to' from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunStateTransitionContract.OutboxTaskType}'")).ShouldBe("running");
        // One applied transition bumps the row's version once; the replay wrote no second state change.
        (await RunVersionAsync(world)).ShouldBe(2);
    }

    [Fact]
    public async Task An_Already_Running_Run_Is_Not_Re_Transitioned()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        await SetRunStateAsync(world, "update public.run set state = 'running'");

        await ParseAsync(world, artifactId, ProducerKind);

        (await RunStateAsync(world)).ShouldBe("running");
        (await RunVersionAsync(world)).ShouldBe(2, "the seeded update is the only version bump");
        (await OutboxRowsAsync(world, RunStateTransitionContract.OutboxTaskType)).ShouldBe(0);
        (await CountAsync(world, "event_batch")).ShouldBe(1);
    }

    [Fact]
    public async Task A_Canceled_Run_Is_Not_Re_Transitioned_And_Its_Report_Is_Still_Metered()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        await SetRunStateAsync(world, "update public.run set state = 'canceled'");

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        (await RunStateAsync(world)).ShouldBe("canceled");
        (await OutboxRowsAsync(world, RunStateTransitionContract.OutboxTaskType)).ShouldBe(0);
        (await _fixture.ScalarAsync<int>(world.Database, Batch(world, "accepted"))).ShouldBe(outcome.Events);
        (await OutboxRowsAsync(world, UsageTask)).ShouldBe(1);
    }

    [Fact]
    public async Task A_Complete_Run_Writes_No_Receipt_No_Usage_Row_And_No_Transition()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        await SetRunStateAsync(world, "update public.run set state = 'complete'");
        var version = await RunVersionAsync(world);

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Skipped);
        outcome.Reason.ShouldBe(RunAdmissionPolicy.RunClosed);
        (await RunStateAsync(world)).ShouldBe("complete");
        (await RunVersionAsync(world)).ShouldBe(version);
        (await OutboxRowsAsync(world, UsageTask)).ShouldBe(0);
        await AssertNothingWrittenAsync(world, artifactId);
    }

    [Fact]
    public async Task An_Artifact_Of_Another_Org_Writes_Nothing_In_Either_Org()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var other = await StartAsync();
        await using var __ = other;
        await SeedPlanAsync(other);
        var foreignArtifact = await SeedArtifactAsync(other, ReportBody);

        // The claimed task's org is world's while the artifact belongs to another tenant: the artifact is not a row
        // here, so the attempt stops before any write — and the transition it had already applied is rolled back.
        var outcome = await ParseAsync(world, foreignArtifact, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Skipped);
        (await RunStateAsync(world)).ShouldBe("queued");
        await AssertNothingWrittenAsync(world, foreignArtifact);
        (await CountAsync(other, "event", foreignArtifact)).ShouldBe(0);
        (await CountAsync(other, "event_batch")).ShouldBe(0);
        (await CountAsync(other, "outbox")).ShouldBe(0);
        (await RunStateAsync(other)).ShouldBe("queued");
    }

    [Fact]
    public async Task A_Producer_Issue_Is_Stored_As_Unsupported_Without_Event_Or_Metering()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);

        var outcome = await ParseIssueAsync(world, artifactId, ParseRawReportPayload.ProducerUnknown);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Unsupported);
        outcome.Reason.ShouldBe(ParseRawReportPayload.ProducerUnknown);
        outcome.Events.ShouldBe(0);
        (await _fixture.ScalarAsync<string>(world.Database, RawReport(world, artifactId, "outcome")))
            .ShouldBe("unsupported");
        (await _fixture.ScalarAsync<string>(world.Database, RawReport(world, artifactId, "reason")))
            .ShouldBe(ParseRawReportPayload.ProducerUnknown);
        // 0078's column is not null, so an issue row carries the reserved word instead of a producer hint.
        (await _fixture.ScalarAsync<string>(world.Database, RawReport(world, artifactId, "producer_kind")))
            .ShouldBe(ParseRawReportPayload.UnresolvedProducerKind);
        (await _fixture.ScalarAsync<string?>(world.Database, RawReport(world, artifactId, "parser_id"))).ShouldBeNull();
        (await CountAsync(world, "event_batch")).ShouldBe(0);
        (await OutboxRowsAsync(world, UsageTask)).ShouldBe(0);
        (await RunStateAsync(world)).ShouldBe("queued");
        await AssertNothingWrittenAsync(world, artifactId);
    }

    [Fact]
    public async Task A_Second_Issue_Delivery_Adds_No_Second_Issue_Row()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);

        var first = await ParseIssueAsync(world, artifactId, ParseRawReportPayload.ProducerAmbiguous);
        var second = await ParseIssueAsync(world, artifactId, ParseRawReportPayload.ProducerAmbiguous);

        first.Reason.ShouldBe(ParseRawReportPayload.ProducerAmbiguous);
        second.Status.ShouldBe(ParseRawReportOutcome.Unsupported);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.raw_report where org_id = '{world.Org:D}' "
            + $"and artifact_id = '{artifactId:D}'")).ShouldBe(1);
    }

    [Fact]
    public void The_Usage_Row_Matches_The_Ingest_Routes_Own_Constants()
    {
        // The receipt this handler writes is the ingest route's row: the same task type, metric, aggregate and key
        // shape, so the metering task that binds it cannot tell the two writers apart.
        UsageBatchAcceptedContract.TaskType.ShouldBe(IngestEventBatchHandler.UsageTaskType);
        UsageBatchAcceptedContract.Metric.ShouldBe(IngestEventBatchHandler.UsageMetric);

        var batchId = Guid.CreateVersion7();
        UsageBatchAcceptedContract.IdempotencyKey(batchId).ShouldBe($"usage:batch:{batchId}:event");
        UsageBatchAcceptedContract.PayloadJson(batchId, 3)
            .ShouldBe($$"""{"event_batch_id":"{{batchId:D}}","metric":"event","quantity":3}""");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<ParseRawReportOutcome> ParseIssueAsync(World world, Guid artifactId, string issue) =>
        await CreateHandler(world, runner: null).ExecuteAsync(
            new ParseRawReportPayload(world.RunId, artifactId, ProducerKind: null, issue),
            world.Org,
            Token);

    /// <summary>
    /// A second case of the world's project, planned for its run and resolvable by its bare name: the one case a report
    /// can add that the stored evidence does not already hold.
    /// </summary>
    private async Task SeedSecondCaseAsync(World world)
    {
        var caseId = Guid.CreateVersion7();
        var revisionId = Guid.CreateVersion7();
        var hash = "sha256:" + new string('c', 64);

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit) "
            + $"values ('{caseId:D}', '{world.Org:D}', '{world.Project:D}', '{Guid.CreateVersion7():D}', 'beta', "
            + "'playwright', 'abcdef0')");
        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.test_revision (id, org_id, case_id, revision_hash, commit_sha, file_path, framework, "
            + "content_sha256, assertion_hash) values "
            + $"('{revisionId:D}', '{world.Org:D}', '{caseId:D}', '{hash}', 'abcdef0', 'b', 'playwright', '{hash}', '{hash}')");
        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.planned_execution (id, org_id, run_id, execution_key, case_revision_id, variant_hash, "
            + "target_env, shard_id) values "
            + $"('{Guid.CreateVersion7():D}', '{world.Org:D}', '{world.RunId:D}', '{new string('c', 64)}', "
            + $"'{revisionId:D}', 'sha256:{new string('b', 64)}', 'staging', 'shard-1')");
    }

    private Task<string?> RunStateAsync(World world) =>
        _fixture.ScalarAsync<string?>(
            world.Database,
            $"select state from public.run where id = '{world.RunId:D}'");

    private Task<int> RunVersionAsync(World world) =>
        _fixture.ScalarAsync<int>(
            world.Database,
            $"select version from public.run where id = '{world.RunId:D}'");

    private static string Batch(World world, string column) =>
        $"select {column} from public.event_batch where org_id = '{world.Org:D}'";

    /// <summary>The receipt of the most recent attempt: its predecessor already holds the first parse's row.</summary>
    private static string LatestBatch(World world, string column) =>
        $"select {column} from public.event_batch where org_id = '{world.Org:D}' order by created_at desc, id desc limit 1";

    private static string Usage(World world, string column) =>
        $"select {column} from public.outbox where org_id = '{world.Org:D}' and task_type = '{UsageTask}'";

    /// <summary>One key of the usage row's payload, read back as the column type it has to be.</summary>
    private static string UsageJson(World world, string key, string type) =>
        $"select (payload ->> '{key}')::{type} from public.outbox where org_id = '{world.Org:D}' "
        + $"and task_type = '{UsageTask}'";

    private static string RawReport(World world, Guid artifactId, string column) =>
        $"select {column} from public.raw_report where org_id = '{world.Org:D}' and artifact_id = '{artifactId:D}'";
}
