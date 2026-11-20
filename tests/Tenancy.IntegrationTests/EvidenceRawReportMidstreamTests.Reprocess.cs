using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
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

// The same midstream cases as the initial parse, through `ingest.reprocess_report`, over a source report a real parse
// already turned into evidence: the pinned release maps that report's one case and the child then fails, so the
// refusal must be the attempt's only write and the original parse's event, attempt, slot and trigger plus the source
// report row must be exactly what they were.
public sealed partial class ReprocessReportTaskTests
{
    [Fact]
    public async Task RecordCapHitAfterAMappedCase_WritesOnlyTheRefusal() =>
        await AssertRefusedAfterMappedCaseAsync(MidstreamOutcome.RecordCapHit, ParseRunRejection.Crashed);

    [Fact]
    public async Task ByteCapHitAfterAMappedCase_WritesOnlyTheRefusal() =>
        await AssertRefusedAfterMappedCaseAsync(MidstreamOutcome.ByteCapHit, ParseRunRejection.Crashed);

    [Fact]
    public async Task MalformedFrameAfterAMappedCase_WritesOnlyTheRefusal() =>
        await AssertRefusedAfterMappedCaseAsync(MidstreamOutcome.MalformedFrame, ParseRunRejection.Crashed);

    [Fact]
    public async Task BrokenPipeAfterAMappedCase_WritesOnlyTheRefusal() =>
        await AssertRefusedAfterMappedCaseAsync(MidstreamOutcome.BrokenPipe, ParseRunRejection.Crashed);

    [Fact]
    public async Task KilledChildAfterAMappedCase_WritesOnlyTheRefusal() =>
        await AssertRefusedAfterMappedCaseAsync(MidstreamOutcome.Killed, ParseRunRejection.Crashed);

    [Fact]
    public async Task WallClockAfterAMappedCase_WritesOnlyTheTimeoutRefusal() =>
        await AssertRefusedAfterMappedCaseAsync(MidstreamOutcome.Stalled, ParseRunRejection.Timeout);

    [Fact]
    public async Task HeapLimitExitAfterAMappedCase_WritesOnlyTheOomRefusal() =>
        await AssertRefusedAfterMappedCaseAsync(MidstreamOutcome.MemoryLimit, ParseRunRejection.Oom);

    [Fact]
    public async Task ChildRejectionAfterAMismatchedEnd_WinsOverTheMapperFault() =>
        await AssertRefusedAfterMappedCaseAsync(
            MidstreamOutcome.RejectedAfterMismatchedEnd,
            ParsingFailureReason.DtdProhibited);

    [Fact]
    public async Task ChildRejectionOfTheEntryBudget_IsTheCapExceededRefusal() =>
        await AssertRefusedAfterMappedCaseAsync(MidstreamOutcome.RejectedCapExceeded, ArtifactFailureCode.CapExceeded);

    [Fact]
    public async Task Control_ACompleteStreamAfterTheMappedCase_WritesItsEventAndLeavesTheSourceUntouched()
    {
        var world = await StartAsync();
        await using var _ = world;
        var seeded = await SeedAsync(world);
        var evidenceBefore = await SnapshotAsync(world, seeded.SourceProducer);
        var sourceRow = await SourceRowAsync(world, seeded.SourceReportId);
        var producer = $"reprocess-{seeded.ParsingId:D}";

        var result = await ReprocessAsync(world, seeded.ParsingId, MidstreamChildRunner.For(MidstreamOutcome.Complete));

        result.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        result.Events.ShouldBe(1, "the control proves the prefix really maps the planned case");
        (await ProducerRowsAsync(world, "event", producer)).ShouldBe(1);
        (await ProducerRowsAsync(world, "producer_slot", producer)).ShouldBe(1);
        (await CountAsync(world, "case_attempt")).ShouldBe(1, "the reprocess adds no attempt row");

        await AssertSourceIntactAsync(world, evidenceBefore);
        (await SourceRowAsync(world, seeded.SourceReportId)).ShouldBe(sourceRow);
    }

