// The attempt half of the reprocess path: a reprocess adds events and a revision but writes no case_attempt row. An
// attempt is stamped with its transaction's start, and that stamp can fall inside an older revision's frozen window
// (FrozenEvidenceSql.LoadLatestAttemptStatuses), which would change what that revision recomputes. What a re-read row
// contributes to a verdict is settled when a revision is frozen, never by a second writer of the attempt table.
using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class ReprocessReportTaskTests
{
    /// <summary>A planned execution the original report never names, so nothing has attempted it yet.</summary>
    private static readonly string UnattemptedExecutionKey = new('c', 64);

    /// <summary>A report naming only that case: the reprocessed evidence of the pin.</summary>
    private static readonly byte[] SecondReportBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites>
          <testsuite name="pytest" tests="1">
            <testcase name="second" time="0.012"/>
          </testsuite>
        </testsuites>
        """u8.ToArray();

    /// <summary>
    /// The run's first revision is frozen from the original report, then a reprocess of a second stored report writes its
    /// events and its own parsing row, and the reconciler appends one more revision. The no-attempt proof is the attempt
    /// count, its digest and the per-producer read: the reprocessed evidence names an execution nothing has attempted, so
    /// a reinstated attempt writer shows up there. The revision-1 row, hash and frozen view guard the freeze contract;
    /// this run is sequential, so an attempt written by the reprocess would be stamped after the freeze and those three
    /// cannot detect one on their own.
    /// </summary>
    [Fact]
    public async Task Reprocess_Appends_A_Revision_And_Writes_No_Case_Attempt()
    {
        var world = await StartAsync();
        await using var _ = world;

        await SeedPlanAsync(world);
        await SeedUnattemptedCaseAsync(world);

        var originalArtifactId = await SeedArtifactAsync(world, ReportBody);
        var original = await ParseOriginalReportAsync(world, originalArtifactId);
        original.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        original.Events.ShouldBe(1);

        var first = await FreezeFirstRevisionAsync(world);

        var (producerId, outcome) = await ReprocessSecondReportAsync(world);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        outcome.Events.ShouldBe(1);

        await ConsumeEnqueuedReconcileAsync(world, producerId);

        // The reprocessed evidence names an execution nothing has attempted, so an attempt writer would be visible here.
        (await _fixture.ScalarAsync<string>(
            world.Database,
            $"select execution_key from public.event where org_id = '{world.Org:D}' "
            + $"and run_id = '{world.RunId:D}' and producer_id = '{producerId}'"))
            .ShouldBe(UnattemptedExecutionKey);

        // The reprocess still lands as its own revision, past the original event watermark.
        (await RevisionCountAsync(world)).ShouldBe(2, "a reprocess appends one revision");
        (await EventWatermarkAsync(world, revisionNo: 2)).ShouldBe(2);

        await AssertFirstRevisionIntactAsync(world, first);
        await AssertNoAttemptWrittenAsync(world, first, producerId);
    }

    /// <summary>The revision 1 row, its frozen view and the stored attempts a reprocess must leave exactly as they are.</summary>
    private sealed record OriginalRevision(
        string RevisionHash,
        string Freeze,
        string View,
        long AttemptCount,
        string AttemptDigest);

    /// <summary>
    /// The clock closes the run, so the reconcile over the original evidence freezes revision 1: the revision whose row,
    /// hash and frozen view the reprocess must leave untouched.
    /// </summary>
    private async Task<OriginalRevision> FreezeFirstRevisionAsync(World world)
    {
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.run set state = 'queued', collection_deadline_at = now() - interval '1 minute', "
            + $"late_window_minutes = 60 where id = '{world.RunId:D}'");
        await ConsumeEnqueuedReconcileAsync(world, producerId: null);

        (await RevisionCountAsync(world)).ShouldBe(1);
        var view = await FrozenViewAsync(world, revisionNo: 1);
        view.ShouldContain(ExecutionKey, Case.Sensitive, "revision 1's frozen view reads the original parse's attempt");

        var attemptCount = await CountAsync(world, "case_attempt");
        attemptCount.ShouldBe(1, "the original parse is the run's only attempt writer so far");

        return new OriginalRevision(
            await RevisionHashAsync(world, revisionNo: 1),
            await FreezeColumnsAsync(world, revisionNo: 1),
            view,
            attemptCount,
            await AttemptDigestAsync(world));
    }

    /// <summary>
    /// Stores the second report under the run, pins the newer release on it and runs the production reprocess handler;
    /// the trigger it wrote is left for the caller to consume.
    /// </summary>
    private async Task<(string ProducerId, ParseRawReportOutcome Outcome)> ReprocessSecondReportAsync(World world)
    {
        var reprocessedArtifactId = await SeedArtifactAsync(world, SecondReportBody);
        var sourceReportId = await SeedSourceReportAsync(world, reprocessedArtifactId);
        var parsingId = await SeedRequestAsync(world, reprocessedArtifactId, sourceReportId, PinVersion);
        var producerId = "reprocess-" + parsingId.ToString("D", CultureInfo.InvariantCulture);

        return (producerId, await ReprocessAsync(world, parsingId));
    }

    /// <summary>The old revision's row, hash and frozen view are exactly what they were.</summary>
    private async Task AssertFirstRevisionIntactAsync(World world, OriginalRevision first)
    {
        (await RevisionHashAsync(world, revisionNo: 1)).ShouldBe(first.RevisionHash);
        (await FreezeColumnsAsync(world, revisionNo: 1)).ShouldBe(first.Freeze);
        (await FrozenViewAsync(world, revisionNo: 1)).ShouldBe(first.View);
    }

    /// <summary>The no-attempt proof: the reprocessed evidence names a planned execution nothing had attempted yet.</summary>
    private async Task AssertNoAttemptWrittenAsync(World world, OriginalRevision first, string producerId)
    {
        (await CountAsync(world, "case_attempt"))
            .ShouldBe(first.AttemptCount, "a reprocess writes events and a revision, never an attempt");
        (await AttemptDigestAsync(world)).ShouldBe(first.AttemptDigest);
        (await AttemptsForProducerAsync(world, producerId))
            .ShouldBe(0, "no attempt belongs to the evidence the reprocess produced");
    }

    /// <summary>One further planned execution, for a case only the reprocessed report names.</summary>
    /// <param name="world">The tenant and run the plan belongs to.</param>
    private async Task SeedUnattemptedCaseAsync(World world)
    {
        var caseId = Guid.CreateVersion7();
        var revisionId = Guid.CreateVersion7();
        var hash = "sha256:" + new string('d', 64);

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit) "
            + $"values ('{caseId:D}', '{world.Org:D}', '{world.Project:D}', '{Guid.CreateVersion7():D}', 'second', "
            + "'playwright', 'abcdef0')");
        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.test_revision (id, org_id, case_id, revision_hash, commit_sha, file_path, framework, "
            + "content_sha256, assertion_hash) values "
            + $"('{revisionId:D}', '{world.Org:D}', '{caseId:D}', '{hash}', 'abcdef0', 'c', 'playwright', '{hash}', '{hash}')");
        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.planned_execution "
            + "(id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id) values "
            + $"('{Guid.CreateVersion7():D}', '{world.Org:D}', '{world.RunId:D}', '{UnattemptedExecutionKey}', "
            + $"'{revisionId:D}', 'sha256:{new string('e', 64)}', 'staging', 'shard-1')");
    }

    /// <summary>
    /// The frozen evidence of one revision, read through the production reader the policy module judges revisions with:
    /// its row plus the latest attempt at or before its freeze stamp, per execution key.
    /// </summary>
    /// <param name="world">The tenant of the run.</param>
    /// <param name="revisionNo">The <c>revision_no</c> whose frozen view is read.</param>
    /// <returns>The view as one deterministic document.</returns>
    private static async Task<string> FrozenViewAsync(World world, int revisionNo)
    {
        await using var services = world.Host.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, readOnlyTransaction: true, cancellationToken: Token);

        var frozen = services.ServiceProvider.GetRequiredService<IFrozenEvidence>();
        var revision = await frozen.GetRevisionAsync(world.Org, world.RunId, revisionNo, Token);
        var statuses = await frozen.GetLatestAttemptStatusesAsync(world.Org, world.RunId, revisionNo, Token);

        return JsonSerializer.Serialize(new { revision, statuses });
    }

    /// <summary>The run's stored attempts as one digest: a rewrite or an append changes it.</summary>
    /// <param name="world">The tenant of the run.</param>
    private Task<string> AttemptDigestAsync(World world) =>
        _fixture.ScalarAsync<string>(
            world.Database,
            "select coalesce(string_agg(md5(t::text), '' order by t.id), '') from public.case_attempt t "
            + $"where t.org_id = '{world.Org:D}' and t.run_id = '{world.RunId:D}'");

    /// <summary>How many stored attempts belong to an execution key the producer's events name.</summary>
    /// <param name="world">The tenant of the run.</param>
    /// <param name="producerId">The producer whose events name the keys.</param>
    private Task<int> AttemptsForProducerAsync(World world, string producerId) =>
        _fixture.ScalarAsync<int>(
            world.Database,
            "select count(*)::int from public.case_attempt ca "
            + $"where ca.org_id = '{world.Org:D}' and ca.run_id = '{world.RunId:D}' "
            + "and ca.execution_key in (select e.execution_key from public.event e "
            + $"where e.org_id = '{world.Org:D}' and e.run_id = '{world.RunId:D}' "
            + $"and e.producer_id = '{producerId}')");
}
