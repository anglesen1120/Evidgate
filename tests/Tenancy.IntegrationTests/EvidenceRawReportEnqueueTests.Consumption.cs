// The consumption half of the raw-report mapping path: the `raw_report_parse` payload the completion scheduled,
// run through the production handler and asserted by SQL. Kept apart from the facts so neither file grows past the
// size bound; the type is partial, so the worlds and helpers are the class's own.
using System.Text.Json;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Ingestion.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportEnqueueTests
{
    private static readonly byte[] MappableUploadBody =
        PadToCap("<testsuite tests=\"1\"><testcase name=\"probe\" time=\"0.012\"/></testsuite>");

    private static readonly JsonSerializerOptions ReportPayloadJson =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [Fact]
    public async Task RawReport_CompletionReplay_KeepsOneParseAndOneMeteredBatch()
    {
        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunAsync(world, ProducerKind);
        var artifactId = await MintAndUploadAsync(world, runId, "raw_report", XmlType, MappableUploadBody);
        var key = NewKey();

        await CompleteSucceedsAsync(world, artifactId, key);
        (await ConsumeEnqueuedReportAsync(world, artifactId)).Events.ShouldBe(1);
        await CompleteSucceedsAsync(world, artifactId, key);
        await CompleteSucceedsAsync(world, artifactId);
        (await ConsumeEnqueuedReportAsync(world, artifactId)).Events.ShouldBe(0);

        (await OutboxCountAsync(world, ReportParseTask, artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, "artifact_parse", artifactId)).ShouldBe(1);
        (await TenantRowsAsync(world, "raw_report")).ShouldBe(1);
        (await TenantRowsAsync(world, "event_batch")).ShouldBe(1);
        (await UsageRowsAsync(world)).ShouldBe(1);
        (await _fixture.ScalarAsync<int>(world.Database,
            $"select sum(accepted)::int from public.event_batch where org_id = '{world.Org:D}'")).ShouldBe(1);
    }

    [Fact]
    public async Task RawReport_TwoArtifactsForOneRun_KeepDistinctParsesAndMeterBoth()
    {
        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunAsync(world, ProducerKind);
        var first = await MintAndUploadAsync(world, runId, "raw_report", XmlType, MappableUploadBody);
        var second = await MintAndUploadAsync(world, runId, "raw_report", XmlType, MappableUploadBody);
        first.ShouldNotBe(second);

        await CompleteSucceedsAsync(world, first);
        await CompleteSucceedsAsync(world, second);
        (await ConsumeEnqueuedReportAsync(world, first)).Events.ShouldBe(1);
        (await ConsumeEnqueuedReportAsync(world, second)).Events.ShouldBe(1);

        (await OutboxCountForRunAsync(world, runId)).ShouldBe(2);
        (await DistinctArtifactsScheduledAsync(world, runId)).ShouldBe(2);
        (await IdempotencyKeyAsync(world, first)).ShouldNotBe(await IdempotencyKeyAsync(world, second));
        (await TenantRowsAsync(world, "raw_report")).ShouldBe(2);
        (await TenantRowsAsync(world, "event")).ShouldBe(2);
        (await TenantRowsAsync(world, "event_batch")).ShouldBe(2);
        (await UsageRowsAsync(world)).ShouldBe(2);
    }

    [Fact]
    public async Task RawReport_Hostile64CharacterHint_LeavesOneInvalidRefusalWithoutLeakingIt()
    {
        var hostile = HostileProducerHint();
        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunAsync(world, hostile);
        var artifactId = await MintAndUploadAsync(world, runId, "raw_report", XmlType, MappableUploadBody);
        using var listener = CaptureIssues(out var issues);

        await CompleteSucceedsAsync(world, artifactId);
        await AssertInvalidHintRefusedAsync(world, artifactId);

        issues.ShouldContain(ParseRawReportPayload.ProducerInvalid);
        issues.ShouldNotContain(hostile, "the hint is untrusted plan text and never becomes a metric tag");
        (await _fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.run where id = '{runId:D}'")).ShouldBe(1);
    }

    [Theory]
    [InlineData("PyTest")]
    [InlineData("pytest_junitxml")]
    public async Task RawReport_UnstorableProducerHint_LeavesOneInvalidRefusal(string hint)
    {
        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunAsync(world, hint);
        var artifactId = await MintAndUploadAsync(world, runId, "raw_report", XmlType, MappableUploadBody);
        using var listener = CaptureIssues(out var issues);

        await CompleteSucceedsAsync(world, artifactId);
        await AssertInvalidHintRefusedAsync(world, artifactId);

        issues.ShouldContain(ParseRawReportPayload.ProducerInvalid);
        issues.ShouldNotContain(hint);
    }

    [Theory]
    [InlineData("{\"shards\":{\"shard_id\":\"shard-1\",\"producer_hint\":\"pytest-junitxml\"}}")]
    [InlineData("{\"shards\":\"pytest-junitxml\"}")]
    public async Task RawReport_NonArrayShardPlan_StoresTheUnknownIssueAndNoServerError(string shardPlan)
    {
        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunWithShardPlanAsync(world, shardPlan);
        var artifactId = await MintAndUploadAsync(world, runId, "raw_report", XmlType, MappableUploadBody);

        // CompleteSucceedsAsync asserts the 200 and the `ready` state: a shard plan the read cannot walk must not
        // turn the lookup into a server error.
        await CompleteSucceedsAsync(world, artifactId);

        (await PayloadFieldAsync(world, artifactId, "producer_kind")).ShouldBeNull();
        (await PayloadFieldAsync(world, artifactId, "producer_issue")).ShouldBe(ParseRawReportPayload.ProducerUnknown);
        (await ConsumeEnqueuedReportAsync(world, artifactId)).Status.ShouldBe(ParseRawReportOutcome.Unsupported);

        (await TenantRowsAsync(world, "raw_report")).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(world.Database,
            $"select reason from public.raw_report where artifact_id = '{artifactId:D}'"))
            .ShouldBe(ParseRawReportPayload.ProducerUnknown);
        (await TenantRowsAsync(world, "event")).ShouldBe(0);
        (await UsageRowsAsync(world)).ShouldBe(0);
    }

    /// <summary>
    /// The completion schedules <c>artifact_parse</c> before <c>raw_report_parse</c> in one transaction, so the report
    /// task can find its artifact already rejected by the parse of its own bytes. That verdict is the report's refusal:
    /// exactly one <c>malformed</c> row naming the parse's code and the registry's parser pair, no byte read, no event,
    /// and no second row on a replay.
    /// </summary>
    [Fact]
    public async Task RawReport_ArtifactRejectedByItsParse_StoresOneMalformedReport()
    {
        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunAsync(world, ProducerKind);
        var artifactId = await MintAndUploadAsync(world, runId, "raw_report", XmlType, DtdUploadBody);
        await CompleteSucceedsAsync(world, artifactId);

        await RunArtifactParseAsync(world, artifactId);

        (await _fixture.ScalarAsync<string>(world.Database,
            $"select state || '/' || failure_code from public.artifact where id = '{artifactId:D}'"))
            .ShouldBe($"rejected/{ParsingFailureReason.DtdProhibited}");

        var first = await ConsumeEnqueuedReportAsync(world, artifactId);
        first.Status.ShouldBe(ParseRawReportOutcome.Malformed);

        var replay = await ConsumeEnqueuedReportAsync(world, artifactId);
        replay.Events.ShouldBe(0);

        (await TenantRowsAsync(world, "raw_report")).ShouldBe(1, "the parse verdict must not leave the report without a row");
        (await _fixture.ScalarAsync<string>(world.Database,
            $"select outcome || '/' || reason || '/' || producer_kind from public.raw_report where artifact_id = '{artifactId:D}'"))
            .ShouldBe($"malformed/{ParsingFailureReason.DtdProhibited}/{ProducerKind}");
        (await _fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.raw_report where artifact_id = '{artifactId:D}' and parser_id is not null"))
            .ShouldBe(1, "a malformed row carries the registry's parser pair");
        (await TenantRowsAsync(world, "event")).ShouldBe(0);
        (await TenantRowsAsync(world, "event_batch")).ShouldBe(0);
        (await UsageRowsAsync(world)).ShouldBe(0);
    }

    /// <summary>
    /// The parse refuses a report whose stored object no longer fits its kind's cap, so the artifact leaves evidence
    /// with <c>too_large</c>. The report task reads that verdict as the report's refusal rather than skipping a state
    /// that is no longer <c>ready</c>: one <c>malformed</c> row naming the code, no bytes read, no events. The code is
    /// also one the completion writes, and the row keeps nothing that tells the two writes apart, so this pins what
    /// the parse's own verdict must keep producing.
    /// </summary>
    [Fact]
    public async Task RawReport_ArtifactRejectedTooLargeByItsParse_StoresOneMalformedReport()
    {
        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunAsync(world, ProducerKind);
        var artifactId = await MintAndUploadAsync(world, runId, "raw_report", XmlType, MappableUploadBody);
        await CompleteSucceedsAsync(world, artifactId);

        (await LowerKindCapAsync(world, "raw_report", MappableUploadBody.LongLength - 1)).ShouldBe(1);
        await RunArtifactParseAsync(world, artifactId);

        (await _fixture.ScalarAsync<string>(world.Database,
            $"select state || '/' || failure_code from public.artifact where id = '{artifactId:D}'"))
            .ShouldBe("rejected/too_large");

        var outcome = await ConsumeEnqueuedReportAsync(world, artifactId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        outcome.Events.ShouldBe(0);
        (await TenantRowsAsync(world, "raw_report")).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(world.Database,
            $"select outcome || '/' || reason from public.raw_report where artifact_id = '{artifactId:D}'"))
            .ShouldBe("malformed/too_large");
        (await TenantRowsAsync(world, "event")).ShouldBe(0);
        (await UsageRowsAsync(world)).ShouldBe(0);
    }

    /// <summary>
    /// A rejection the completion wrote is not a parse verdict: no report was ever validly uploaded, so the report task
    /// skips it and writes nothing, exactly as it did before the parse-rejected path existed.
    /// </summary>
    [Fact]
    public async Task RawReport_ArtifactRejectedForAnUploadReason_WritesNoReportRow()
    {
        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunAsync(world, ProducerKind);
        var artifactId = await MintAndUploadAsync(world, runId, "raw_report", XmlType, MappableUploadBody);
        await CompleteSucceedsAsync(world, artifactId);
        await _fixture.ExecuteAsync(world.Database,
            $"update public.artifact set state = 'rejected', failure_code = 'checksum_mismatch' where id = '{artifactId:D}'");

        var outcome = await ConsumeEnqueuedReportAsync(world, artifactId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Skipped);
        outcome.Events.ShouldBe(0);
        (await TenantRowsAsync(world, "raw_report")).ShouldBe(0, "a report that never uploaded validly leaves no report row");
        (await TenantRowsAsync(world, "event")).ShouldBe(0);
    }

    [Fact]
    public async Task RawReport_UnregisteredProducerHint_StoresOneUnsupportedReport()
    {
        var hint = "unregistered-dialect";
        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunAsync(world, hint);
        var artifactId = await MintAndUploadAsync(world, runId, "raw_report", XmlType, MappableUploadBody);
        await CompleteSucceedsAsync(world, artifactId);

        var outcome = await ConsumeEnqueuedReportAsync(world, artifactId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Unsupported);
        outcome.Events.ShouldBe(0);
        (await TenantRowsAsync(world, "raw_report"))
            .ShouldBe(1, "an unsupported producer must leave a durable refusal, not silently disappear");
        (await _fixture.ScalarAsync<string>(world.Database,
            $"select outcome from public.raw_report where artifact_id = '{artifactId:D}'")).ShouldBe("unsupported");
        (await TenantRowsAsync(world, "event")).ShouldBe(0);
        (await TenantRowsAsync(world, "event_batch")).ShouldBe(0);
        (await UsageRowsAsync(world)).ShouldBe(0);
    }

    [Theory]
    [InlineData("complete", 0)]
    [InlineData("canceled", 1)]
    public async Task RawReport_CompletedUploadForTerminalRun_ObeysAdmissionWithoutReopeningRun(
        string state, int expectedEvents)
    {
        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunAsync(world, ProducerKind);
        var artifactId = await MintAndUploadAsync(world, runId, "raw_report", XmlType, MappableUploadBody);
        await _fixture.ExecuteAsync(world.Database, $"update public.run set state = '{state}' where id = '{runId:D}'");

        await CompleteSucceedsAsync(world, artifactId);
        (await OutboxCountAsync(world, ReportParseTask, artifactId)).ShouldBe(1);
        var outcome = await ConsumeEnqueuedReportAsync(world, artifactId);

        outcome.Events.ShouldBe(expectedEvents);
        outcome.Status.ShouldBe(expectedEvents == 0 ? ParseRawReportOutcome.Skipped : ParseRawReportOutcome.Parsed);
        (await _fixture.ScalarAsync<string>(world.Database,
            $"select state from public.run where id = '{runId:D}'")).ShouldBe(state);
        (await TenantRowsAsync(world, "event_batch")).ShouldBe(expectedEvents);
        (await UsageRowsAsync(world)).ShouldBe(expectedEvents);
        if (expectedEvents > 0)
        {
            (await _fixture.ScalarAsync<bool>(world.Database,
                $"select after_cancel from public.event where org_id = '{world.Org:D}'")).ShouldBeTrue();
        }
        else
        {
            (await TenantRowsAsync(world, "raw_report")).ShouldBe(0);
            (await TenantRowsAsync(world, "event")).ShouldBe(0);
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task RawReport_OutOfProcessSwitch_ControlsBothParseTasks(bool enabled, int expected)
    {
        await using var world = await StartAsync(outOfProcess: enabled);
        var runId = await SeedRunAsync(world, ProducerKind);
        var artifactId = await MintAndUploadAsync(world, runId, "raw_report", XmlType, MappableUploadBody);

        await CompleteSucceedsAsync(world, artifactId);

        (await OutboxCountAsync(world, "artifact_parse", artifactId)).ShouldBe(expected);
        (await OutboxCountAsync(world, ReportParseTask, artifactId)).ShouldBe(expected);
        (await OutboxCountAsync(world, "artifact_secret_scan", artifactId)).ShouldBe(1);
        (await OutboxCountAsync(world, "artifact_staging_delete", artifactId)).ShouldBe(1);
    }

    [Theory]
    [InlineData("{\"shards\":[]}")]
    [InlineData("{\"shards\":[{\"shard_id\":\"shard-1\",\"from_index\":0,\"to_index\":1,\"producer_hint\":null}]}")]
    public async Task RawReport_EmptyOrNullProducerHints_StoreUnknownIssueOnce(string shardPlan)
    {
        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunWithShardPlanAsync(world, shardPlan);
        var artifactId = await MintAndUploadAsync(world, runId, "raw_report", XmlType, MappableUploadBody);

        await CompleteSucceedsAsync(world, artifactId);
        (await PayloadFieldAsync(world, artifactId, "producer_kind")).ShouldBeNull();
        (await PayloadFieldAsync(world, artifactId, "producer_issue")).ShouldBe(ProducerUnknown);
        (await ConsumeEnqueuedReportAsync(world, artifactId)).Status.ShouldBe(ParseRawReportOutcome.Unsupported);
        (await ConsumeEnqueuedReportAsync(world, artifactId)).Status.ShouldBe(ParseRawReportOutcome.Unsupported);

        (await TenantRowsAsync(world, "raw_report")).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(world.Database,
            $"select producer_kind from public.raw_report where artifact_id = '{artifactId:D}'"))
            .ShouldBe(ParseRawReportPayload.UnresolvedProducerKind);
        (await _fixture.ScalarAsync<string>(world.Database,
            $"select reason from public.raw_report where artifact_id = '{artifactId:D}'")).ShouldBe(ProducerUnknown);
        (await TenantRowsAsync(world, "event")).ShouldBe(0);
        (await UsageRowsAsync(world)).ShouldBe(0);
    }

    [Theory]
    [InlineData(64, false)]
    [InlineData(65, true)]
    public async Task RawReport_ProducerHintLengthBoundary_UsesTheKindOrTheInvalidIssue(int length, bool invalid)
    {
        var hint = new string('k', length);
        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunAsync(world, hint);
        var artifactId = await MintAndUploadAsync(world, runId, "raw_report", XmlType, MappableUploadBody);

        await CompleteSucceedsAsync(world, artifactId);

        if (invalid)
        {
            // The column's ceiling is part of its shape, so a longer hint is the invalid issue, not a silent drop.
            await AssertInvalidHintRefusedAsync(world, artifactId);
            return;
        }

        (await PayloadFieldAsync(world, artifactId, "producer_kind")).ShouldBe(hint);
        (await PayloadFieldAsync(world, artifactId, "producer_issue")).ShouldBeNull();
        (await ConsumeEnqueuedReportAsync(world, artifactId)).Status.ShouldBe(ParseRawReportOutcome.Unsupported);
        (await TenantRowsAsync(world, "raw_report")).ShouldBe(1);
        (await UsageRowsAsync(world)).ShouldBe(0);
    }

    /// <summary>
    /// A storable hint beside an unstorable one is still two declarations: the shards do not agree on a kind, so the
    /// payload carries the ambiguous issue rather than picking the one the column could store.
    /// </summary>
    [Fact]
    public async Task RawReport_StorableAndUnstorableHints_AreAmbiguous()
    {
        await using var world = await StartAsync(outOfProcess: true);
        var runId = await SeedRunAsync(world, ProducerKind, new string('k', 65));
        var artifactId = await MintAndUploadAsync(world, runId, "raw_report", XmlType, MappableUploadBody);

        await CompleteSucceedsAsync(world, artifactId);

        (await PayloadFieldAsync(world, artifactId, "producer_kind")).ShouldBeNull();
        (await PayloadFieldAsync(world, artifactId, "producer_issue"))
            .ShouldBe(ParseRawReportPayload.ProducerAmbiguous);
    }

    private static string HostileProducerHint()
    {
        // PostgreSQL jsonb cannot store U+0000; use its visible symbol and a literal backslash-zero, plus real controls.
        var prefix = "'\\\"; DROP TABLE public.run;--\\0\u2400\u0001\u001f\u007fé漢";
        var hint = prefix + new string('x', 64 - prefix.Length);
        hint.Length.ShouldBe(64);
        return hint;
    }

    /// <summary>A report whose own parse refuses it (the profile prohibits a DTD), padded to the kind policy's cap.</summary>
    private static readonly byte[] DtdUploadBody =
        PadToCap("<!DOCTYPE testsuite [<!ENTITY x \"y\">]><testsuite><testcase name=\"probe\" time=\"0.012\"/></testsuite>");

    /// <summary>
    /// The whole path of a hint the column cannot store: the completion carries the invalid issue and no kind, the
    /// report task refuses before reading, and the durable outcome is exactly one <c>unsupported</c> row under the fixed
    /// producer kind word — the report never disappears, and a replay adds nothing.
    /// </summary>
    /// <param name="world">The tenant.</param>
    /// <param name="artifactId">The report artifact whose run declared the unusable hint.</param>
    private async Task AssertInvalidHintRefusedAsync(World world, Guid artifactId)
    {
        (await PayloadFieldAsync(world, artifactId, "producer_kind")).ShouldBeNull();
        (await PayloadFieldAsync(world, artifactId, "producer_issue"))
            .ShouldBe(ParseRawReportPayload.ProducerInvalid);

        var first = await ConsumeEnqueuedReportAsync(world, artifactId);
        first.Status.ShouldBe(ParseRawReportOutcome.Unsupported);

        var replay = await ConsumeEnqueuedReportAsync(world, artifactId);
        replay.Events.ShouldBe(0);

        (await TenantRowsAsync(world, "raw_report"))
            .ShouldBe(1, "a hint the column cannot store must still leave one durable refusal");
        (await _fixture.ScalarAsync<string>(world.Database,
            $"select producer_kind || '/' || outcome || '/' || reason from public.raw_report where artifact_id = '{artifactId:D}'"))
            .ShouldBe(
                $"{ParseRawReportPayload.UnresolvedProducerKind}/unsupported/{ParseRawReportPayload.ProducerInvalid}");
        (await _fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.raw_report where artifact_id = '{artifactId:D}' and parser_id is null"))
            .ShouldBe(1, "no parser judged the report, so the row's key is the kind it could not declare");
        (await TenantRowsAsync(world, "event")).ShouldBe(0);
        (await TenantRowsAsync(world, "event_batch")).ShouldBe(0);
        (await UsageRowsAsync(world)).ShouldBe(0);
    }

    /// <summary>
    /// One <c>artifact_parse</c> attempt through the production handler and the adapter the task consumer uses, with
    /// the emulated child the report tests already drive: the same code that rejects a report's bytes before the report
    /// task runs.
    /// </summary>
    /// <param name="world">The tenant the artifact belongs to.</param>
    /// <param name="artifactId">The report artifact to parse.</param>
    private static async Task RunArtifactParseAsync(World world, Guid artifactId)
    {
        var services = world.Host.Services;
        var handler = new ParseArtifactTaskHandler(
            services.GetRequiredService<ITenantDbContextFactory<EvidenceDbContext>>(),
            services.GetRequiredService<IObjectStorage>(),
            new EmulatedChildRunner(services.GetRequiredService<IParsing>()),
            services.GetRequiredService<IOptions<IngestionLimits>>(),
            NullLogger<ParseArtifactTaskHandler>.Instance);

        var invoker = new TaskHandlerAdapter<ParseArtifactPayload>(handler);
        var payload = JsonSerializer.Serialize(new { artifact_id = artifactId });
        var token = TestContext.Current.CancellationToken;
        var factory = services.GetRequiredService<ITenantScopeFactory>();

        await using var scope = await factory.BeginAsync(world.Org, world.Project, cancellationToken: token);
        await invoker.InvokeAsync(payload, scope, token);
        await scope.CommitAsync(token);
    }

    private async Task<ParseRawReportOutcome> ConsumeEnqueuedReportAsync(World world, Guid artifactId)
    {
        var json = await _fixture.ScalarAsync<string>(world.Database,
            $"select payload::text from public.outbox where task_type = '{ReportParseTask}' "
            + $"and payload->>'artifact_id' = '{artifactId:D}'");
        var payload = JsonSerializer.Deserialize<ParseRawReportPayload>(json, ReportPayloadJson);
        payload.ShouldNotBeNull();
        var services = world.Host.Services;
        var runner = new EmulatedChildRunner(services.GetRequiredService<IParsing>());
        var handler = ActivatorUtilities.CreateInstance<ParseRawReportHandler>(services, runner);
        return await handler.ExecuteAsync(payload, world.Org, TestContext.Current.CancellationToken);
    }

    private Task<long> TenantRowsAsync(World world, string table) =>
        _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.{table} where org_id = '{world.Org:D}'");

    private Task<long> UsageRowsAsync(World world) =>
        _fixture.ScalarAsync<long>(world.Database,
            $"select count(*) from public.outbox where org_id = '{world.Org:D}' and task_type = 'usage.batch_accepted'");

    private async Task<Guid> SeedRunWithShardPlanAsync(World world, string shardPlan)
    {
        var templateRun = await SeedRunAsync(world);
        var manifestId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var hash = "sha256:" + manifestId.ToString("N") + manifestId.ToString("N");
        var plan = shardPlan.Replace("'", "''", StringComparison.Ordinal);
        await _fixture.ExecuteAsync(world.Database, $$"""
            insert into public.manifest (id, org_id, project_id, manifest_hash, schema_version, suite_revision_id,
                environment_revision_id, shard_plan, matrix, env_needs, planned_count, document, verified_by)
            select '{{manifestId}}', m.org_id, m.project_id, '{{hash}}', m.schema_version, m.suite_revision_id,
                m.environment_revision_id, '{{plan}}'::jsonb, m.matrix, m.env_needs, m.planned_count, m.document, m.verified_by
            from public.manifest m join public.run r on r.org_id = m.org_id and r.manifest_id = m.id
            where r.id = '{{templateRun}}';
            insert into public.run (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id,
                collection_deadline_at, late_window_minutes)
            select '{{runId}}', org_id, project_id, '{{manifestId}}', suite_revision_id, environment_revision_id,
                collection_deadline_at, late_window_minutes from public.run where id = '{{templateRun}}';
            insert into public.planned_execution (id, org_id, run_id, execution_key, case_revision_id, variant_hash,
                target_env, shard_id, required)
            select gen_random_uuid(), org_id, '{{runId}}', execution_key, case_revision_id, variant_hash,
                target_env, shard_id, required from public.planned_execution where run_id = '{{templateRun}}';
            insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id, bound_token_jti)
            select gen_random_uuid(), org_id, '{{runId}}', shard_index, producer_id, bound_token_jti
            from public.producer_slot where run_id = '{{templateRun}}';
            """);
        return runId;
    }
}