    private async Task AssertRefusedAfterMappedCaseAsync(MidstreamOutcome midstream, string reason)
    {
        var world = await StartAsync();
        await using var _ = world;
        var seeded = await SeedAsync(world);
        var evidenceBefore = await SnapshotAsync(world, seeded.SourceProducer);
        var sourceRow = await SourceRowAsync(world, seeded.SourceReportId);

        var result = await ReprocessAsync(world, seeded.ParsingId, MidstreamChildRunner.For(midstream));

        result.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        result.Reason.ShouldBe(reason);
        result.Events.ShouldBe(0);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select outcome || '|' || reason from public.raw_report where id = '{seeded.ParsingId:D}'"))
            .ShouldBe(RawReportOutcomes.Malformed + "|" + reason);

        await AssertNoReprocessEvidenceAsync(world, seeded.ParsingId);
        await AssertSourceIntactAsync(world, evidenceBefore);
        (await SourceRowAsync(world, seeded.SourceReportId)).ShouldBe(sourceRow);
    }

    /// <summary>The world with its report already parsed once, so the reprocess has real evidence to leave alone.</summary>
    private async Task<(Guid ArtifactId, Guid SourceReportId, string SourceProducer, Guid ParsingId)> SeedAsync(World world)
    {
        await SeedPlanAsync(world);
        await MidstreamScripts.SeedOneCaseAsync(_fixture, world.Database, world.Org, world.Project, world.RunId);
        var artifactId = await SeedArtifactAsync(world, MidstreamScripts.OneCaseBody);
        var sourceReportId = await ParseSourceReportAsync(world, artifactId);
        var producer = $"junit-{artifactId:D}";

        // The source evidence is a real parse's, so a check that it survived cannot pass over an empty world: one
        // event, one attempt and one slot exist before the reprocess runs, and a deletion or rewrite of them is what
        // these cases look for.
        (await ProducerRowsAsync(world, "event", producer)).ShouldBe(1, "the source parse must leave evidence to protect");
        (await ProducerRowsAsync(world, "producer_slot", producer)).ShouldBe(1);
        (await CountAsync(world, "case_attempt")).ShouldBe(1);

        return (artifactId, sourceReportId, producer, await SeedRequestAsync(world, artifactId, sourceReportId, PinVersion));
    }

    /// <summary>The source report as the initial parse commits it: its row plus the evidence the reprocess must not move.</summary>
    private async Task<Guid> ParseSourceReportAsync(World world, Guid artifactId)
    {
        var outcome = await CreateParseHandler(world, MidstreamChildRunner.For(MidstreamOutcome.Complete))
            .ExecuteAsync(new ParseRawReportPayload(world.RunId, artifactId, ProducerKind), world.Org, Token);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed, "the source report must be the row a real parse writes");
        outcome.Events.ShouldBe(1);

