// The advisory half's tenant boundary: a deferral belongs to the tenant whose task row carries it, so another org's
// deferred pair that names the same artifact key must neither stand in for this tenant's report nor defer it.
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRawReportQuotaTests
{
    [Fact]
    public async Task Another_Tenants_Deferral_Of_The_Same_Artifact_Key_Does_Not_Pre_Check_This_Report()
    {
        var storage = new ReportStorageSpy();
        var gate = new ReportPreCheckGate();
        var world = await StartAsync(services =>
        {
            ReplaceObjectStorage(services, inner => storage.Wrap(inner));
            ReplaceQuotaGate(services, inner => gate.Wrap(inner));
        });
        await using var _ = world;
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, OneEventBody);
        await SeedCounterAsync(counters, world, used: 10m, limit: 10m);
        await SeedForeignDeferralAsync(world, artifactId);

        var outcome = await ParseAsync(world, artifactId);

        // This tenant never deferred the report, so the attempt is the ordinary one: no advisory question, the fetch
        // happens, and the transactional gate is what refuses it.
        outcome.Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        gate.Peeks.ShouldBe(0, "the deferral the probe finds belongs to another tenant");
        gate.Checks.ShouldBe(1);
        storage.Gets.ShouldBe(1);
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        (await CountRawReportsAsync(world, artifactId)).ShouldBe(0);
        var stamped = (await ReadCounterAsync(counters, world)).ShouldNotBeNull().BlockedAt;
        stamped.ShouldNotBeNull("the block came from the transaction, which stamps the counter it refused");

        // The positive control: the same deferral under this tenant is what the probe looks for, so the second attempt is
        // asked about the counter before it is fetched and never reaches the batch.
        await SeedDeferredTaskAsync(world, artifactId);
        var deferred = await ParseAsync(world, artifactId);

        deferred.Status.ShouldBe(ParseRawReportOutcome.QuotaBlocked);
        deferred.RetryAfter.ShouldNotBeNull("the advisory block carries the gate's wait");
        gate.Peeks.ShouldBe(1);
        gate.Checks.ShouldBe(1, "a report stopped before the fetch never reaches the transactional gate");
        storage.Gets.ShouldBe(1, "neither attempt read the object: the first was refused, the second stopped at the door");
        (await ReadCounterAsync(counters, world)).ShouldNotBeNull().BlockedAt.ShouldBe(
            stamped,
            "an advisory answer writes nothing, so the stamp the first block earned is still the one on the counter");
    }

    /// <summary>
    /// The production relation another tenant leaves behind for the same artifact key: one outbox row and the deferred
    /// task the dispatcher makes from it, both owned by an org with no relation to the report.
    /// </summary>
    /// <param name="world">The world the report belongs to.</param>
    /// <param name="artifactId">The report artifact whose key the foreign deferral names.</param>
    private async Task SeedForeignDeferralAsync(World world, Guid artifactId)
    {
        var org = Guid.CreateVersion7();
        var outboxId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        // The key this tenant's own outbox row would carry: the probe must still tell the two tenants apart.
        var key = $"evidence:artifact:{artifactId:D}:{EvidenceTaskTypes.ReportParse}";
        var payload = $$"""
            {"run_id":"{{world.RunId:D}}","artifact_id":"{{artifactId:D}}","producer_kind":"{{ProducerKind}}"}
            """;
        // The deferral's text carries an apostrophe, so it is escaped for the literal.
        var persistedError = TaskDeferredException.PersistedError.Replace("'", "''", StringComparison.Ordinal);

        await _fixture.ExecuteAsync(world.Database, $$"""
            insert into public.organization (id, name, slug)
            values ('{{org:D}}', '{{Unique.Slug("foreign-quota")}}', '{{Unique.Slug("foreign-quota")}}');

            insert into public.outbox (id, org_id, aggregate_type, aggregate_id, task_type, payload, idempotency_key)
            values ('{{outboxId:D}}', '{{org:D}}', 'artifact', '{{artifactId:D}}', '{{EvidenceTaskTypes.ReportParse}}',
                '{{payload}}'::jsonb, '{{key}}');

            insert into public.task (id, org_id, queue, task_type, payload, idempotency_key, state, last_error)
            values ('{{taskId:D}}', '{{org:D}}', 'tasks', '{{EvidenceTaskTypes.ReportParse}}',
                '{{payload}}'::jsonb, 'outbox:{{outboxId:D}}', 'retry', '{{persistedError}}')
            """);
    }
}
