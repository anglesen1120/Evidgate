// The forced-incomplete closure of an overdue run: a run whose producers never started (`queued`) or never finalized
// (`running`) gets no reconcile from any finalize, so the collection deadline is the only thing that can decide it.
// These cases drive the whole platform path — the schedule runner, the sweep handler, the outbox dispatcher and the
// task consumer. The scan and the attempt compare `collection_deadline_at` with the database's `now()`, so a case
// writes the past into the column and the fake clock only moves the schedule's ticks.
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed partial class ReconcileForcedIncompleteTests
{
    private readonly PostgresTenancyFixture _fixture;

    public ReconcileForcedIncompleteTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ a run past its deadline is closed incomplete

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    public async Task Sweep_RunPastItsDeadline_ClosesIncompleteWithReasons(string state)
    {
        var clock = AlignedClock();
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database);
        var runId = world.RunId;

        // No finalize will ever arrive for this run: its slot stays open, which is the stall the deadline exists for.
        await SetRunStateAsync(world, runId, state);
        await PassCollectionDeadlineAsync(world, runId);

        await SweepAsync(world, clock);

        (await DeadlineRowsAsync(world, runId)).ShouldBe(1, "one overdue run is one reconcile in the outbox");
        (await StateAsync(world, runId)).ShouldBe(state, "the sweep queues an attempt, it never transitions a run itself");

        await DispatchAndReconcileAsync(world, expectedReconciles: 1);

        (await StateAsync(world, runId)).ShouldBe(
            "incomplete",
            "the run never reached collecting, so its evidence cannot prove a pass");
        (await CompletenessAsync(world, runId)).ShouldBe("incomplete");

        var missing = await MissingAsync(world, runId);
        missing.ShouldNotBeNullOrWhiteSpace("a forced incomplete never carries an empty missing[]");
        missing.Contains("missing_shard", StringComparison.Ordinal).ShouldBeTrue("the seeded slot never closed, so its producer is missing");
        missing.Contains("missing_result", StringComparison.Ordinal).ShouldBeTrue("the seeded plan's executions recorded no attempt");

        (await RevisionCountAsync(world, runId)).ShouldBe(1, "one attempt freezes exactly one revision");
        (await PointerAsync(world, runId)).ShouldNotBeNull("the attempt moves the run's evidence pointer to that revision");
        (await StateChangeAuditCountAsync(world, runId)).ShouldBe(
            1,
            "the queued|running -> incomplete edge is audited in the attempt's own transaction");
        (await TaskStateAsync(world, RunReconcileContract.OutboxTaskType)).ShouldBe(
            "done",
            "the dispatcher moved the trigger to the queue and the consumer completed it");
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    public async Task Sweep_RunBeforeItsDeadline_IsIgnored(string state)
    {
        var clock = AlignedClock();
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database);
        var runId = world.RunId;

        await SetRunStateAsync(world, runId, state);
        await FutureDeadlineAsync(world, runId);

        await SweepAsync(world, clock);

        (await DeadlineRowsAsync(world, runId)).ShouldBe(0, "a run inside its deadline is not overdue");
        (await StateAsync(world, runId)).ShouldBe(state, "nothing decides a run that is still starting");
        (await RevisionCountAsync(world, runId)).ShouldBe(0, "no attempt, no revision");
    }

    /// <summary>
    /// The scan names the three states that carry a declared edge to <c>incomplete</c> and nothing else: a
    /// <c>created</c> run has no shard plan and no such edge, and a terminal run's verdict is its last word.
    /// </summary>
    /// <param name="state">The state a past-due run must be left in.</param>
    [Theory]
    [InlineData("created")]
    [InlineData("complete")]
    [InlineData("canceled")]
    [InlineData("incomplete")]
    public async Task Sweep_StateWithoutAForcedClose_IsIgnored(string state)
    {
        var clock = AlignedClock();
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database);
        var runId = world.RunId;

        await SetRunStateAsync(world, runId, state);
        await PassCollectionDeadlineAsync(world, runId);

        await SweepAsync(world, clock);

        (await DeadlineRowsAsync(world, runId)).ShouldBe(
            0,
            $"the scan names queued, running and collecting only: a {state} run is never force-closed");
        (await StateAsync(world, runId)).ShouldBe(state);
        (await RevisionCountAsync(world, runId)).ShouldBe(0, "no attempt, no revision");
    }

    /// <summary>The scan names only <c>queued</c>, <c>running</c> and <c>collecting</c> — the runs an attempt can decide.</summary>
    [Fact]
    public async Task DeadlineScan_OverdueRuns_NamesQueuedRunningAndCollectingOnly()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(AlignedClock(), database);

        var runs = new Dictionary<string, Guid>(StringComparer.Ordinal) { ["created"] = world.RunId };
        foreach (var state in new[] { "queued", "running", "collecting", "complete", "canceled", "incomplete" })
        {
            runs[state] = (await world.SeedOtherRunAsync(world.Org)).RunId;
        }

        foreach (var (state, runId) in runs)
        {
            await _fixture.ExecuteAsync(
                world.Database,
                $"update public.run set state = '{state}', collection_deadline_at = now() - interval '1 second' "
                + $"where id = '{runId:D}'");
        }

        var scanned = ((await world.ScalarAsync<string?>(
                    "select string_agg(run_id::text, ',' order by run_id) from evidgate.reconcile_overdue_runs(null, null, 100)"))
                ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

        scanned.Count.ShouldBe(3, "one overdue run per decidable state, and nothing else");
        foreach (var state in new[] { "queued", "running", "collecting" })
        {
            scanned.ShouldContain(runs[state].ToString("D"), $"an overdue {state} run is named");
        }

        foreach (var state in new[] { "created", "complete", "canceled", "incomplete" })
        {
            scanned.ShouldNotContain(runs[state].ToString("D"), $"a {state} run is never named");
        }
    }

    // ------------------------------------------------------------------ the degenerate plan

    /// <summary>
    /// The floor of the rule: a run with no producer slot and no planned execution gives the comparison nothing to
    /// name, and a forced close still records one closed-list reason — <c>missing_result</c>, because the plan this run
    /// should have carried never reported anything.
    /// </summary>
    [Fact]
    public async Task Sweep_RunWithoutSlotsOrPlan_ClosesIncompleteWithMissingResult()
    {
        var clock = AlignedClock();
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database, planned: 0, slots: 0);
        var runId = world.RunId;

        await SetRunStateAsync(world, runId, "queued");
        await PassCollectionDeadlineAsync(world, runId);

        await SweepAsync(world, clock);
        (await DeadlineRowsAsync(world, runId)).ShouldBe(1, "a run with no slot is still overdue and still named");
        await DispatchAndReconcileAsync(world, expectedReconciles: 1);

        (await StateAsync(world, runId)).ShouldBe("incomplete", "a run that proved nothing is never complete");
        (await CompletenessAsync(world, runId)).ShouldBe("incomplete");
        (await MissingAsync(world, runId)).ShouldBe(
            "missing_result",
            "an empty comparison still records what is absent: no plan ever reported");
        (await RevisionCountAsync(world, runId)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ the fallback and the scan/claim window

    /// <summary>
    /// A comparison that names nothing — slot closed with a matching declaration, nothing left to report — still closes
    /// <c>incomplete</c> with a reason from the closed list, because a run outside collecting cannot prove a pass.
    /// Without the fallback the revision would be <c>incomplete</c> with an empty <c>missing[]</c>, which this rejects.
    /// </summary>
    [Fact]
    public async Task Sweep_ComparisonNamesNothing_StillClosesWithAReason()
    {
        var clock = AlignedClock();
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database, planned: 0, slots: 1);
        var runId = world.RunId;

        (await CloseSlotAsync(world, runId)).ShouldBe(1, "the run's one slot is closed: the comparison then names nothing");
        await SetRunStateAsync(world, runId, "running");
        await PassCollectionDeadlineAsync(world, runId);

        await SweepAsync(world, clock);
        (await DeadlineRowsAsync(world, runId)).ShouldBe(1);
        await DispatchAndReconcileAsync(world, expectedReconciles: 1);

        (await StateAsync(world, runId)).ShouldBe("incomplete", "a run outside collecting never reads complete");
        (await CompletenessAsync(world, runId)).ShouldBe("incomplete");
        (await MissingAsync(world, runId)).ShouldBe(
            "missing_shard",
            "nothing was missing and the plan still holds a slot: the fallback names the shard never collected");
    }

    /// <summary>
    /// A cancel landing between the scan and the claim survives: the attempt finds a terminal run, closes nothing and
    /// appends no revision, so a forced verdict can never overwrite a caller's decision.
    /// </summary>
    [Fact]
    public async Task PostScanCancel_LeavesTheRunCanceledWithNoRevision()
    {
        var clock = AlignedClock();
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database);
        var runId = world.RunId;

        await SetRunStateAsync(world, runId, "running");
        await PassCollectionDeadlineAsync(world, runId);
        await SweepAsync(world, clock);
        await DispatchAsync(world, expectedReconciles: 1);

        await SetRunStateAsync(world, runId, "canceled");

        await ReconcileAsync(world, expectedReconciles: 1);

        (await StateAsync(world, runId)).ShouldBe("canceled", "the attempt never re-decides a run that left early");
        (await RevisionCountAsync(world, runId)).ShouldBe(0, "nothing was compared, so nothing was frozen");
        (await TaskStateAsync(world, RunReconcileContract.OutboxTaskType)).ShouldBe(
            "done",
            "the attempt is a normal completion: the run it named is terminal");
    }

    /// <summary>
    /// The claim finds <c>collecting</c> and decides as collecting: a satisfied plan reads <c>complete</c>, a settlement a
    /// forced close could never produce.
    /// </summary>
    [Fact]
    public async Task PostScanCollecting_AttemptTakesTheCollectingPath()
    {
        var clock = AlignedClock();
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(clock, database, planned: 0, slots: 1);
        var runId = world.RunId;

        (await CloseSlotAsync(world, runId)).ShouldBe(1, "a closed, matching slot is what makes the plan satisfied");
        await SetRunStateAsync(world, runId, "running");
        await PassCollectionDeadlineAsync(world, runId);
        await SweepAsync(world, clock);
        await DispatchAsync(world, expectedReconciles: 1);

        await SetRunStateAsync(world, runId, "collecting");

        await ReconcileAsync(world, expectedReconciles: 1);

        (await MissingAsync(world, runId)).ShouldBe(
            string.Empty,
            "the collecting comparison named nothing, so the reason list is empty — a forced close always names one");
        (await StateAsync(world, runId)).ShouldBe("complete", "the claim decided as collecting, not as forced");
        (await CompletenessAsync(world, runId)).ShouldBe("complete");
    }

    /// <summary>
    /// The scan compares with <c>&lt;=</c> on the database clock: a deadline equal to the transaction's <c>now()</c> is
    /// already overdue — asserted inside that one transaction.
    /// </summary>
    [Fact]
    public async Task DeadlineScan_DeadlineEqualToNow_IsOverdue()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(AlignedClock(), database, planned: 0, slots: 1);
        var runId = world.RunId;

        await using var connection = await _fixture.OpenSuperuserAsync(world.Database);
        await using var transaction = await connection.BeginTransactionAsync(Token);

        await using (var set = connection.CreateCommand())
        {
            set.Transaction = transaction;
            set.CommandText =
                $"update public.run set state = 'queued', collection_deadline_at = now() where id = '{runId:D}'";
            (await set.ExecuteNonQueryAsync(Token)).ShouldBe(1);
        }

        long named;
        await using (var scan = connection.CreateCommand())
        {
            scan.Transaction = transaction;
            scan.CommandText =
                "select count(*) from evidgate.reconcile_overdue_runs(null, null, 100) "
                + $"where run_id = '{runId:D}'";
            named = (long)(await scan.ExecuteScalarAsync(Token))!;
        }

        named.ShouldBe(1, "the comparison is <=, so a deadline equal to the database's now() is already overdue");

        await transaction.RollbackAsync(Token);
    }

}
