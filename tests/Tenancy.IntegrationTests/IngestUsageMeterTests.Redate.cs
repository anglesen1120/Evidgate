using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Usage.Application;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The metering task against a month that was already billed: a replayed attempt must not bill twice or audit a move
/// twice, a stored row that was itself re-dated is compared through its provenance, and a failure after the write takes
/// the move's audit row with it.
/// </summary>
public sealed partial class IngestUsageMeterTests
{
    [Fact]
    public async Task MeterBatch_ReplayedAfterALateWrite_AddsNoRowAndNoAudit()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(owned.Name);
        var batchId = await PostBatchAsync(world, events: 2);
        var metered = await BatchColumnAsync(
            world.Database, batchId, "to_char(created_at at time zone 'UTC', 'YYYY-MM')");
        await IssueStatementAsync(world.Database, world.Org, metered);
        await DispatchAndRunAsync(world);

        (await LedgerColumnAsync(world.Database, world.Org, batchId, "original_period")).ShouldBe(metered);

        // The month the row was assigned to is billed too before the queue replays the same task: the walk would now
        // land a fresh row somewhere else, and a replay must still bill nothing.
        await IssueStatementAsync(world.Database, world.Org, NextMonth(metered));
        await MakeRunnableAsync(world.Database);
        (await Consumer(world).PollOnceAsync(Queue, Token)).ShouldBe(1);