        return await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.raw_report where org_id = '{world.Org:D}' and artifact_id = '{artifactId:D}'");
    }

    /// <summary>What a reprocess must leave alone: the source producer's rows, the run's attempt, and its trigger.</summary>
    private async Task<SourceEvidence> SnapshotAsync(World world, string producer) =>
        new(
            producer,
            await ProducerRowsAsync(world, "event", producer),
            await ProducerRowsAsync(world, "producer_slot", producer),
            await EvidenceRowsAsync(world, producer),
            await AttemptRowsAsync(world),
            await OutboxCountAsync(world),
            await OutboxForProducerAsync(world, producer));

    private async Task AssertSourceIntactAsync(World world, SourceEvidence before)
    {
        (await ProducerRowsAsync(world, "event", before.Producer)).ShouldBe(
            before.Events, "the source parse's events are the rows a reprocess may not touch");
        (await ProducerRowsAsync(world, "producer_slot", before.Producer)).ShouldBe(
            before.Slots, "the source parse's slot is still its own");
        (await EvidenceRowsAsync(world, before.Producer)).ShouldBe(
            before.Rows, "no source row was replaced under the same id or another");
        (await AttemptRowsAsync(world)).ShouldBe(before.Attempts, "a reprocess writes no attempt row");
        (await OutboxForProducerAsync(world, before.Producer)).ShouldBe(
            before.OutboxForProducer, "the source's reconcile trigger is still there");
        (await OutboxCountAsync(world)).ShouldBe(
            before.Outbox + 1, "the attempt's trigger is its whole outbox write, whatever the source already queued");
    }

    /// <summary>Nothing of an attempt that mapped a case before failing may land, scoped to that attempt's producer.</summary>
    private async Task AssertNoReprocessEvidenceAsync(World world, Guid parsingId)
    {
        var producer = $"reprocess-{parsingId:D}";

        (await ProducerRowsAsync(world, "event", producer)).ShouldBe(0);
        (await ProducerRowsAsync(world, "producer_slot", producer)).ShouldBe(0);
        (await ProducerRowsAsync(world, "event_batch", producer)).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunReconcileContract.OutboxTaskType}' "
            + $"and (payload ->> 'producer_id') = '{producer}'")).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunStateTransitionContract.OutboxTaskType}'")).ShouldBe(0);
    }

    /// <summary>The source report's own column values, so a reprocess can be shown not to have moved them.</summary>
    private Task<string> SourceRowAsync(World world, Guid sourceReportId) =>
        _fixture.ScalarAsync<string>(
            world.Database,
            "select concat_ws('|', outcome, parser_version, observed_tests, discrepancies::text) "
            + $"from public.raw_report where id = '{sourceReportId:D}'");

    /// <summary>One producer's event and slot rows as an id list, so a replaced row cannot hide behind a count.</summary>
    private async Task<string> EvidenceRowsAsync(World world, string producer) =>
        await IdsAsync(world, "event", producer) + "|" + await IdsAsync(world, "producer_slot", producer);

    private Task<string> AttemptRowsAsync(World world) => IdsAsync(world, "case_attempt", producer: null);

    private Task<string> IdsAsync(World world, string table, string? producer) =>
        _fixture.ScalarAsync<string>(
            world.Database,
            $"select coalesce(string_agg(id::text, ',' order by id), 'none') from public.{table} "
            + $"where org_id = '{world.Org:D}'"
            + (producer is null ? string.Empty : $" and producer_id = '{producer}'"));

    private Task<long> ProducerRowsAsync(World world, string table, string producer) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.{table} where org_id = '{world.Org:D}' and producer_id = '{producer}'");

    private Task<long> OutboxCountAsync(World world) =>
        _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.outbox where org_id = '{world.Org:D}'");

    private Task<long> OutboxForProducerAsync(World world, string producer) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where org_id = '{world.Org:D}' "
            + $"and (payload ->> 'producer_id') = '{producer}'");

    private static async Task<ParseRawReportOutcome> ReprocessAsync(
        World world,
        Guid parsingId,
        IParseRunner runner) =>
        await CreateReprocessHandler(world, runner).ExecuteAsync(new ReprocessReportPayload(parsingId), world.Org, Token);

    /// <summary>The production reprocess handler with its child replaced: the same scopes, store, registry and outbox.</summary>
    private static ReprocessReportHandler CreateReprocessHandler(World world, IParseRunner runner)
    {
        var services = world.Host.Services;

        return new ReprocessReportHandler(
            services.GetRequiredService<ITenantScopeFactory>(),
            services.GetRequiredService<ITenantDbContextFactory<EvidenceDbContext>>(),
            services.GetRequiredService<ITenantDb>(),
            services.GetRequiredService<IObjectStorage>(),
            runner,
            services.GetRequiredService<IOptions<IngestionLimits>>(),
            services.GetServices<IJUnitMapper>(),
            services.GetRequiredService<IOutboxWriter>(),
            NullLogger<ReprocessReportHandler>.Instance);
    }

    /// <summary>The production initial-parse handler, used once to commit the source evidence a reprocess must keep.</summary>
    private static ParseRawReportHandler CreateParseHandler(World world, IParseRunner runner)
    {
        var services = world.Host.Services;

        return new ParseRawReportHandler(
            services.GetRequiredService<ITenantScopeFactory>(),
            services.GetRequiredService<ITenantDbContextFactory<EvidenceDbContext>>(),
            services.GetRequiredService<ITenantDb>(),
            services.GetRequiredService<IObjectStorage>(),
            services.GetRequiredService<IReportParserResolver>(),
            runner,
            services.GetRequiredService<IOptions<IngestionLimits>>(),
            services.GetServices<IJUnitMapper>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IOutboxWriter>(),
            services.GetRequiredService<IAuditTrail>(),
            services.GetRequiredService<TimeProvider>(),
            NullLogger<ParseRawReportHandler>.Instance);
    }

    /// <summary>The source producer's evidence, fingerprinted by id so a count cannot hide a replacement.</summary>
    private sealed record SourceEvidence(
        string Producer,
        long Events,
        long Slots,
        string Rows,
        string Attempts,
        long Outbox,
        long OutboxForProducer);
}
