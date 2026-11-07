// The revision half of the reprocess path: the report a run already holds is parsed once through the production report
// task, the run's first evidence revision is frozen by the production reconcile task, and a reprocess at a newer release
// appends exactly one more revision without rewriting the old row, its events or the stored report.
using System.Text.Json;
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
using Evidgate.Modules.Orchestrator.Application.Runs;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReprocessReportTaskTests
{
    /// <summary>snake_case, the wire convention every payload of this platform writes.</summary>
    private static readonly JsonSerializerOptions ReconcilePayloadJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// The report a run already holds is parsed at the built-in release and frozen into the run's first revision; a
    /// reprocess that pins the newer release writes its own parsing row and trigger, and the reconcile of that trigger
    /// appends one further revision. The old revision keeps its identity and hashes, its events and the stored report are
    /// untouched, the run stays where it was, and the new report row is the request's own id.
    /// </summary>
    [Fact]
    public async Task Reprocess_NewParser_NewRevision()
    {
        var world = await StartAsync();
        await using var _ = world;

        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);

        var original = await ParseOriginalReportAsync(world, artifactId);
        original.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        original.Events.ShouldBe(1);
        var sourceReportId = await SourceReportIdAsync(world, artifactId);

        var first = await FreezeFirstRevisionAsync(world, artifactId, sourceReportId);

        var parsingId = await SeedRequestAsync(world, artifactId, sourceReportId, PinVersion);

        var outcome = await ReprocessAsync(world, parsingId);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(1);

        await ConsumeEnqueuedReconcileAsync(
            world,
            "reprocess-" + parsingId.ToString("D", System.Globalization.CultureInfo.InvariantCulture));

        await AssertFirstRevisionUntouchedAsync(world, artifactId, sourceReportId, first);
        await AssertSecondRevisionAsync(world, first);
        await AssertReprocessedRowAsync(world, parsingId);
        await AssertRunUnmovedAsync(world, first);
    }

    /// <summary>Everything the first revision froze and everything the original parse left behind.</summary>
    private sealed record FirstRevision(
        string RevisionHash,
        string Freeze,
        string Producer,
        string Events,
        string Report,
        string Artifact,
        string Object,
        string State,
        long Transitions);

    /// <summary>
    /// The run is queued past its collection deadline, so the attempt closes it incomplete: a finished run a reprocess
    /// may still re-read its own bytes for, and the one the late append answers. The reconcile that closes it freezes
    /// revision 1, and this reads back everything the reprocess must leave alone.
    /// </summary>
    private async Task<FirstRevision> FreezeFirstRevisionAsync(World world, Guid artifactId, Guid sourceReportId)
    {
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set state = 'queued', collection_deadline_at = now() - interval '1 minute', "
            + $"late_window_minutes = 60 where id = '{world.RunId:D}'");

        await ConsumeEnqueuedReconcileAsync(world, producerId: null);

        (await RevisionCountAsync(world)).ShouldBe(1);
        var revisionHash = await RevisionHashAsync(world, revisionNo: 1);
        var freeze = await FreezeColumnsAsync(world, revisionNo: 1);
        var producer = await EventProducerAsync(world);

        return new FirstRevision(
            revisionHash,
            freeze,
            producer,
            await EventDigestAsync(world, producer),
            await ReportHashAsync(world, sourceReportId),
            await ArtifactHashAsync(world, artifactId),
            await StoredObjectDigestAsync(world, artifactId),
            await RunStateAsync(world),
            await TransitionCountAsync(world));
    }

    /// <summary>The old revision, the events it froze, the stored report and its artifact are exactly what they were.</summary>
    private async Task AssertFirstRevisionUntouchedAsync(
        World world,
        Guid artifactId,
        Guid sourceReportId,
        FirstRevision first)
    {
        (await RevisionCountAsync(world)).ShouldBe(2, "a reprocess appends one revision and rewrites none");
        (await RevisionHashAsync(world, revisionNo: 1)).ShouldBe(first.RevisionHash);
        (await FreezeColumnsAsync(world, revisionNo: 1)).ShouldBe(first.Freeze);
        (await EventDigestAsync(world, first.Producer)).ShouldBe(first.Events);
        (await ReportHashAsync(world, sourceReportId)).ShouldBe(first.Report);
        (await ArtifactHashAsync(world, artifactId)).ShouldBe(first.Artifact);
        (await StoredObjectDigestAsync(world, artifactId)).ShouldBe(first.Object);
    }

    /// <summary>The new evidence is its own revision, one number further and past the old event watermark.</summary>
    private async Task AssertSecondRevisionAsync(World world, FirstRevision first)
    {
        (await FreezeColumnsAsync(world, revisionNo: 2)).ShouldNotBeNullOrEmpty();
        (await FreezeColumnsAsync(world, revisionNo: 2)).ShouldNotBe(first.Freeze);
        (await EventWatermarkAsync(world, revisionNo: 2))
            .ShouldBe(2, "the run holds the original report's event and the reprocessed one");
    }

    /// <summary>The parsing row the request named is the reprocessed report's own row, stamped with the pinned release.</summary>
    private async Task AssertReprocessedRowAsync(World world, Guid parsingId) =>
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select id::text || '/' || outcome || '/' || parser_version from public.raw_report "
            + $"where org_id = '{world.Org:D}' and id = '{parsingId:D}'"))
            .ShouldBe($"{parsingId:D}/{RawReportOutcomes.Mapped}/{PinVersion}");

    /// <summary>The late append records a revision and never moves a finished run.</summary>
    private async Task AssertRunUnmovedAsync(World world, FirstRevision first)
    {
        (await RunStateAsync(world)).ShouldBe(first.State);
        (await TransitionCountAsync(world)).ShouldBe(first.Transitions);
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select r.current_evidence_revision_id::text || '/' || er.revision_no::text from public.run r "
            + "join public.evidence_revision er on er.org_id = r.org_id and er.id = r.current_evidence_revision_id "
            + $"where r.id = '{world.RunId:D}'"))
            .ShouldEndWith("/2");
    }

    /// <summary>
    /// The call the report task consumer makes, minus the queue: the run's own stored report is parsed at the release the
    /// registry resolves, so the events and the report row the first revision freezes are production rows.
    /// </summary>
    /// <param name="world">The tenant and run the report belongs to.</param>
    /// <param name="artifactId">The run's <c>ready</c> raw report artifact.</param>
    /// <returns>The outcome the production handler reported.</returns>
    private static async Task<ParseRawReportOutcome> ParseOriginalReportAsync(World world, Guid artifactId) =>
        await CreateParseHandler(world).ExecuteAsync(
            new ParseRawReportPayload(world.RunId, artifactId, ProducerKind),
            world.Org,
            Token);

    private static ParseRawReportHandler CreateParseHandler(World world)
    {
        var services = world.Host.Services;

        return new ParseRawReportHandler(
            services.GetRequiredService<ITenantScopeFactory>(),
            services.GetRequiredService<ITenantDbContextFactory<EvidenceDbContext>>(),
            services.GetRequiredService<ITenantDb>(),
            services.GetRequiredService<IObjectStorage>(),
            services.GetRequiredService<IReportParserResolver>(),
            new EmulatedChildRunner(services.GetRequiredService<IParsing>()),
            services.GetRequiredService<IOptions<IngestionLimits>>(),
            services.GetServices<IJUnitMapper>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IOutboxWriter>(),
            services.GetRequiredService<IAuditTrail>(),
            services.GetRequiredService<TimeProvider>(),
            NullLogger<ParseRawReportHandler>.Instance);
    }

    /// <summary>
    /// Claims one enqueued <c>orchestrator.run.reconcile</c> row and runs the production task handler over it in the
    /// tenant's own transaction, exactly as the task consumer does — the payload is the row's, never a rebuilt one.
    /// </summary>
    /// <param name="world">The tenant of the trigger.</param>
    /// <param name="producerId">The producer the trigger names, or <see langword="null"/> for the only trigger queued.</param>
    private async Task ConsumeEnqueuedReconcileAsync(World world, string? producerId)
    {
        var filter = producerId is null ? string.Empty : $"and (payload ->> 'producer_id') = '{producerId}'";
        var json = await _fixture.ScalarAsync<string>(
            world.Database,
            $"select payload::text from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunReconcileContract.OutboxTaskType}' {filter} "
            + "order by created_at, id limit 1");
        json.ShouldNotBeNull($"the outbox holds no reconcile trigger for {producerId ?? "the run"}");

        var payload = JsonSerializer.Deserialize<RunReconcilePayload>(json, ReconcilePayloadJson);
        payload.ShouldNotBeNull();

        await using var services = world.Host.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, Token);
        var handler = new ReconcileRunTaskHandler(
            services.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ReconcileRunTaskHandler>.Instance);

        await handler.HandleAsync(payload, scope, Token);
        await scope.CommitAsync(Token);
    }

    /// <summary>The report row the original parse committed: the source a reprocess re-reads.</summary>
    /// <param name="world">The tenant of the report.</param>
    /// <param name="artifactId">The artifact the report was parsed from.</param>
    private Task<Guid> SourceReportIdAsync(World world, Guid artifactId) =>
        _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.raw_report where org_id = '{world.Org:D}' "
            + $"and artifact_id = '{artifactId:D}'");

    private Task<long> RevisionCountAsync(World world) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.evidence_revision where org_id = '{world.Org:D}' "
            + $"and run_id = '{world.RunId:D}'");

    /// <summary>One revision row's own digest, so a rewrite of any column shows up as a changed hash.</summary>
    /// <param name="world">The tenant of the run.</param>
    /// <param name="revisionNo">The <c>revision_no</c> the row was frozen under.</param>
    private Task<string> RevisionHashAsync(World world, int revisionNo) =>
        _fixture.ScalarAsync<string>(
            world.Database,
            "select md5(t::text) from public.evidence_revision t "
            + $"where org_id = '{world.Org:D}' and run_id = '{world.RunId:D}' and revision_no = {revisionNo}");

    /// <summary>The frozen evidence one revision names: both hashes, the watermark and the verdict fields.</summary>
    /// <param name="world">The tenant of the run.</param>
    /// <param name="revisionNo">The <c>revision_no</c> the row was frozen under.</param>
    private Task<string> FreezeColumnsAsync(World world, int revisionNo) =>
        _fixture.ScalarAsync<string>(
            world.Database,
            "select concat_ws('|', artifact_set_hash, manifest_hash, event_watermark::text, completeness, "
            + "missing::text, coalesce(outcome, '')) from public.evidence_revision "
            + $"where org_id = '{world.Org:D}' and run_id = '{world.RunId:D}' and revision_no = {revisionNo}");

    private Task<long> EventWatermarkAsync(World world, int revisionNo) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select event_watermark from public.evidence_revision where org_id = '{world.Org:D}' "
            + $"and run_id = '{world.RunId:D}' and revision_no = {revisionNo}");

    /// <summary>The one producer the run's stored events carry before the reprocess writes its synthetic one.</summary>
    /// <param name="world">The tenant of the run.</param>
    private Task<string> EventProducerAsync(World world) =>
        _fixture.ScalarAsync<string>(
            world.Database,
            $"select min(producer_id) from public.event where org_id = '{world.Org:D}' "
            + $"and run_id = '{world.RunId:D}'");

    /// <summary>One producer's stored events, in order, as one digest: an append elsewhere never changes it.</summary>
    /// <param name="world">The tenant of the run.</param>
    /// <param name="producerId">The producer whose event rows the digest covers.</param>
    private Task<string> EventDigestAsync(World world, string producerId) =>
        _fixture.ScalarAsync<string>(
            world.Database,
            $"select coalesce(string_agg(id::text || ':' || sequence::text || ':' || payload::text, E'\\n' order by id), '') "
            + $"from public.event where org_id = '{world.Org:D}' and run_id = '{world.RunId:D}' "
            + $"and producer_id = '{producerId}'");

    private Task<string> ReportHashAsync(World world, Guid reportId) =>
        _fixture.ScalarAsync<string>(
            world.Database,
            $"select md5(t::text) from public.raw_report t where org_id = '{world.Org:D}' and id = '{reportId:D}'");

    private Task<string> ArtifactHashAsync(World world, Guid artifactId) =>
        _fixture.ScalarAsync<string>(
            world.Database,
            $"select md5(t::text) from public.artifact t where org_id = '{world.Org:D}' and id = '{artifactId:D}'");

    /// <summary>The stored report object's own digest: the bytes a reprocess re-reads, never rewritten.</summary>
    /// <param name="world">The tenant of the artifact.</param>
    /// <param name="artifactId">The report artifact whose object is read back.</param>
    private async Task<string> StoredObjectDigestAsync(World world, Guid artifactId)
    {
        var body = await _store.ReadAsync(ObjectKeyFor(world, artifactId));
        body.ShouldNotBeNull("the report object must still be in the store");

        return Convert.ToHexStringLower(body);
    }

    private Task<string> RunStateAsync(World world) =>
        _fixture.ScalarAsync<string>(
            world.Database,
            $"select state from public.run where id = '{world.RunId:D}'");

    private Task<long> TransitionCountAsync(World world) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where org_id = '{world.Org:D}' "
            + $"and task_type = '{RunStateTransitionContract.OutboxTaskType}'");
}
