// The durable run state history of migration 0082 against a real PostgreSQL: the trigger on public.run is the only
// writer, so every path that moves a run's state appends exactly one row — the creation row first (sequence 1, no
// from state), then one per real change — no role can rewrite or delete one, and two writers of one run serialise on
// the run row's own lock so the run's sequence stays unique and gapless. The read half (the served state_history) is
// the API suite's; this file is the write, the permission and the append-only half.
#pragma warning disable CA1861
using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed partial class RunStateEventTests
{
    private readonly PostgresTenancyFixture _fixture;

    public RunStateEventTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ the writer's rows

    /// <summary>
    /// The create path inserts the run as <c>queued</c>, so the creation row is the only row a fresh run has: the
    /// lingering <c>(insert)</c> marker with no from state, naming the state the insert carried.
    /// </summary>
    [Fact]
    public async Task CreateRun_AppendsTheCreationRow()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        (await HistoryAsync(world, runId)).ShouldBe("(insert)->queued", "the create path's INSERT is the creation row");
        (await SequencesAsync(world, runId)).ShouldBe([1]);
        (await RowCountAsync(world, runId)).ShouldBe(1);
    }

    /// <summary>
    /// The forward chain from the state the API never writes: a seeded run starts <c>created</c>, and each of the four
    /// applied edges appends its own row, so the history is sequence 1..5 in order — gapless, because the allocation
    /// is <c>max(seq) + 1</c> under the run row's own lock, and the creation row comes first with no from state.
    /// </summary>
    [Fact]
    public async Task TransitionRun_FromCreatedThroughComplete_AppendsOneRowPerEdge()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        // The one state the API never inserts: the seeder writes the row as `created`, so `created → queued` is this
        // run's first transition rather than a no-op.
        var runId = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);
        (await HistoryAsync(world, runId)).ShouldBe(
            "(insert)->created",
            "the creation row is first: sequence 1, no from state, the state the insert carried");

        foreach (var to in new[] { "queued", "running", "collecting", "complete" })
        {
            var step = await TransitionAsync(world, runId, to);

            step.IsSuccess.ShouldBeTrue(step.Error?.Message);
            step.Value.Outcome.ShouldBe(TransitionOutcome.Applied, to);
            step.Value.State.ShouldBe(to);
        }

        (await HistoryAsync(world, runId)).ShouldBe(
            "(insert)->created,created->queued,queued->running,running->collecting,collecting->complete");
        (await SequencesAsync(world, runId)).ShouldBe([1, 2, 3, 4, 5], "sequence is the run's own 1-based, gapless count");
        (await RowCountAsync(world, runId)).ShouldBe(5);
    }

    /// <summary>
    /// A request for the state a run already holds is the guarded transition's no-op, and a direct update that assigns
    /// the same state again makes the trigger write nothing — so the history carries no zero-length row and sequence
    /// stays gapless.
    /// </summary>
    [Fact]
    public async Task TransitionRun_SameState_AppendsNoRow()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await RunningRunAsync(world);
        var before = await HistoryAsync(world, runId);
        before.ShouldBe("(insert)->queued,queued->running");

        var noOp = await TransitionAsync(world, runId, "running");

        noOp.IsSuccess.ShouldBeTrue(noOp.Error?.Message);
        noOp.Value.Outcome.ShouldBe(TransitionOutcome.NoOp);
        (await HistoryAsync(world, runId)).ShouldBe(before, "the handler's no-op transition appends nothing");

        // The trigger's own guard: the same state written directly, by the role that may move `state`, is not an
        // update of state that changes anything.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync($"update public.run set state = 'running' where id = '{runId:D}'")).ShouldBe(1);
            await scope.CommitAsync();
        }

        (await HistoryAsync(world, runId)).ShouldBe(before, "a same-state update writes no history row");
        (await SequencesAsync(world, runId)).ShouldBe([1, 2]);
    }

    /// <summary>
    /// The cancel route through the real host: one row for the one edge it applies, and no second row for the repeated
    /// cancel the route answers idempotently.
    /// </summary>
    [Fact]
    public async Task CancelRun_AppendsExactlyOneTransitionRow()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await RunningRunAsync(world);

        var answer = await CancelRunTestData.CancelAsync(world, runId);
        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);

        (await HistoryAsync(world, runId)).ShouldBe("(insert)->queued,queued->running,running->canceled");
        (await RowCountAsync(world, runId)).ShouldBe(3, "one transition row for the one transition");

        var repeated = await CancelRunTestData.CancelAsync(world, runId);
        repeated.Status.ShouldBe(HttpStatusCode.Accepted, repeated.ErrorMessage);
        (await RowCountAsync(world, runId)).ShouldBe(3, "a repeated cancel applies no edge, so it appends no row");
    }

    /// <summary>
    /// Ten transitions in one transaction — the fastest sequential writer there is — so every row is one sequence
    /// higher than the last and the allocation neither skips nor repeats: the aggregate reads its own uncommitted rows,
    /// which is what lets one statement allocate and insert.
    /// </summary>
    [Fact]
    public async Task TenSequentialStateWrites_KeepTheSequenceGapless()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            for (var write = 0; write < 10; write++)
            {
                var state = write % 2 == 0 ? "running" : "collecting";

                (await scope.ExecuteAsync($"update public.run set state = '{state}' where id = '{runId:D}'"))
                    .ShouldBe(1, $"write {write}");
            }

            await scope.CommitAsync();
        }

        (await SequencesAsync(world, runId)).ShouldBe([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);
        (await RowCountAsync(world, runId)).ShouldBe(11, "the creation row plus one row per applied change");
    }

    /// <summary>
    /// Two writers of one run at once: they serialise on the run row's lock (the update holds it until commit), so the
    /// second reads the first one's committed sequence and the two rows are 2 and 3 — no gap, no duplicate — and the
    /// loser's row names the state the winner committed, never the one it read before blocking.
    /// </summary>
    [Fact]
    public async Task TwoConcurrentStateWrites_KeepTheSequenceGapless()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);
        var firstUpdated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var winner = Detached(
            async () =>
            {
                await using var scope =
                    await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
                (await scope.ExecuteAsync($"update public.run set state = 'running' where id = '{runId:D}'")).ShouldBe(1);
                firstUpdated.SetResult();
                await releaseFirst.Task.WaitAsync(TestContext.Current.CancellationToken);
                await scope.CommitAsync();
            },
            TestContext.Current.CancellationToken);

        Task? loser = null;
        try
        {
            // Bounded, so a winner that never reaches its update fails this case instead of hanging on the signal.
            await firstUpdated.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            loser = Detached(
                async () =>
                {
                    await using var scope =
                        await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
                    (await scope.ExecuteAsync($"update public.run set state = 'collecting' where id = '{runId:D}'"))
                        .ShouldBe(1);
                    await scope.CommitAsync();
                },
                TestContext.Current.CancellationToken);

            // The happens-before edge, never a sleep: the second writer is provably parked on the first one's row
            // lock before the first commits.
            await AwaitParkedOnRunLockAsync(world, runId, loser);
        }
        finally
        {
            // Always hand the winner its commit signal and let both writers finish: a transaction left parked on the
            // run row would hold that lock for every later case in this database, and an arrange that failed above
            // must not leave one behind.
            releaseFirst.TrySetResult();
            await SettleAsync(winner, loser);
        }

        await Task.WhenAll(winner, loser.ShouldNotBeNull());

        (await SequencesAsync(world, runId)).ShouldBe([1, 2, 3], "unique and gapless across the two writers");
        (await HistoryAsync(world, runId)).ShouldBe(
            "(insert)->queued,queued->running,running->collecting",
            "the blocked writer's row names the state the winner committed");
    }

    // ------------------------------------------------------------------ privileges and append-only

    /// <summary>
    /// The app role holds SELECT only: no insert, update, delete or truncate grant, so the trigger (which runs as the
    /// table owner) is the only path that writes a row and a statement that names the table directly is answered
    /// 42501. The operator keeps its read grant, the scheduler holds nothing, and both row-security flags are on.
    /// </summary>
    [Fact]
    public async Task AppRole_HoldsSelectOnly_AndEveryMutationIsRefused()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var database = world.Database;
        var runId = await CreateQueuedRunAsync(world);

        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "run_state_event", "select")).ShouldBeTrue();
        foreach (var privilege in new[] { "insert", "update", "delete", "truncate" })
        {
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "run_state_event", privilege))
                .ShouldBeFalse($"evidgate_app must hold no {privilege} grant: the trigger is the only writer");
        }

        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_operator", "run_state_event", "select"))
            .ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_worker_sched", "run_state_event", "select"))
            .ShouldBeFalse();

        (await _fixture.ScalarAsync<bool>(
            database,
            "select relrowsecurity and relforcerowsecurity from pg_class where oid = 'public.run_state_event'::regclass"))
            .ShouldBeTrue("enable plus force: the owner is subject to the policy too");

        // The grants in behaviour: each statement the table itself would accept is refused before any row is written.
        await using (var insert = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), world.Org))
        {
            (await insert.CaptureErrorAsync(
                "insert into public.run_state_event (id, org_id, run_id, seq, from_state, to_state) "
                + "values (gen_random_uuid(), @org_id, @run_id, 9, 'queued', 'running')",
                ("org_id", world.Org),
                ("run_id", runId))).SqlState.ShouldBe("42501");
        }

        await using (var update = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), world.Org))
        {
            (await update.CaptureErrorAsync(
                $"update public.run_state_event set to_state = 'complete' where run_id = '{runId:D}'"))
                .SqlState.ShouldBe("42501");
        }

        await using (var delete = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), world.Org))
        {
            (await delete.CaptureErrorAsync($"delete from public.run_state_event where run_id = '{runId:D}'"))
                .SqlState.ShouldBe("42501");
        }

        (await HistoryAsync(world, runId)).ShouldBe("(insert)->queued", "no refused statement wrote a row");
    }

    /// <summary>
    /// Append-only for every role, not only the app one: as the table owner an update and a delete each raise 23514
    /// (<c>run_state_event_immutable</c>) and the row survives, so a future grant cannot rewrite history. An erasure
    /// that must remove a run's history disables that trigger first, which is the documented handover.
    /// </summary>
    [Fact]
    public async Task OwnerUpdateOrDelete_IsRefused()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        // One transaction per attempt: the raised exception aborts the transaction it fired in.
        await using (var update = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            var refused = await update.CaptureErrorAsync(
                $"update public.run_state_event set to_state = 'complete' where run_id = '{runId:D}'");

            refused.SqlState.ShouldBe("23514");
            refused.MessageText.ShouldContain("run_state_event_immutable");
        }

        await using (var delete = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            var refused = await delete.CaptureErrorAsync(
                $"delete from public.run_state_event where run_id = '{runId:D}'");

            refused.SqlState.ShouldBe("23514");
            refused.MessageText.ShouldContain("run_state_event_immutable");
        }

        (await HistoryAsync(world, runId)).ShouldBe("(insert)->queued", "both refusals left the row in place");
    }

    // ------------------------------------------------------------------ the migration's own contract

    /// <summary>
    /// The migration file is idempotent in the shape the migrations' file contract allows: re-running its own SQL as
    /// the migration role leaves one table with its two policies and its guard, the two writer triggers on
    /// <c>public.run</c>, and the same grants — and the writer still writes afterwards.
    /// </summary>
    [Fact]
    public async Task ReapplyingTheMigration_KeepsTheWriterItsGuardsAndItsGrants()
    {
        // Dedicated: re-running the DDL takes table-wide locks the rest of this suite must not share.
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        var database = owned.Name;

        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            await owner.ExecuteAsync(await MigrationSqlAsync());
            await owner.CommitAsync();
        }

        (await _fixture.ScalarAsync<bool>(database, "select to_regclass('public.run_state_event') is not null"))
            .ShouldBeTrue();
        (await _fixture.ColumnAsync<string>(
            database,
            "select polname from pg_policy where polrelid = 'public.run_state_event'::regclass order by polname"))
            .ShouldBe(["run_state_event_app_tenant", "run_state_event_owner_backfill"]);
        (await _fixture.ColumnAsync<string>(
            database,
            "select tgname from pg_trigger where tgrelid = 'public.run'::regclass and not tgisinternal "
            + "and tgname like 'run_state_event%' order by tgname"))
            .ShouldBe(["run_state_event_created", "run_state_event_transition"]);
        (await _fixture.ColumnAsync<string>(
            database,
            "select tgname from pg_trigger where tgrelid = 'public.run_state_event'::regclass "
            + "and not tgisinternal order by tgname"))
            .ShouldBe(["run_state_event_immutable"]);
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "run_state_event", "select")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "run_state_event", "insert")).ShouldBeFalse();

        // The writer survived the re-run: a run created afterwards still records its creation row.
        await using var world = await RunCreationTests.StartAsync(_fixture, database: database);
        var runId = await CreateQueuedRunAsync(world);
        (await HistoryAsync(world, runId)).ShouldBe("(insert)->queued");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// One API-created run (state <c>queued</c>): the create path's insert is the creation row, so every case that
    /// needs a known seq 2 starts here.
    /// </summary>
    private static async Task<Guid> CreateQueuedRunAsync(RunCreationTests.World world)
    {
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id("run_id");
    }

    /// <summary>One API-created run driven to <c>running</c> through the guarded transition itself.</summary>
    private static async Task<Guid> RunningRunAsync(RunCreationTests.World world)
    {
        var runId = await CreateQueuedRunAsync(world);
        var step = await TransitionAsync(world, runId, "running");

        step.IsSuccess.ShouldBeTrue(step.Error?.Message);
        step.Value.State.ShouldBe("running");

        return runId;
    }

    /// <summary>
    /// One committed transition in its own tenant transaction: the handler, then the audit flush and the commit only
    /// on success — the order the request pipeline uses.
    /// </summary>
    private static async Task<Result<TransitionResult>> TransitionAsync(
        RunCreationTests.World world,
        Guid runId,
        string to)
    {
        await using var scope = await RunTransitionScope.BeginAsync(world.Api, world.Org, world.Project);
        var result = await scope.TransitionAsync(new TransitionRunStateCommand(runId, to));

        if (result.IsSuccess)
        {
            await scope.FlushAuditAsync();
            await scope.CommitAsync();
        }

        return result;
    }

    /// <summary>
    /// Runs <paramref name="work"/> in a fresh execution flow: <see cref="ExecutionContext.SuppressFlow"/> stops this
    /// flow's ambient tenant scope from flowing into the child, so the child's <c>BeginAsync</c> opens the second scope
    /// the race needs instead of refusing a second one here.
    /// </summary>
    private static Task Detached(Func<Task> work, CancellationToken cancellationToken)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work, cancellationToken);
        }
    }

    /// <summary>
    /// Waits — never using a sleep as synchronisation — until <paramref name="loser"/>'s update is parked on the
    /// winner's uncommitted run row, and asserts it has not completed. The statement text carries the run id, so another
    /// suite's unrelated lock wait in the same database cannot satisfy the poll.
    /// </summary>
    private async Task AwaitParkedOnRunLockAsync(RunCreationTests.World world, Guid runId, Task loser)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (await _fixture.ScalarAsync<int>(
                   world.Database,
                   "select count(*)::int from pg_stat_activity "
                   + $"where datname = current_database() and wait_event_type = 'Lock' and pid <> pg_backend_pid() "
                   + $"and query like '%{runId:D}%'") == 0)
        {
            loser.IsCompleted.ShouldBeFalse("the second transition finished while the first still held the row lock");
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the second transition never blocked on the run row lock");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        loser.IsCompleted.ShouldBeFalse("the second transition must still be waiting while the first is uncommitted");
    }

    /// <summary>
    /// Waits for the case's detached writers to finish, bounded so a stuck writer cannot hang the test. It never
    /// throws: a writer's own failure is the case's outcome and is asserted by the awaits that follow, so cleanup here
    /// can never mask or replace an arrange failure.
    /// </summary>
    private static async Task SettleAsync(Task winner, Task? loser)
    {
        var writers = loser is null ? new[] { winner } : new[] { winner, loser };
        var all = Task.WhenAll(writers);

        await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        if (all.IsCompletedSuccessfully)
        {
            await all;
        }
    }

    /// <summary>
    /// The run's history as one delimited scalar, in sequence order; the creation row reads <c>(insert)-&gt;x</c>.
    /// </summary>
    private Task<string> HistoryAsync(RunCreationTests.World world, Guid runId) =>
        _fixture.ScalarAsync<string>(
            world.Database,
            "select coalesce(string_agg(coalesce(from_state, '(insert)') || '->' || to_state, ',' order by seq), '') "
            + $"from public.run_state_event where org_id = '{world.Org:D}' and run_id = '{runId:D}'");

    /// <summary>The run's recorded sequences in order — the gaplessness assertion.</summary>
    private Task<IReadOnlyList<int>> SequencesAsync(RunCreationTests.World world, Guid runId) =>
        _fixture.ColumnAsync<int>(
            world.Database,
            $"select seq from public.run_state_event where run_id = '{runId:D}' order by seq");

    /// <summary>How many history rows the run has.</summary>
    private Task<long> RowCountAsync(RunCreationTests.World world, Guid runId) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.run_state_event where org_id = '{world.Org:D}' and run_id = '{runId:D}'");

    /// <summary>The migration file's own SQL, executed as the migration role — the re-run path.</summary>
    private static Task<string> MigrationSqlAsync() =>
        File.ReadAllTextAsync(
            Path.Combine(PostgresTenancyFixture.RepoMigrationsPath, "0082_orchestration_run_state_event.sql"),
            TestContext.Current.CancellationToken);
}
