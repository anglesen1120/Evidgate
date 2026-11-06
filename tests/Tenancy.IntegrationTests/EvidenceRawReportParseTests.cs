// The `raw_report_parse` task handler over the real Api composition, a real PostgreSQL and a real SeaweedFS
// container: the handler is resolved from the production service collection, so these tests also prove the
// registration (transactionless handler, resolver, mapper enumerable, attempt writer) rather than the constructor.
// Expectations come from the feature plan (AC1/AC3/AC7, the handler task) and the coordinator decision that the
// migration ships `report_parser` only.
using System.Globalization;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The <c>raw_report_parse</c> handler against the shared migrated database and one SeaweedFS container: a resolvable
/// report writes its mapped <c>test.finished</c> events, their case attempts, a closed synthetic producer slot and the
/// reconcile trigger in one commit; a replay writes nothing new unless the bytes changed; the run's admission rules
/// refuse a closed run and a deleted or suspended project; and an unknown producer kind or a malformed document writes
/// the report's own row and no event.
/// </summary>
/// <remarks>
/// The mapper and the parser registry are production: the built-in <c>junit-pytest/1.0.0</c> row comes from the
/// migration, and the run's one planned execution is seeded as the owner seam, the way the run's other parents are.
/// </remarks>
[Trait("Category", "Docker")]
public sealed partial class EvidenceRawReportParseTests(PostgresTenancyFixture fixture, SeaweedFsStore store)
    : IClassFixture<SeaweedFsStore>
{
    /// <summary>The registry's producer kind for a pytest JUnit XML report.</summary>
    private const string ProducerKind = "pytest-junitxml";

    /// <summary>A producer kind no parser registers.</summary>
    private const string UnknownKind = "rube-goldberg-json";

    private const string XmlType = "application/xml";

    /// <summary>A well-formed execution key: sha256 hex (00 §7), the shape <c>planned_execution</c> requires.</summary>
    private static readonly string ExecutionKey = new('a', 64);

    /// <summary>
    /// One case that resolves by its bare name (the seeded plan's case is named <c>probe</c>) and one that cannot (no
    /// planned execution carries that name), so one run proves both the insert and the <c>unmapped_case</c> path.
    /// </summary>
    private static readonly byte[] ReportBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites>
          <testsuite name="pytest" tests="2">
            <testcase name="probe" time="0.012"/>
            <testcase classname="tests.test_payments" name="ghost_case" time="0.003"/>
          </testsuite>
        </testsuites>
        """u8.ToArray();

    /// <summary>A failing case: pytest's raw <c>failure</c> word must reach the attempt as <c>failed</c>, not <c>error</c>.</summary>
    private static readonly byte[] FailingBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites>
          <testsuite name="pytest" tests="1" failures="1">
            <testcase name="probe" time="0.012"><failure message="boom"/></testcase>
          </testsuite>
        </testsuites>
        """u8.ToArray();

    /// <summary>The same report with a different duration: the same identities, a different payload hash.</summary>
    private static readonly byte[] ChangedDurationBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites>
          <testsuite name="pytest" tests="2">
            <testcase name="probe" time="0.020"/>
            <testcase classname="tests.test_payments" name="ghost_case" time="0.003"/>
          </testsuite>
        </testsuites>
        """u8.ToArray();

    /// <summary>A document that is not well formed: an unclosed element no parser can accept.</summary>
    private static readonly byte[] MalformedBody = "<testsuite><testcase name=\"probe\""u8.ToArray();

    private readonly PostgresTenancyFixture _fixture = fixture;

    private readonly SeaweedFsStore _store = store;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record World(
        TenancyApiFactory Host,
        string Database,
        Guid Org,
        Guid Project,
        Guid RunId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Host.DisposeAsync();
    }

    [Fact]
    public async Task Failing_Case_Persists_Its_Attempt_As_Failed_And_The_Raw_Word_On_The_Event()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, FailingBody);

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select raw_status from public.case_attempt where org_id = '{world.Org:D}'")).ShouldBe("failed");
        (await _fixture.ScalarAsync<string?>(
            world.Database,
            $"select raw_status from public.event where artifact_ids = array['{artifactId:D}'::uuid]"))
            .ShouldBe("failure");
    }

    [Fact]
    public async Task Mapped_Report_Writes_Its_Event_Attempt_Closed_Slot_And_One_Reconcile_Trigger()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(1);
        (await CountAsync(world, "event", artifactId)).ShouldBe(1);

        // Three outbox rows leave this commit: the run's queued -> running edge, the one reconcile trigger and the
        // batch's usage row. The receipt and the usage row are asserted in EvidenceRawReportParseTests.Metering.
        (await CountAsync(world, "outbox")).ShouldBe(3);
        (await OutboxRowsAsync(world, RunStateTransitionContract.OutboxTaskType)).ShouldBe(1);
        (await OutboxRowsAsync(world, RunReconcileContract.OutboxTaskType)).ShouldBe(1);

        var parser = await _fixture.ScalarAsync<string?>(
            world.Database,
            $"select payload #>> '{{ext,parser}}' from public.event where artifact_ids = array['{artifactId:D}'::uuid]");
        parser.ShouldBe("junit-pytest/1.0.0");

        var key = await _fixture.ScalarAsync<string?>(
            world.Database,
            $"select execution_key from public.event where artifact_ids = array['{artifactId:D}'::uuid]");
        key.ShouldBe(ExecutionKey);

        // The finish is also the plan's attempt: the report path feeds the same writer the event batch route does.
        (await CountAsync(world, "case_attempt")).ShouldBe(1);
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select attempt_no from public.case_attempt where org_id = '{world.Org:D}'")).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select raw_status from public.case_attempt where org_id = '{world.Org:D}'")).ShouldBe("passed");

        await AssertClosedSyntheticSlotAsync(world, artifactId);

        // An ordinary run gets the ordinary trigger: the reconciler already sees a closed shard, and the trigger
        // carries the slot's declaration rather than a late-evidence marker.
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select task_type from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunReconcileContract.OutboxTaskType}'"))
            .ShouldBe(RunReconcileContract.OutboxTaskType);
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            $"select payload ? 'late_evidence' from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunReconcileContract.OutboxTaskType}'")).ShouldBeFalse();
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select (payload ->> 'declared_count')::int from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunReconcileContract.OutboxTaskType}'"))
            .ShouldBe(1);
    }

    /// <summary>The synthetic slot this report closed and declared in its own commit, read back by producer id.</summary>
    private async Task AssertClosedSyntheticSlotAsync(World world, Guid artifactId)
    {
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            $"select closed_at is not null from public.producer_slot where org_id = '{world.Org:D}'")).ShouldBeTrue();
        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select declared_count from public.producer_slot where org_id = '{world.Org:D}'")).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select declared_last_sequence from public.producer_slot where org_id = '{world.Org:D}'")).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select producer_id from public.producer_slot where org_id = '{world.Org:D}'"))
            .ShouldBe("junit-" + artifactId.ToString("D", CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Replay_Of_The_Same_Bytes_Writes_No_Second_Event_Attempt_Or_Outbox_Row()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);

        var first = await ParseAsync(world, artifactId, ProducerKind);
        var second = await ParseAsync(world, artifactId, ProducerKind);

        first.Events.ShouldBe(1);
        second.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        second.Events.ShouldBe(0);
        (await CountAsync(world, "event", artifactId)).ShouldBe(1);
        (await CountAsync(world, "case_attempt")).ShouldBe(1);
        // The replay adds no transition (the run is running by then) and no second reconcile trigger, so the first
        // parse's three rows stand alone.
        (await CountAsync(world, "outbox")).ShouldBe(3);
        (await OutboxRowsAsync(world, RunStateTransitionContract.OutboxTaskType)).ShouldBe(1);
        (await CountAsync(world, "event_batch")).ShouldBe(1);
    }

    [Fact]
    public async Task Replay_Whose_Bytes_Changed_Refuses_The_Whole_Attempt_And_Keeps_The_Stored_Evidence()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        await ParseAsync(world, artifactId, ProducerKind);

        // The same artifact and the same testcase names, with a different duration: the identity is unchanged and the
        // substantive hash is not, so the replay must refuse instead of overwriting immutable evidence.
        await StoreAsync(world, artifactId, ChangedDurationBody);
        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Skipped);
        outcome.Reason.ShouldBe("duplicate_different_payload");
        outcome.Events.ShouldBe(0);
        (await CountAsync(world, "event", artifactId)).ShouldBe(1);
        (await CountAsync(world, "case_attempt")).ShouldBe(1);
        // The refused replay rolls its own transition back, so only the first parse's three rows remain.
        (await CountAsync(world, "outbox")).ShouldBe(3);
        (await CountAsync(world, "event_batch")).ShouldBe(1);
        (await _fixture.ScalarAsync<long?>(
            world.Database,
            $"select duration_ms from public.event where artifact_ids = array['{artifactId:D}'::uuid]"))
            .ShouldBe(12L);
    }

    [Fact]
    public async Task Complete_Run_Refuses_The_Report_And_Writes_Nothing()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        await SetRunStateAsync(world, "update public.run set state = 'complete'");

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Skipped);
        outcome.Reason.ShouldBe(RunAdmissionPolicy.RunClosed);
        await AssertNothingWrittenAsync(world, artifactId);
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
        await AssertNothingWrittenAsync(world, artifactId);
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
        await AssertNothingWrittenAsync(world, artifactId);
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
        await AssertNothingWrittenAsync(world, artifactId);
    }

    [Fact]
    public async Task Unknown_Producer_Kind_Is_Unsupported_And_Writes_No_Event()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);

        var outcome = await ParseAsync(world, artifactId, UnknownKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Unsupported);
        outcome.Reason.ShouldNotBeNullOrWhiteSpace();
        outcome.Events.ShouldBe(0);
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        // The refusal row commits with its one reconcile trigger; the run was never moved, so nothing else is written.
        (await OutboxRowsAsync(world, RunReconcileContract.OutboxTaskType)).ShouldBe(1);
    }

    [Fact]
    public async Task Malformed_Report_Writes_No_Event()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, MalformedBody);

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        // A document the parser refuses is a verdict; an infrastructure failure would throw instead, which the
        // handler's own contract keeps distinct so a broken store can never be reported as a malformed report.
        outcome.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        outcome.Events.ShouldBe(0);
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        // The row and its trigger are the whole refusal: no event, slot or receipt, and no run transition either.
        (await OutboxRowsAsync(world, RunReconcileContract.OutboxTaskType)).ShouldBe(1);
    }

    [Fact]
    public async Task Artifact_Of_Another_Run_Writes_Nothing()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        var otherRun = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);

        var outcome = await ParseAsync(world, artifactId, ProducerKind, runId: otherRun);

        outcome.Events.ShouldBe(0);
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        (await CountAsync(world, "outbox")).ShouldBe(0);
    }

    [Fact]
    public async Task Report_Without_A_Planned_Execution_Writes_No_Event()
    {
        var world = await StartAsync();
        await using var _ = world;
        var artifactId = await SeedArtifactAsync(world, ReportBody);

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(0);
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        // The report that mapped nothing moved the run on, so its transition row is the commit's only outbox row: no
        // reconcile trigger and nothing metered.
        (await OutboxRowsAsync(world, RunStateTransitionContract.OutboxTaskType)).ShouldBe(1);
        (await OutboxRowsAsync(world, RunReconcileContract.OutboxTaskType)).ShouldBe(0);
        (await CountAsync(world, "event_batch")).ShouldBe(0);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------------------

    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        Guid user;
        Guid org;
        Guid project;

        using (var setup = api.CreateClient())
        {
            user = Guid.CreateVersion7();
            org = await CreateOrgAsync(setup, user, Unique.Slug("acme"));
            project = await CreateProjectAsync(setup, user, org, Unique.Slug("checkout"));
        }

        var host = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(database),
            org,
            project,
            RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: "jti-report-1"),
            _store.Configuration());

        var runId = await RunRowSeeder.SeedRunAsync(_fixture, database, org);

        // `created` is never persisted: the create transaction inserts the run `queued` and that edge has no caller, so
        // the world starts in the state a report can move on.
        await _fixture.ExecuteAsync(
            database,
            $"update public.run set state = 'queued' where id = '{runId:D}'");

        return new World(host, database, org, project, runId);
    }

    /// <summary>Moves the seeded run into one state, as the owner seam the reconciler's tests also use.</summary>
    private async Task SetRunStateAsync(World world, string sql) =>
        await _fixture.ExecuteAsync(world.Database, sql + $" where id = '{world.RunId:D}'");

    /// <summary>
    /// Asserts a refused or skipped attempt left no evidence behind in this tenant: no event, attempt, receipt or slot,
    /// and exactly the outbox rows its own committed refusal row earns — one reconcile trigger for a refusal, none for a
    /// skip — with no other outbox row of any kind.
    /// </summary>
    private async Task AssertNothingWrittenAsync(World world, Guid artifactId)
    {
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        (await CountAsync(world, "case_attempt")).ShouldBe(0);
        (await CountAsync(world, "event_batch")).ShouldBe(0);
        (await CountAsync(world, "producer_slot")).ShouldBe(0);

        var ownTrigger = $"task_type = '{RunReconcileContract.OutboxTaskType}' "
            + $"and (payload ->> 'producer_id') = 'junit-{artifactId:D}'";
        var refusals = await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.raw_report where org_id = '{world.Org:D}' "
            + $"and artifact_id = '{artifactId:D}'");

        // One trigger per committed refusal row, so a skip still proves exactly zero and a refusal exactly one.
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where org_id = '{world.Org:D}' and {ownTrigger}"))
            .ShouldBe(refusals);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where org_id = '{world.Org:D}' and not ({ownTrigger})"))
            .ShouldBe(0);
    }

    /// <summary>
    /// The call the task consumer makes, minus the queue. The runner is the child's stand-in: unless a test scripts one,
    /// it answers with the record stream the real child would write for these bytes.
    /// </summary>
    private static async Task<ParseRawReportOutcome> ParseAsync(
        World world,
        Guid artifactId,
        string producerKind,
        Guid? runId = null,
        IParseRunner? runner = null) =>
        await CreateHandler(world, runner).ExecuteAsync(
            new ParseRawReportPayload(runId ?? world.RunId, artifactId, producerKind),
            world.Org,
            Token);

    private Task<long> CountAsync(World world, string table, Guid? artifactId = null) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            artifactId is null
                ? $"select count(*) from public.{table} where org_id = '{world.Org:D}'"
                : $"select count(*) from public.{table} where artifact_ids = array['{artifactId:D}'::uuid]");

    /// <summary>Counts one task type's outbox rows; one parse commit writes more than one kind.</summary>
    private Task<long> OutboxRowsAsync(World world, string taskType) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where org_id = '{world.Org:D}' and task_type = '{taskType}'");
}
