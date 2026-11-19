using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

// Aggregate-scale sibling of the reconcile suite: it seeds a thousand events per run in one statement so a sequence
// gap at 317 and the grouped aggregate's plan are exercised at volume, not on a handful of rows. The class-level
// [Trait("Category","Docker")] of the partial class applies to every case here, like its siblings.
public sealed partial class ReconcileRunTests
{
    /// <summary>
    /// Two producers, five hundred events each, one hole at sequence 317 in a shard that declares 500: the grouped
    /// aggregate reads the gap, the persisted revision names it, and identical evidence in a second run freezes the
    /// same persisted fields while restoring the hole changes them.
    /// </summary>
    [Fact]
    public async Task Reconcile_1kEvents_TwoProducers_GapAtArbitrarySequence_ReportsGapAndCounts()
    {
        await using var first = await StartAsync(slots: 2);
        await using var repeated = await StartAsync(slots: 2);
        await using var complete = await StartAsync(slots: 2);

        var gap = await FreezeAggregateAsync(first, missingSequence: 317);
        var same = await FreezeAggregateAsync(repeated, missingSequence: 317);
        var whole = await FreezeAggregateAsync(complete, missingSequence: 0);

        gap.Comparison.EventsMatch.ShouldBeFalse();
        gap.Comparison.ShardsPlanned.ShouldBe(2);
        gap.Comparison.ShardsClosed.ShouldBe(2);
        gap.Comparison.EventWatermark.ShouldBe(999);
        gap.Comparison.Missing.ShouldBe(
            [new ReconcileMissing(
                ReconcileMissingCodes.CountMismatch,
                Producer,
                "declared_count=500;observed=499;declared_last_sequence=500;beyond=0;min_sequence=1;sequence_span=499")]);
        gap.Revision.Completeness.ShouldBe("incomplete");
        gap.Revision.Missing.ShouldBe([ReconcileMissingCodes.CountMismatch]);
        gap.Revision.EventWatermark.ShouldBe(999);
        (await AggregateProducerCountAsync(first, Producer)).ShouldBe(499);
        (await AggregateProducerCountAsync(first, SecondProducer)).ShouldBe(500);
        (await first.CountAsync("event", $"run_id = '{first.RunId:D}' and producer_id = '{Producer}' and sequence = 317"))
            .ShouldBe(0);

        same.Comparison.Missing.ShouldBe(gap.Comparison.Missing);
        same.Comparison.ShardsPlanned.ShouldBe(2);
        same.Comparison.ShardsClosed.ShouldBe(2);
        same.Comparison.EventWatermark.ShouldBe(999);
        (await AggregateProducerCountAsync(repeated, Producer)).ShouldBe(499);
        (await AggregateProducerCountAsync(repeated, SecondProducer)).ShouldBe(500);
        same.Evidence.ShouldBe(gap.Evidence, "identical evidence freezes the same persisted fields, excluding run identity");

        whole.Comparison.EventsMatch.ShouldBeTrue();
        whole.Comparison.Missing.ShouldBeEmpty();
        whole.Comparison.ShardsPlanned.ShouldBe(2);
        whole.Comparison.ShardsClosed.ShouldBe(2);
        whole.Comparison.EventWatermark.ShouldBe(1000);
        whole.Revision.Completeness.ShouldBe("complete");
        whole.Revision.Missing.ShouldBeEmpty();
        whole.Revision.EventWatermark.ShouldBe(1000);
        (await AggregateProducerCountAsync(complete, Producer)).ShouldBe(500);
        (await AggregateProducerCountAsync(complete, SecondProducer)).ShouldBe(500);
        whole.Evidence.ShouldNotBe(gap.Evidence, "restoring sequence 317 changes the persisted completeness and watermark");
    }

