// The trigger half of a refusal: a report whose row is committed leaves one reconcile row in the same transaction, a
// replay leaves none, and the whole-object read happens once per attempt. Real PostgreSQL and a real SeaweedFS
// container through the shared harness; the handler is built directly so the storage seam can count its reads.
using System.Globalization;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// What a refusal leaves behind besides its row: one <c>orchestrator.run.reconcile</c> trigger per refusal row, no
/// second row or trigger on a replay, and no second read of the stored object. One artifact may hold several refusal
/// rows — an unsupported verdict and a malformed one under a parser — so each row's trigger names that row and the
/// two coexist instead of deduping each other away.
/// </summary>
public sealed partial class EvidenceRawReportParseTests
{
    /// <summary>The synthetic producer id prefix the mapper's slot and this trigger share.</summary>
    private const string SyntheticProducerPrefix = "junit-";

    /// <summary>
    /// A document truncated inside its second testcase: the first case is complete and mappable, so only an all-or-nothing
    /// refusal keeps it out of the event set.
    /// </summary>
    private static readonly byte[] TruncatedInsideTestcaseBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites>
          <testsuite name="pytest" tests="2">
            <testcase name="probe" time="0.009"/>
            <testcase name="ghost_case" time="0.004"><failure message="boom
        """u8.ToArray();

    [Fact]
    public async Task Malformed_Report_Commits_Its_Row_With_One_Reconcile_Trigger()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, MalformedBody);

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        (await RawReportCountAsync(world, artifactId)).ShouldBe(1);
        Text(await RawReportAsync(world, artifactId), "outcome").ShouldBe("malformed");
        await AssertOneRefusalTriggerAsync(world, artifactId);
        (await RunStateAsync(world)).ShouldBe("queued", "a refusal transitions nothing");
    }

    [Fact]
    public async Task Unsupported_Producer_Kind_Commits_Its_Row_With_One_Reconcile_Trigger()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);

        var outcome = await ParseAsync(world, artifactId, UnknownKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Unsupported);
        Text(await RawReportAsync(world, artifactId), "outcome").ShouldBe("unsupported");
        await AssertOneRefusalTriggerAsync(world, artifactId);
    }

    [Fact]
    public async Task Refusal_Replay_Writes_No_Second_Row_Or_Trigger()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, MalformedBody);

        var first = await ParseAsync(world, artifactId, ProducerKind);
        var replay = await ParseAsync(world, artifactId, ProducerKind);

        first.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        replay.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        (await RawReportCountAsync(world, artifactId)).ShouldBe(1);
        await AssertOneRefusalTriggerAsync(world, artifactId);
    }

    [Fact]
    public async Task Refusal_Then_Mapped_Report_Of_One_Run_Write_Two_Distinct_Triggers()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var refused = await SeedArtifactAsync(world, MalformedBody);
        (await ParseAsync(world, refused, ProducerKind)).Status.ShouldBe(ParseRawReportOutcome.Malformed);
        var mapped = await SeedArtifactAsync(world, MappedBody);

        var parsed = await ParseAsync(world, mapped, ProducerKind);

        parsed.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        var keys = await _fixture.ColumnAsync<string>(
            world.Database,
            $"select idempotency_key from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunReconcileContract.OutboxTaskType}' order by idempotency_key");
        keys.Count.ShouldBe(2, "the outbox dedupes on (org, key) and the two rows name their own evidence");
        var batchId = await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.event_batch where org_id = '{world.Org:D}'");
        var refusedRow = await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.raw_report where artifact_id = '{refused:D}'");
        keys.ShouldContain(RunReconcileContract.BatchIdempotencyKey(world.RunId, SyntheticProducerPrefix + refused.ToString("D", CultureInfo.InvariantCulture), refusedRow));
        keys.ShouldContain(RunReconcileContract.BatchIdempotencyKey(world.RunId, SyntheticProducerPrefix + mapped.ToString("D", CultureInfo.InvariantCulture), batchId));
        keys[0].ShouldNotBe(keys[1]);
    }

    [Fact]
    public async Task Two_Refusals_Of_One_Artifact_Commit_One_Row_And_One_Trigger_Each()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, MalformedBody);

        // A kind no parser names leaves the unsupported row, keyed by the kind the report declared; the same bytes
        // under a registered kind leave the malformed row, keyed by the parser release. One artifact therefore holds
        // two refusal rows, and each one's trigger must survive the other's.
        var unsupported = await ParseAsync(world, artifactId, UnknownKind);
        var malformed = await ParseAsync(world, artifactId, ProducerKind);

        unsupported.Status.ShouldBe(ParseRawReportOutcome.Unsupported);
        malformed.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        (await RawReportCountAsync(world, artifactId)).ShouldBe(2);
        var rawReportIds = await _fixture.ColumnAsync<Guid>(
            world.Database,
            $"select id from public.raw_report where org_id = '{world.Org:D}' and artifact_id = '{artifactId:D}'");
        rawReportIds.Count.ShouldBe(2);

        var producerId = SyntheticProducerPrefix + artifactId.ToString("D", CultureInfo.InvariantCulture);
        var keys = await _fixture.ColumnAsync<string>(
            world.Database,
            $"select idempotency_key from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunReconcileContract.OutboxTaskType}'");
        keys.ShouldBe(
            rawReportIds
                .Select(rowId => RunReconcileContract.BatchIdempotencyKey(world.RunId, producerId, rowId))
                .ToList(),
            ignoreOrder: true);
    }

    [Fact]
    public async Task PartialDocument_NoEventsCommitted_Truncated_After_A_Complete_Case()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, TruncatedInsideTestcaseBody);

        var outcome = await ParseAsync(world, artifactId, ProducerKind);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        outcome.Reason.ShouldBe(ParsingFailureReason.Malformed);
        var row = await RawReportAsync(world, artifactId);
        Text(row, "outcome").ShouldBe("malformed");
        Number(row, "observed_tests").ShouldBe(0);
        (await CountAsync(world, "event", artifactId)).ShouldBe(0, "the complete first case may not survive the truncation");
        (await CountAsync(world, "case_attempt")).ShouldBe(0);
        (await CountAsync(world, "producer_slot")).ShouldBe(0);
        (await CountAsync(world, "event_batch")).ShouldBe(0);
        await AssertOneRefusalTriggerAsync(world, artifactId);
    }

    [Fact]
    public async Task Malformed_Refusal_Reads_The_Object_Once_And_Not_For_Its_Row()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, MalformedBody);
        var storage = new CountingStorage(
            world.Host.Services.GetRequiredService<IObjectStorage>(),
            ObjectKeyFor(world, artifactId));

        var outcome = await ExecuteAsync(
            world,
            storage,
            new EmulatedChildRunner(world.Host.Services.GetRequiredService<IParsing>()),
            artifactId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        storage.ObjectReads.ShouldBe(1);
        storage.OtherCalls.ShouldBe(0, "the refusal row is written from the payload and never re-reads the object");
        await AssertOneRefusalTriggerAsync(world, artifactId);
    }

    [Fact]
    public async Task Report_Over_The_Byte_Cap_Is_Read_Once_And_Still_Commits_Its_Trigger()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity) "
            + $"values ('{Guid.CreateVersion7():D}', '{world.Org:D}', '{world.Project:D}', 'raw_report', 8, "
            + $"array['{XmlType}'], 'normal')");
        var storage = new CountingStorage(
            world.Host.Services.GetRequiredService<IObjectStorage>(),
            ObjectKeyFor(world, artifactId));
        var runner = new StubParseRunner();

        var outcome = await ExecuteAsync(world, storage, runner, artifactId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        outcome.Reason.ShouldBe("too_large");
        runner.Calls.ShouldBe(0);
        storage.ObjectReads.ShouldBe(1);
        storage.OtherCalls.ShouldBe(0);
        await AssertOneRefusalTriggerAsync(world, artifactId);
    }

    /// <summary>
    /// The one trigger a committed refusal row leaves: the run aggregate, the report's synthetic producer, a zero
    /// declaration, no late marker and a key naming that refusal row. The table's <c>(org, key)</c> dedupe makes it one
    /// row.
    /// </summary>
    private async Task AssertOneRefusalTriggerAsync(World world, Guid artifactId)
    {
        var producerId = SyntheticProducerPrefix + artifactId.ToString("D", CultureInfo.InvariantCulture);
        (await OutboxRowsAsync(world, RunReconcileContract.OutboxTaskType)).ShouldBe(1);
        var rawReportId = await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.raw_report where artifact_id = '{artifactId:D}'");

        var row = await _fixture.ScalarAsync<string>(
            world.Database,
            "select concat_ws('|', aggregate_type, aggregate_id::text, idempotency_key, payload ->> 'run_id', "
            + "payload ->> 'producer_id', payload ->> 'declared_count', payload ->> 'declared_last_sequence', "
            + "coalesce(payload ->> 'late_evidence', 'absent')) from public.outbox "
            + $"where org_id = '{world.Org:D}' and task_type = '{RunReconcileContract.OutboxTaskType}'");

        row.ShouldBe(string.Join(
            '|',
            RunReconcileContract.AggregateType,
            world.RunId.ToString("D", CultureInfo.InvariantCulture),
            RunReconcileContract.BatchIdempotencyKey(world.RunId, producerId, rawReportId),
            world.RunId.ToString("D", CultureInfo.InvariantCulture),
            producerId,
            "0",
            "0",
            "absent"));
    }

    /// <summary>The handler over a chosen storage seam, so the reads a refusal makes (and does not make) are counted.</summary>
    private static Task<ParseRawReportOutcome> ExecuteAsync(
        World world,
        IObjectStorage storage,
        IParseRunner runner,
        Guid artifactId)
    {
        var services = world.Host.Services;

        return new ParseRawReportHandler(
                services.GetRequiredService<ITenantScopeFactory>(),
                services.GetRequiredService<ITenantDbContextFactory<EvidenceDbContext>>(),
                services.GetRequiredService<ITenantDb>(),
                storage,
                services.GetRequiredService<IReportParserResolver>(),
                runner,
                services.GetRequiredService<IOptions<IngestionLimits>>(),
                services.GetServices<IJUnitMapper>(),
                services.GetRequiredService<IServiceScopeFactory>(),
                services.GetRequiredService<IOutboxWriter>(),
                services.GetRequiredService<IAuditTrail>(),
                NullLogger<ParseRawReportHandler>.Instance)
            .ExecuteAsync(new ParseRawReportPayload(world.RunId, artifactId, ProducerKind), world.Org, Token);
    }

    /// <summary>
    /// A test <see cref="IObjectStorage"/> over the real adapter: it counts the whole-object reads of one key and every
    /// other call, so a test proves the refusal path fetches the report once and never again.
    /// </summary>
    private sealed class CountingStorage(IObjectStorage inner, string watchedKey) : IObjectStorage
    {
        /// <summary>Whole-object reads of the watched key.</summary>
        public int ObjectReads { get; private set; }

        /// <summary>Every other call, including a read of another key.</summary>
        public int OtherCalls { get; private set; }

        public PresignedPut PresignPut(PresignPutRequest request)
        {
            OtherCalls++;
            return inner.PresignPut(request);
        }

        public PresignedGet PresignGet(string objectKey)
        {
            OtherCalls++;
            return inner.PresignGet(objectKey);
        }

        public Task<ObjectHead?> HeadAsync(string objectKey, CancellationToken cancellationToken)
        {
            OtherCalls++;
            return inner.HeadAsync(objectKey, cancellationToken);
        }

        public Task<byte[]?> ReadPrefixAsync(
            string objectKey,
            int maxBytes,
            CancellationToken cancellationToken,
            string? ifMatch = null)
        {
            OtherCalls++;
            return inner.ReadPrefixAsync(objectKey, maxBytes, cancellationToken, ifMatch);
        }

        public Task<byte[]?> GetAsync(string objectKey, long maxBytes, CancellationToken cancellationToken)
        {
            if (string.Equals(objectKey, watchedKey, StringComparison.Ordinal))
            {
                ObjectReads++;
            }
            else
            {
                OtherCalls++;
            }

            return inner.GetAsync(objectKey, maxBytes, cancellationToken);
        }

        public Task CopyAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken)
        {
            OtherCalls++;
            return inner.CopyAsync(sourceKey, destinationKey, cancellationToken);
        }

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
        {
            OtherCalls++;
            return inner.DeleteAsync(objectKey, cancellationToken);
        }
    }
}