        (await TaskStateAsync(world.Database)).ShouldBe("done", "an already-billed batch is this task's own replay");
        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(1);
        (await LedgerColumnAsync(world.Database, world.Org, batchId, "period")).ShouldBe(NextMonth(metered));
        (await LedgerColumnAsync(world.Database, world.Org, batchId, "original_period")).ShouldBe(metered);
        (await RedateAuditCountAsync(world)).ShouldBe(1L, "a replay moved nothing, so it audits no second move");
    }

    [Fact]
    public async Task MeterBatch_ReplayedRedatedDuplicateWithAnotherOriginalMonth_CompletesAsAConflict()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(owned.Name);
        var batchId = await PostBatchAsync(world, events: 2);
        var entry = await SeededEntryAsync(world, batchId, quantity: 2m);
        var previous = PreviousMonth(entry.Period);

        // The stored row was metered for the month before, so its provenance is not this batch's month: the payload
        // that disagrees is the period, and a stored bill is never rewritten.
        await IssueStatementAsync(world.Database, world.Org, previous);
        await SeedLedgerAsync(world, entry with { Period = previous });
        (await LedgerColumnAsync(world.Database, world.Org, batchId, "period")).ShouldBe(entry.Period);
        (await LedgerColumnAsync(world.Database, world.Org, batchId, "original_period")).ShouldBe(previous);

        (await Dispatcher(world).DispatchBatchAsync(Token)).Enqueued.ShouldBe(1);
        (await Consumer(world).PollOnceAsync(Queue, Token)).ShouldBe(1);

        (await TaskStateAsync(world.Database))
            .ShouldBe("done", "the ledger compares original_period, finds another month, and answers a conflict");
        (await AttemptsAsync(world.Database)).ShouldBe(1);
        (await PendingTaskRowsAsync(world.Database)).ShouldBe(0, "a conflict is not retried");
        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(1);
        (await LedgerColumnAsync(world.Database, world.Org, batchId, "original_period")).ShouldBe(previous);
    }

    [Theory]
    [InlineData("unit")]
    [InlineData("project_id")]
    public async Task MeterBatch_ReplayedRedatedDuplicateWithOneDifferentField_CompletesAsAConflict(string field)
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(owned.Name);
        var batchId = await PostBatchAsync(world, events: 2);
        var entry = await SeededEntryAsync(world, batchId, quantity: 2m);
        var different = field switch
        {
            "unit" => entry with { Unit = "different" },
            "project_id" => entry with { ProjectId = null },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        // The stored row was re-dated out of the batch's month, so only its provenance matches this batch: the differing
        // unit or project is what the payload hash catches, and no second row is written.
        await IssueStatementAsync(world.Database, world.Org, entry.Period);
        await SeedLedgerAsync(world, different);
        (await LedgerColumnAsync(world.Database, world.Org, batchId, "original_period")).ShouldBe(entry.Period);

        (await Dispatcher(world).DispatchBatchAsync(Token)).Enqueued.ShouldBe(1);
        (await Consumer(world).PollOnceAsync(Queue, Token)).ShouldBe(1);

        (await TaskStateAsync(world.Database)).ShouldBe("done", "the conflict is reported, not retried");
        (await AttemptsAsync(world.Database)).ShouldBe(1);
        (await PendingTaskRowsAsync(world.Database)).ShouldBe(0);
        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(1);
        (await RedateAuditCountAsync(world)).ShouldBe(1L, "only the seed's move is audited");
    }

    [Fact]
    public async Task MeterBatch_LateWriteThenFailure_RollsBackTheMoveAuditAndRetryMetersOnce()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        var failures = new LedgerFailures(failuresLeft: 1);
        await using var world = await StartAsync(
            owned.Name,
            services =>
            {
                services.AddScoped<UsageContracts>();
                services.AddScoped<IUsageLedger>(provider =>
                    new WriteThenThrowLedger(provider.GetRequiredService<UsageContracts>(), failures));
            });
        var batchId = await PostBatchAsync(world, events: 2);
        var metered = await BatchColumnAsync(
            world.Database, batchId, "to_char(created_at at time zone 'UTC', 'YYYY-MM')");
        await IssueStatementAsync(world.Database, world.Org, metered);

        await DispatchAndRunAsync(world);

        (await TaskStateAsync(world.Database)).ShouldBe("retry", "the failure after the write still fails the attempt");
        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(0, "the attempt's transaction rolled the row back");
        (await RedateAuditCountAsync(world)).ShouldBe(0L, "the move's audit row belongs to the rolled-back attempt");

        await MakeRunnableAsync(world.Database);
        (await Consumer(world).PollOnceAsync(Queue, Token)).ShouldBe(1);

        (await TaskStateAsync(world.Database)).ShouldBe("done");
        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(1);
        (await LedgerColumnAsync(world.Database, world.Org, batchId, "original_period")).ShouldBe(metered);
        (await RedateAuditCountAsync(world)).ShouldBe(1L, "the surviving write audits its move exactly once");
    }

    [Fact]
    public async Task MeterBatch_DecemberBatchAfterDecemberAndJanuaryWereIssued_LandsInFebruary()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(owned.Name);
        var batchId = await PostBatchAsync(world, events: 2);

        // The batch's own commit instant is what names its month, so moving it to December is what makes the year end
        // the case under test.
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.event_batch set created_at = '2026-12-05T00:00:00Z' where id = '{batchId:D}'");
        await IssueStatementAsync(world.Database, world.Org, "2026-12");
        await IssueStatementAsync(world.Database, world.Org, "2027-01");

        await DispatchAndRunAsync(world);

        (await TaskStateAsync(world.Database)).ShouldBe("done");
        (await LedgerColumnAsync(world.Database, world.Org, batchId, "period"))
            .ShouldBe("2027-02", "December rolls into January, and an issued January is skipped");
        (await LedgerColumnAsync(world.Database, world.Org, batchId, "original_period")).ShouldBe("2026-12");
        (await RedateAuditCountAsync(world)).ShouldBe(1L);
    }

    [Fact]
    public async Task MeterBatch_UpstreamRunNamesForeignProject_RefusesBeforeTheRedateAndRecoversWhenCorrected()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        var run = new MeterRunProject();
        await using var world = await StartAsync(
            owned.Name,
            services => services.AddSingleton<IRunEventScope>(run));
        run.ProjectId = world.Project;
        var batchId = await PostBatchAsync(world, events: 2);
        var (otherOrg, otherProject) = await world.CreateOtherOrgAsync(Unique.Slug("foreign-meter"));
        var metered = await BatchColumnAsync(
            world.Database, batchId, "to_char(created_at at time zone 'UTC', 'YYYY-MM')");
        await IssueStatementAsync(world.Database, world.Org, metered);

        // The run names a project of another tenant, so the ledger refuses the entry before the guard can move it.
        run.ProjectId = otherProject;
        await DispatchAndRunAsync(world);

        (await TaskStateAsync(world.Database)).ShouldBe("retry");
        (await LastErrorAsync(world)).ShouldContain("/entries/0/project_id");
        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(0);
        (await LedgerCountAsync(world.Database, otherOrg)).ShouldBe(0);
        (await RedateAuditCountAsync(world)).ShouldBe(0L);

        run.ProjectId = world.Project;
        await MakeRunnableAsync(world.Database);
        (await Consumer(world).PollOnceAsync(Queue, Token)).ShouldBe(1);

        (await TaskStateAsync(world.Database)).ShouldBe("done");
        (await LedgerCountAsync(world.Database, world.Org)).ShouldBe(1);
        (await LedgerColumnAsync(world.Database, world.Org, batchId, "project_id::text"))
            .ShouldBe(world.Project.ToString("D"));
        (await RedateAuditCountAsync(world)).ShouldBe(1L);
    }

    private Task<long> RedateAuditCountAsync(IngestTestWorld world) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.audit_log where org_id = '{world.Org:D}' and action = 'usage.entry_redated'");

    /// <summary>The month before <paramref name="period"/>: the one a row is metered for when it arrives late.</summary>
    private static string PreviousMonth(string period) =>
        DateOnly.ParseExact(period + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            .AddMonths(-1)
            .ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The run scope the metering handler reads, with the project the case chooses.</summary>
    private sealed class MeterRunProject : IRunEventScope
    {
        internal Guid ProjectId { get; set; }

        public ValueTask<RunEventScope?> ReadAsync(
            Guid runId,
            IReadOnlyCollection<string> executionKeys,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult<RunEventScope?>(
                new RunEventScope(ProjectId, "active", "collecting", executionKeys.ToHashSet(StringComparer.Ordinal), true));
        }
    }
}