    /// <summary>
    /// A finished run reconciled a second time is NotCollectable, so nothing is compared or finished. The retry half of
    /// that guard is already covered by ReconcileRunTask_CommittedButNotAcknowledged_RedeliveryWritesNothing (queue claim,
    /// lease, acknowledgement); this case adds the direct second attempt's run version, audit and row counts.
    /// </summary>
    [Fact]
    public async Task Reconcile_Rerun_NoOp()
    {
        await using var world = await StartAsync(slots: 2);
        var first = await FreezeAggregateAsync(world, missingSequence: 0);
        var runId = world.RunId;
        first.Revision.Completeness.ShouldBe("complete");
        (await RunRevisionCountAsync(world, runId)).ShouldBe(1);
        (await FrozenEventRowsOfRunAsync(world, runId)).ShouldBe(1);
        (await StateAsync(world, runId)).ShouldBe("complete");
        (await StateChangeAuditCountAsync(world, runId)).ShouldBe(1);
        var version = await VersionAsync(world, runId);
        var updatedAt = await UpdatedAtAsync(world, runId);
        var outboxRows = await AggregateRunOutboxCountAsync(world);
        outboxRows.ShouldBeGreaterThanOrEqualTo(1, "the frozen revision announced itself");

        var (replayed, finalization) = await ReconcileAndFinishAsync(world, runId);

        replayed.Disposition.ShouldBe(ReconcileDisposition.NotCollectable);
        replayed.Comparison.ShouldBeNull();
        finalization.ShouldBeNull();
        var after = await RevisionSummaryAsync(world, runId);
        after.Id.ShouldBe(first.Revision.Id);
        after.RevisionNo.ShouldBe(1);
        after.ManifestHash.ShouldBe(first.Revision.ManifestHash);
        after.ArtifactSetHash.ShouldBe(first.Revision.ArtifactSetHash);
        (await AggregateRevisionFieldsAsync(world)).ShouldBe(first.Evidence);
        (await RunRevisionCountAsync(world, runId)).ShouldBe(1);
        (await PointerAsync(world, runId)).ShouldBe(first.Revision.Id);
        (await StateAsync(world, runId)).ShouldBe("complete");
        (await VersionAsync(world, runId)).ShouldBe(version);
        (await UpdatedAtAsync(world, runId)).ShouldBe(updatedAt);
        (await StateChangeAuditCountAsync(world, runId)).ShouldBe(1);
        (await FrozenEventRowsOfRunAsync(world, runId)).ShouldBe(1);
        (await AggregateRunOutboxCountAsync(world)).ShouldBe(outboxRows);
        (await AggregateProducerCountAsync(world, Producer)).ShouldBe(500);
        (await AggregateProducerCountAsync(world, SecondProducer)).ShouldBe(500);
    }

    /// <summary>
    /// The grouped aggregate must read one run through the (org_id, run_id, producer_id, sequence) unique index; a
    /// sequential scan of event would read every tenant's evidence. ANALYZE is run on a dedicated database so the
    /// statistics reflect this case's rows and no sibling's plan is influenced.
    /// </summary>
    [Fact]
    public async Task ObservedEventsAggregate_UsesRunProducerSequenceIndex_NoSeqScanOnEvent()
    {
        // ANALYZE changes global planner statistics, so this case owns its database rather than influencing siblings.
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(slots: 2, database: owned.Name);
        await PrepareAggregateAsync(world, missingSequence: 0);
        (await AggregateProducerCountAsync(world, Producer)).ShouldBe(500);
        (await AggregateProducerCountAsync(world, SecondProducer)).ShouldBe(500);

        // The 1k target is 1.2 % of 81k events across forty other runs of the same org, so the planner's choice between
        // the (org_id, run_id, ...) index and a sequential scan is made on selectivity, not on a tiny table.
        for (var index = 0; index < 40; index++)
        {
            var otherRun = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);
            await IngestTestWorld.SeedSlotsAsync(_fixture, world.Database, world.Org, otherRun, 1, boundJti: null);
            await StoreBulkEventsAsync(world, otherRun, count: 2000);
        }

