// Plan coverage of one reconcile attempt against a real PostgreSQL: the comparison is not only the
// declared-versus-observed event evidence, because a producer can declare fewer events than the plan expected and agree
// with itself while a planned case never ran. The coverage read answers that half from the plan rows and the recorded
// attempts alone: a planned_execution whose execution_key has no case_attempt is named once as missing_result, so the run
// is incomplete however well the counts match.
//
// The predicate is row existence, never finished_at and never a status vocabulary copied here: every recorded
// case_attempt row carries one of 0032's terminal normalized_status values, a started-but-unrecorded attempt writes no
// row at all (so it counts missing), and a timestamp-free blocked row still exists (so it counts present). The retry case
// pins the same choice from the other side: a case whose last attempt is terminal is present.
//
// Conventions follow ReconcileRunTests.cs (same world, same seeding roles, same one-transaction attempt): PlannedExecutionSeeder
// gives execution i the shard id "shard-{i+1}", so a three-execution plan spans shard-1..shard-3 while the world seeds the
// one producer slot this file finalizes.
using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class ReconcileExecutionCoverageTests
{
    /// <summary>The producer id of the one seeded producer slot, which the run registers as <c>shard-N</c> per shard.</summary>
    private const string Producer = IngestTestWorld.ProducerId;

    /// <summary>How many planned executions these cases seed: three, so "some ran" and "all ran" differ by one.</summary>
    private const int Planned = 3;

    private readonly PostgresTenancyFixture _fixture;

    public ReconcileExecutionCoverageTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reconcile_PlannedExecutionWithoutAttempt_IncompleteWithMissingResult()
    {
        // Three planned, two run: the producer declares the two events it stored and finalizes, so the counts agree
        // exactly; the third planned case never recorded an attempt, which only the plan rows can say.
        await using var world = await StartAsync();
        var runId = world.RunId;
        var executions = world.Plan.Executions;
        await SetRunStateAsync(world);
        await RecordAttemptsAsync(world, (executions[0].ExecutionKey, 1, "passed"), (executions[1].ExecutionKey, 1, "passed"));
        await StoreEventsAsync(world, runId, new SeededEvent(executions[0].ExecutionKey, 1), new SeededEvent(executions[1].ExecutionKey, 2));
        await CloseSlotAsync(world, runId, declaredLastSequence: 2, declaredCount: 2);

        var (outcome, finalization) = await ReconcileAndFinishAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        var comparison = outcome.Comparison.ShouldNotBeNull();
        comparison.EventsMatch.ShouldBeFalse("counts that agree cannot prove a case that never attempted");

        var reason = comparison.Missing.ShouldHaveSingleItem();
        reason.Code.ShouldBe(ReconcileMissingCodes.MissingResult);
        reason.ProducerId.ShouldBe(await ShardOfAsync(world, executions[2].ExecutionKey), "the reason names the plan row's shard");
        reason.Detail.ShouldBe($"execution_key={executions[2].ExecutionKey}");

        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Incomplete);
        var revision = await RevisionAsync(world, runId);
        revision.Completeness.ShouldBe(ReconcileFinisher.Incomplete);
        revision.Missing.ShouldBe([ReconcileMissingCodes.MissingResult]);
        (await StateAsync(world, runId)).ShouldBe("incomplete", "missing evidence never completes the run");
    }

    [Fact]
    public async Task Reconcile_AllPlannedExecutionsAttempted_Complete()
    {
        await using var world = await StartAsync();
        var runId = world.RunId;
        var executions = world.Plan.Executions;
        await SetRunStateAsync(world);
        await RecordAttemptsAsync(
            world,
            (executions[0].ExecutionKey, 1, "passed"),
            (executions[1].ExecutionKey, 1, "passed"),
            (executions[2].ExecutionKey, 1, "passed"));
        await StoreEventsAsync(
            world,
            runId,
            new SeededEvent(executions[0].ExecutionKey, 1),
            new SeededEvent(executions[1].ExecutionKey, 2),
            new SeededEvent(executions[2].ExecutionKey, 3));
        await CloseSlotAsync(world, runId, declaredLastSequence: 3, declaredCount: 3);

        var (outcome, finalization) = await ReconcileAndFinishAsync(world, runId);

        outcome.Disposition.ShouldBe(ReconcileDisposition.Compared, Describe(outcome));
        outcome.Comparison.ShouldNotBeNull().Missing.ShouldBeEmpty(Describe(outcome));
        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Complete);

        var revision = await RevisionAsync(world, runId);
        revision.Completeness.ShouldBe(ReconcileFinisher.Complete);
        revision.Missing.ShouldBeEmpty("every planned execution recorded an attempt");
        (await StateAsync(world, runId)).ShouldBe("complete");
    }

    [Fact]
    public async Task Reconcile_StartedWithoutRecordedAttempt_CountsMissing()
    {
        // A case that started and never reported writes no case_attempt row, so the plan still says it is unattempted:
        // the started event is a status the shard never finished (unknown_status) and, on the plan side, a case no
        // attempt answered (missing_result). The two reasons are separate: neither can stand for the other.
        await using var world = await StartAsync();
        var runId = world.RunId;
        var executions = world.Plan.Executions;
        await SetRunStateAsync(world);
        await RecordAttemptsAsync(world, (executions[0].ExecutionKey, 1, "passed"), (executions[1].ExecutionKey, 1, "passed"));
        await StoreEventsAsync(
            world,
            runId,
            new SeededEvent(executions[2].ExecutionKey, 1, "test.started", null),
            new SeededEvent(executions[0].ExecutionKey, 2),
            new SeededEvent(executions[1].ExecutionKey, 3));
        await CloseSlotAsync(world, runId, declaredLastSequence: 3, declaredCount: 3);

        var (outcome, finalization) = await ReconcileAndFinishAsync(world, runId);

        var comparison = outcome.Comparison.ShouldNotBeNull();
        comparison.Missing.Select(reason => $"{reason.ProducerId}|{reason.Code}").ShouldBe(
            [$"{Producer}|{ReconcileMissingCodes.UnknownStatus}", $"{await ShardOfAsync(world, executions[2].ExecutionKey)}|{ReconcileMissingCodes.MissingResult}"],
            Describe(outcome));
        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Incomplete);

        var revision = await RevisionAsync(world, runId);
        revision.Missing.ShouldBe([ReconcileMissingCodes.UnknownStatus, ReconcileMissingCodes.MissingResult]);
        revision.Missing.Count(code => code == ReconcileMissingCodes.MissingResult)
            .ShouldBe(1, "the started case still holds one planned row");
    }

    [Fact]
    public async Task Reconcile_RetriedExecutionWithTerminalLastAttempt_IsPresent()
    {
        // Attempt 1 failed and attempt 2 passed: the execution has a terminal attempt, so the plan is answered. The
        // retry does not count twice and does not leave the earlier failure behind as a missing case.
        await using var world = await StartAsync();
        var runId = world.RunId;
        var executions = world.Plan.Executions;
        await SetRunStateAsync(world);
        await RecordAttemptsAsync(
            world,
            (executions[0].ExecutionKey, 1, "passed"),
            (executions[1].ExecutionKey, 1, "passed"),
            (executions[2].ExecutionKey, 1, "failed"),
            (executions[2].ExecutionKey, 2, "passed"));
        await StoreEventsAsync(
            world,
            runId,
            new SeededEvent(executions[0].ExecutionKey, 1),
            new SeededEvent(executions[1].ExecutionKey, 2),
            new SeededEvent(executions[2].ExecutionKey, 3, AttemptNo: 2));
        await CloseSlotAsync(world, runId, declaredLastSequence: 3, declaredCount: 3);

        var (outcome, finalization) = await ReconcileAndFinishAsync(world, runId);

        outcome.Comparison.ShouldNotBeNull().Missing.ShouldBeEmpty(Describe(outcome));
        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Complete);
        (await RevisionAsync(world, runId)).Missing.ShouldBeEmpty("a retried execution whose last attempt is terminal is present");
    }

    [Fact]
    public async Task Reconcile_BlockedAttemptWithoutTimestamps_IsPresent()
    {
        // A blocked case (a failed setup) reports a status and never runs, so its row carries no started_at and
        // no finished_at. The coverage predicate reads row existence only, so the execution counts as present and the
        // run can complete on the two cases that did run.
        await using var world = await StartAsync();
        var runId = world.RunId;
        var executions = world.Plan.Executions;
        await SetRunStateAsync(world);
        await RecordAttemptsAsync(world, (executions[0].ExecutionKey, 1, "passed"), (executions[1].ExecutionKey, 1, "passed"));
        await BlockAttemptAsync(world, runId, executions[2].ExecutionKey);
        await StoreEventsAsync(world, runId, new SeededEvent(executions[0].ExecutionKey, 1), new SeededEvent(executions[1].ExecutionKey, 2));
        await CloseSlotAsync(world, runId, declaredLastSequence: 2, declaredCount: 2);

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.case_attempt where org_id = '{world.Org}' and run_id = '{runId:D}' "
            + $"and execution_key = '{executions[2].ExecutionKey}' and started_at is null and finished_at is null"))
            .ShouldBe(1, "the blocked attempt of the third execution carries no timestamps");

        var (outcome, finalization) = await ReconcileAndFinishAsync(world, runId);

        outcome.Comparison.ShouldNotBeNull().Missing.ShouldBeEmpty(Describe(outcome));
        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Complete);
        (await RevisionAsync(world, runId)).Missing.ShouldBeEmpty("a timestamp-free blocked row is an attempt, not a gap");
    }

    [Fact]
    public async Task Reconcile_EveryUncoveredExecution_NamedOnceInComparison()
    {
        // Two executions never recorded an attempt: the comparison names each of them (so the operator learns which
        // cases are missing), while the revision stores the closed vocabulary code once.
        await using var world = await StartAsync();
        var runId = world.RunId;
        var executions = world.Plan.Executions;
        await SetRunStateAsync(world);
        await RecordAttemptsAsync(world, (executions[0].ExecutionKey, 1, "passed"));
        await StoreEventsAsync(world, runId, new SeededEvent(executions[0].ExecutionKey, 1));
        await CloseSlotAsync(world, runId, declaredLastSequence: 1, declaredCount: 1);

        var (outcome, finalization) = await ReconcileAndFinishAsync(world, runId);

        var comparison = outcome.Comparison.ShouldNotBeNull();
        comparison.Missing.Select(reason => reason.Detail).ShouldBe(
            [$"execution_key={executions[1].ExecutionKey}", $"execution_key={executions[2].ExecutionKey}"],
            Describe(outcome));
        comparison.Missing.ShouldAllBe(reason => reason.Code == ReconcileMissingCodes.MissingResult);
        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Incomplete);
        (await RevisionAsync(world, runId)).Missing.ShouldBe([ReconcileMissingCodes.MissingResult]);
    }

    [Fact]
    public async Task Reconcile_MoreUncoveredExecutionsThanTheReadCap_ReportsTheTrueTotal()
    {
        // One planned execution more than the coverage read returns: the comparison names the lowest execution keys the
        // read selected and one counted entry for the rest, so the gap is never understated, the attempt never builds one
        // object per execution of a huge plan, and two attempts over the same rows select the same subset.
        var cap = ReconcileSql.UncoveredExecutionLimit;
        await using var world = await StartAsync(planned: cap + 1);
        var runId = world.RunId;
        await SetRunStateAsync(world);
        await CloseSlotAsync(world, runId, declaredLastSequence: 0, declaredCount: 0);

        var (outcome, finalization) = await ReconcileAndFinishAsync(world, runId);

        var comparison = outcome.Comparison.ShouldNotBeNull(Describe(outcome));
        comparison.Missing.Count.ShouldBe(cap + 1, Describe(outcome));
        comparison.Missing.ShouldAllBe(reason => reason.Code == ReconcileMissingCodes.MissingResult);
        comparison.Missing.Count(reason => reason.ProducerId is not null).ShouldBe(cap);
        comparison.Missing
            .Where(reason => reason.ProducerId is not null)
            .Select(reason => reason.Detail)
            .Order(StringComparer.Ordinal)
            .ToArray()
            .ShouldBe(
                [.. world.Plan.Executions
                    .Select(execution => $"execution_key={execution.ExecutionKey}")
                    .Order(StringComparer.Ordinal)
                    .Take(cap)],
                "the read returns the lowest execution keys, so its subset cannot move between attempts");

        var counted = comparison.Missing.Single(reason => reason.ProducerId is null);
        counted.Detail.ShouldBe(
            string.Create(CultureInfo.InvariantCulture, $"uncovered_total={cap + 1};listed={cap}"),
            "the true total of the plan's uncovered executions, not what the read returned");

        finalization.ShouldNotBeNull().Completeness.ShouldBe(ReconcileFinisher.Incomplete);
        (await RevisionAsync(world, runId)).Missing.ShouldBe([ReconcileMissingCodes.MissingResult]);
    }

    // ------------------------------------------------------------------ world and seeding

    /// <summary>One stored, acknowledged event of the run (the columns the aggregate and the status evidence read).</summary>
    /// <param name="ExecutionKey">The planned execution the event belongs to.</param>
    /// <param name="Sequence">The producer's sequence; unique per <c>(org, run, producer)</c>.</param>
    /// <param name="Type">The event type, <c>test.finished</c> unless the case seeds a started attempt.</param>
    /// <param name="NormalizedStatus">The normalized status, or null for "the producer reported none".</param>
    /// <param name="AttemptNo">The attempt number the event reports.</param>
    private sealed record SeededEvent(
        string ExecutionKey,
        long Sequence,
        string Type = "test.finished",
        string? NormalizedStatus = "passed",
        int AttemptNo = 1);

    /// <summary>
    /// One migrated database with a run of <paramref name="planned"/> planned executions and the one producer slot these
    /// cases finalize. No attempt is recorded up front: each case seeds exactly the attempts its scenario is about.
    /// </summary>
    /// <param name="planned">How many executions the run plans.</param>
    private Task<IngestTestWorld> StartAsync(int planned = Planned) =>
        IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), planned: planned, slots: 1);

    /// <summary>The run has to be <c>collecting</c> for the reconciler to compare it at all.</summary>
    /// <param name="world">The world whose run moves.</param>
    private static Task SetRunStateAsync(IngestTestWorld world) => world.SetRunStateAsync("collecting");

    /// <summary>
    /// Records one attempt per tuple through the real writer, so the schema owns the row and resolves each case: a
    /// terminal status as a producer reports it (<c>passed</c>, <c>failed</c>).
    /// </summary>
    /// <param name="world">The world whose run the attempts belong to.</param>
    /// <param name="attempts">The (execution key, attempt number, raw status) triples to write.</param>
    private static async Task RecordAttemptsAsync(IngestTestWorld world, params (string ExecutionKey, int AttemptNo, string Status)[] attempts)
    {
        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        var inputs = attempts
            .Select(attempt => new CaseAttemptInput(attempt.ExecutionKey, attempt.AttemptNo, attempt.Status))
            .ToArray();

        var outcomes = await scope.WriteAsync(world.RunId, inputs);
        outcomes.Count.ShouldBe(inputs.Length);
        outcomes.ShouldAllBe(outcome => outcome.Status == CaseAttemptWriteStatus.Accepted);
        await scope.CommitAsync();
    }

    /// <summary>
    /// Stores the events as the migration role (<c>event_owner_backfill</c> admits the owner, 0034), one statement per
    /// event inside one transaction, so the producer's sequence is stored as the batch it claims to be.
    /// </summary>
    /// <param name="world">The world whose run receives the events.</param>
    /// <param name="runId">The run the events report on.</param>
    /// <param name="events">The events to store, in the order given.</param>
    private async Task StoreEventsAsync(IngestTestWorld world, Guid runId, params SeededEvent[] events)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));

        foreach (var stored in events)
        {
            (await scope.ExecuteAsync(
                """
                insert into public.event
                  (id, org_id, run_id, event_id, producer_id, sequence, execution_key, attempt_no, type,
                   normalized_status, payload_hash)
                values (@id, @org_id, @run_id, @event_id, @producer_id, @sequence, @execution_key, @attempt_no, @type,
                        @normalized_status, @payload_hash)
                """,
                ("id", Guid.CreateVersion7()),
                ("org_id", world.Org),
                ("run_id", runId),
                ("event_id", Guid.CreateVersion7()),
                ("producer_id", Producer),
                ("sequence", stored.Sequence),
                ("execution_key", stored.ExecutionKey),
                ("attempt_no", stored.AttemptNo),
                ("type", stored.Type),
                ("normalized_status", (object?)stored.NormalizedStatus ?? DBNull.Value),
                ("payload_hash", "sha256:" + new string('c', 64)))).ShouldBe(1);
        }

        await scope.CommitAsync();
    }

    /// <summary>
    /// One <c>blocked</c> attempt in the shape only the server produces (the blocked-attempt resolver): a
    /// failed setup stopped the case before it ran, so the row carries neither <c>started_at</c> nor <c>finished_at</c>,
    /// and the writer never receives this status from a producer (its raw vocabulary has no <c>blocked</c>).
    /// </summary>
    /// <param name="world">The world whose run receives the attempt.</param>
    /// <param name="runId">The run the attempt belongs to.</param>
    /// <param name="executionKey">The planned execution the attempt answers.</param>
    private async Task BlockAttemptAsync(IngestTestWorld world, Guid runId, string executionKey)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await scope.ExecuteAsync(
            """
            insert into public.case_attempt
              (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status)
            select @id, @org_id, @run_id, pe.execution_key, tr.case_id, 1, 'blocked', 'blocked'
              from public.planned_execution pe
              join public.test_revision tr on tr.org_id = pe.org_id and tr.id = pe.case_revision_id
             where pe.org_id = @org_id and pe.run_id = @run_id and pe.execution_key = @execution_key
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", world.Org),
            ("run_id", runId),
            ("execution_key", executionKey))).ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>
    /// Closes the seeded slot the way a finalize writes it: <c>closed_at</c> plus the two declared values in one statement
    /// (0035's all-or-nothing check), so the reconciler sees a first-close declaration.
    /// </summary>
    /// <param name="world">The world whose slot is closed.</param>
    /// <param name="runId">The run the slot belongs to.</param>
    /// <param name="declaredLastSequence">The declared high-water mark.</param>
    /// <param name="declaredCount">The declared event count.</param>
    private async Task CloseSlotAsync(IngestTestWorld world, Guid runId, long declaredLastSequence, int declaredCount)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await scope.ExecuteAsync(
            """
            update public.producer_slot
               set closed_at = now(), declared_last_sequence = @last_sequence, declared_count = @declared_count
             where org_id = @org_id and run_id = @run_id and producer_id = @producer_id
            """,
            ("last_sequence", declaredLastSequence),
            ("declared_count", declaredCount),
            ("org_id", world.Org),
            ("run_id", runId),
            ("producer_id", Producer))).ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>The <c>shard_id</c> the plan row of one execution carries (the id its reason names).</summary>
    /// <param name="world">The world's database.</param>
    /// <param name="executionKey">The planned execution.</param>
    private static async Task<string> ShardOfAsync(IngestTestWorld world, string executionKey) =>
        await world.ScalarAsync<string>(
            $"select shard_id from public.planned_execution where org_id = '{world.Org}' and execution_key = '{executionKey}'");

    // ------------------------------------------------------------------ the consumer's whole attempt, and its reads

    /// <summary>
    /// One full attempt the way the consumer runs it: reconcile, then — only for a compared attempt —
    /// freeze and persist the revision, all in ONE ambient tenant transaction, committed when both return.
    /// </summary>
    /// <param name="world">The world the attempt runs against.</param>
    /// <param name="runId">The run to reconcile.</param>
    private static async Task<(ReconcileOutcome Outcome, ReconcileFinalization? Finalization)> ReconcileAndFinishAsync(
        IngestTestWorld world,
        Guid runId)
    {
        await using var services = world.Api.Services.CreateAsyncScope();
        await using var scope = await services.ServiceProvider.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, null, false, Token);
        var outcome = await services.ServiceProvider.GetRequiredService<IRunReconciler>()
            .ReconcileAsync(runId, lateTrigger: false, Token);
        ReconcileFinalization? finalization = null;
        if (outcome.Disposition == ReconcileDisposition.Compared && outcome.Comparison is { } comparison)
        {
            finalization = await services.ServiceProvider.GetRequiredService<IReconcileFinisher>()
                .FinishAsync(runId, comparison, ReconcileSettlement.DecideCollection, Token);
        }

        await scope.CommitAsync();

        return (outcome, finalization);
    }

    /// <summary>The persisted completeness and missing codes of a run's latest revision.</summary>
    /// <param name="Completeness">The stored completeness.</param>
    /// <param name="Missing">The stored missing codes in order.</param>
    private sealed record RevisionSummary(string Completeness, IReadOnlyList<string> Missing);

    /// <summary>Reads the latest revision of one run as one delimited scalar (the suite's read style).</summary>
    /// <param name="world">The world's database.</param>
    /// <param name="runId">The run whose latest revision is read.</param>
    private static async Task<RevisionSummary> RevisionAsync(IngestTestWorld world, Guid runId)
    {
        var row = await world.ScalarAsync<string>(
            "select er.completeness || '|' || array_to_string(er.missing, ',') "
            + $"from public.evidence_revision er where er.org_id = '{world.Org}' and er.run_id = '{runId:D}' "
            + "order by er.revision_no desc limit 1");

        var parts = row.Split('|');

        return new RevisionSummary(parts[0], parts[1].Length == 0 ? [] : parts[1].Split(','));
    }

    /// <summary>The run's state, as the transition wrote it.</summary>
    /// <param name="world">The world's database.</param>
    /// <param name="runId">The run to read.</param>
    private static Task<string> StateAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<string>($"select state from public.run where id = '{runId:D}'");

    /// <summary>The reasons of one outcome as <c>code:producer:detail</c>, for a readable failure message.</summary>
    /// <param name="outcome">The attempt's outcome.</param>
    private static string Describe(ReconcileOutcome outcome) =>
        outcome.Comparison is null
            ? string.Create(CultureInfo.InvariantCulture, $"disposition={outcome.Disposition}, no comparison")
            : $"disposition={outcome.Disposition}, missing=[{string.Join(" | ", outcome.Comparison.Missing.Select(
                reason => $"{reason.Code}:{reason.ProducerId}:{reason.Detail}"))}]";
}