        (await world.CountAsync("event", $"org_id = '{world.Org:D}'")).ShouldBe(81000);
        await _fixture.ExecuteAsync(world.Database, "analyze public.event");
        await _fixture.ExecuteAsync(world.Database, "analyze public.producer_slot");
        var plan = await AggregateExplainJsonAsync(world);
        var nodes = AggregatePlanNodes(plan.GetProperty("Plan")).ToArray();
        nodes.Length.ShouldBeGreaterThan(1, plan.GetRawText());
        nodes.Any(node =>
                node.TryGetProperty("Index Name", out var name)
                && name.GetString() == "event_run_producer_sequence_key"
                && node.GetProperty("Node Type").GetString() is "Index Scan" or "Index Only Scan" or "Bitmap Index Scan")
            .ShouldBeTrue(plan.GetRawText());
        nodes.Any(node =>
                node.GetProperty("Node Type").GetString() is "Seq Scan" or "Parallel Seq Scan"
                && node.TryGetProperty("Relation Name", out var relation)
                && relation.GetString() == "event")
            .ShouldBeFalse(plan.GetRawText());
    }

    private async Task<(ReconcileComparison Comparison, RevisionSummary Revision, string Evidence)> FreezeAggregateAsync(
        IngestTestWorld world,
        int missingSequence)
    {
        await PrepareAggregateAsync(world, missingSequence);
        var (outcome, finalization) = await ReconcileAndFinishAsync(world, world.RunId);
        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        var revision = await RevisionSummaryAsync(world, world.RunId);
        finalization.ShouldNotBeNull().RevisionId.ShouldBe(revision.Id);
        revision.RevisionNo.ShouldBe(1);
        revision.ManifestHash.ShouldBe(ManifestHashOf(world.RunId));
        (await RunRevisionCountAsync(world, world.RunId)).ShouldBe(1);
        (await PointerAsync(world, world.RunId)).ShouldBe(revision.Id);
        (await StateAsync(world, world.RunId)).ShouldBe(revision.Completeness);

        return (outcome.Comparison.ShouldNotBeNull(), revision, await AggregateRevisionFieldsAsync(world));
    }

    private async Task PrepareAggregateAsync(IngestTestWorld world, int missingSequence)
    {
        await SetRunStateAsync(world, world.RunId, "collecting");
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await scope.ExecuteAsync(
            """
            insert into public.event
              (id, org_id, run_id, event_id, producer_id, sequence, execution_key, attempt_no, type,
               normalized_status, payload_hash)
            select gen_random_uuid(), @org_id, @run_id, md5(p.producer_id || ':' || s::text)::uuid,
                   p.producer_id, s, md5(p.producer_id || ':' || s::text) || md5((s + 1)::text),
                   1, 'test.finished', 'passed',
                   'sha256:' || md5(p.producer_id || ':' || s::text) || md5((s + 1)::text)
              from public.producer_slot p
              cross join generate_series(1, 500) as s
             where p.org_id = @org_id and p.run_id = @run_id
               and not (p.producer_id = @producer_id and s = @missing_sequence)
            """,
            ("org_id", world.Org),
            ("run_id", world.RunId),
            ("producer_id", Producer),
            ("missing_sequence", missingSequence))).ShouldBe(missingSequence == 0 ? 1000 : 999);
        await scope.CommitAsync();
        await CloseSlotAsync(world, world.RunId, Producer, declaredLastSequence: 500, declaredCount: 500);
        await CloseSlotAsync(world, world.RunId, SecondProducer, declaredLastSequence: 500, declaredCount: 500);
    }

    private static Task<long> AggregateProducerCountAsync(IngestTestWorld world, string producer) =>
        world.CountAsync("event", $"org_id = '{world.Org:D}' and run_id = '{world.RunId:D}' and producer_id = '{producer}'");

    private static Task<long> AggregateRunOutboxCountAsync(IngestTestWorld world) =>
        world.ScalarAsync<long>(
            $"select count(*) from public.outbox where org_id = '{world.Org:D}' "
            + $"and (aggregate_id = '{world.RunId:D}' or payload ->> 'run_id' = '{world.RunId:D}')");

    // The manifest hashes the run identity, not its events; compare only the evidence fields persisted by the finisher.
    private static Task<string> AggregateRevisionFieldsAsync(IngestTestWorld world) =>
        world.ScalarAsync<string>(
            "select jsonb_build_object('revision_no', revision_no, 'completeness', completeness, 'missing', missing, "
            + "'outcome', outcome, 'artifact_set_hash', artifact_set_hash, 'event_watermark', event_watermark)::text "
            + $"from public.evidence_revision where org_id = '{world.Org:D}' and run_id = '{world.RunId:D}' and revision_no = 1");

    private async Task<JsonElement> AggregateExplainJsonAsync(IngestTestWorld world)
    {
        await using var connection = await _fixture.OpenSuperuserAsync(world.Database);
        await using var command = connection.CreateCommand();
        command.CommandText = "explain (format json) " + ReconcileSql.ObservedEvents
            .Replace("@org_id", $"'{world.Org:D}'::uuid", StringComparison.Ordinal)
            .Replace("@run_id", $"'{world.RunId:D}'::uuid", StringComparison.Ordinal);
        var json = (await command.ExecuteScalarAsync(Token)).ShouldBeOfType<string>();
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetArrayLength().ShouldBe(1);
        return document.RootElement[0].Clone();
    }

    private static IEnumerable<JsonElement> AggregatePlanNodes(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                foreach (var descendant in AggregatePlanNodes(child))
                {
                    yield return descendant;
                }
            }
        }
    }
}
