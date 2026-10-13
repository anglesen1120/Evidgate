// 05.3.1-T1.1/T1.2/T1.3 through the real host and a real PostgreSQL: the guarded run state transition
// (00 §8.1) inside a tenant transaction the test owns — its FOR UPDATE read, the T82 classification, the
// outbox row, the audit row and the version bump — plus the two races the story names (concurrent first
// batch, cancel vs reconciler verdict).
#pragma warning disable CA1861
using System.Net;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Orchestrator.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.3.1-T1.1–T1.3 (AC1–AC6): the in-process run state transition, driven the way its callers drive it —
/// inside the caller's own tenant transaction, with the audit unit opened and flushed exactly as
/// <c>TenantTransactionBehaviour</c> does. The handler is resolved from the real host's container over the
/// app role, so the column grant, RLS and the version trigger are the production ones.
/// </summary>
/// <remarks>
/// The two races are deterministic without sleeps: the test makes the winner take the row lock first (it
/// awaits its own <c>FOR UPDATE</c> read), starts the second transaction, and only then commits the winner —
/// so the loser is provably waiting on that lock when it reads the row.
/// </remarks>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class RunStateTransitionTests
{
    /// <summary>The outbox task type an applied transition writes (00 §5.3, 05.3.1 §7).</summary>
    private const string StateChangedTask = "orchestrator.run.state_changed";

    /// <summary>The audit action an applied transition writes (00 §7, 05.3.1 §7).</summary>
    private const string StateChangedAction = "run.state_changed";

    private readonly PostgresTenancyFixture _fixture;

    public RunStateTransitionTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ AC1: every declared edge

    /// <summary>
    /// AC1: the forward chain of §6 applies edge by edge — <c>created → queued</c> (the 05.2.1-T4 state the
    /// create transaction writes, here on a seeded <c>created</c> row), then <c>queued → running → collecting
    /// → complete</c> on an API-created run — each one applied once, bumping <c>version</c> by one and
    /// writing its own outbox row and audit entry.
    /// </summary>
    [Fact]
    public async Task TransitionRun_ForwardChain_AppliesEachDeclaredEdge()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        // `created → queued`: the one edge with no production caller (05.2.1-T4 inserts the run as `queued`),
        // so the run is seeded in `created` to exercise it.
        var seeded = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);
        (await StateAsync(world, seeded)).ShouldBe("created");

        var queued = await TransitionAsync(world, seeded, "queued");
        queued.IsSuccess.ShouldBeTrue(queued.Error?.Message);
        queued.Value.Outcome.ShouldBe(TransitionOutcome.Applied);
        queued.Value.State.ShouldBe("queued");
        queued.Value.Version.ShouldBe(1);

        var runId = await CreateQueuedRunAsync(world);
        (await StateAsync(world, runId)).ShouldBe("queued");
        (await VersionAsync(world, runId)).ShouldBe(0);

        var version = 0;
        foreach (var (to, expected) in new[] { ("running", "running"), ("collecting", "collecting"), ("complete", "complete") })
        {
            var step = await TransitionAsync(world, runId, to);
            step.IsSuccess.ShouldBeTrue(step.Error?.Message);
            step.Value.Outcome.ShouldBe(TransitionOutcome.Applied, to);
            step.Value.State.ShouldBe(expected, to);
            version++;
            step.Value.Version.ShouldBe(version, to);
            (await VersionAsync(world, runId)).ShouldBe(version, to);
        }

        (await StateAsync(world, runId)).ShouldBe("complete");
        (await OutboxTargetsAsync(world, runId)).ShouldBe(["running", "collecting", "complete"]);
        (await OutboxTargetsAsync(world, seeded)).ShouldBe(["queued"]);
        (await AuditRowsAsync(world, runId)).ShouldBe(3);
        (await AuditRowsAsync(world, seeded)).ShouldBe(1);

        // Every applied transition wrote exactly one outbox row of the story's task type (00 §5.3).
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where aggregate_id = '{runId}' and task_type = '{StateChangedTask}'")).ShouldBe(3);
    }

    /// <summary>
    /// AC1: the side exits apply from every declared source — <c>canceled</c> from each non-terminal state
    /// and <c>incomplete</c> from <c>queued</c>, <c>running</c> and <c>collecting</c>.
    /// </summary>
    [Fact]
    public async Task TransitionRun_SideExits_ApplyFromEveryDeclaredSource()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        foreach (var (from, to) in new[]
                 {
                     ("created", "canceled"),
                     ("queued", "canceled"),
                     ("running", "canceled"),
                     ("collecting", "canceled"),
                     ("queued", "incomplete"),
                     ("running", "incomplete"),
                     ("collecting", "incomplete"),
                 })
        {
            var runId = await RunInStateAsync(world, from);
            var drive = DriveTo(from).ToList();

            var result = await TransitionAsync(world, runId, to);

            result.IsSuccess.ShouldBeTrue($"{from} -> {to}: {result.Error?.Message}");
            result.Value.Outcome.ShouldBe(TransitionOutcome.Applied, $"{from} -> {to}");
            result.Value.State.ShouldBe(to, $"{from} -> {to}");
            result.Value.Version.ShouldBe(drive.Count + 1, $"{from} -> {to}");
            (await StateAsync(world, runId)).ShouldBe(to, $"{from} -> {to}");
            (await OutboxTargetsAsync(world, runId)).ShouldBe(drive.Append(to).ToArray(), $"{from} -> {to}");
            (await AuditRowsAsync(world, runId)).ShouldBe(drive.Count + 1, $"{from} -> {to}");
        }
    }

    // ------------------------------------------------------------------ AC2/AC3: no-ops and forward-skips

    /// <summary>
    /// AC2 (T82): a request for the state the run holds, or one it has already passed on the forward path,
    /// is a no-op returning the current state — no outbox row, no audit row and no version bump.
    /// </summary>
    [Fact]
    public async Task TransitionRun_SameOrPassedState_NoOp()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await RunInStateAsync(world, "running");
        var version = await VersionAsync(world, runId);
        version.ShouldBe(1, "queued -> running is one apply");

        foreach (var to in new[] { "running", "queued", "created" })
        {
            var result = await TransitionAsync(world, runId, to);

            result.IsSuccess.ShouldBeTrue($"{to}: {result.Error?.Message}");
            result.Value.Outcome.ShouldBe(TransitionOutcome.NoOp, to);
            result.Value.State.ShouldBe("running", to);
            result.Value.Version.ShouldBe(version, to);
        }

        (await StateAsync(world, runId)).ShouldBe("running");
        (await VersionAsync(world, runId)).ShouldBe(version);
        (await OutboxTargetsAsync(world, runId)).ShouldBe(["running"]);
        (await AuditRowsAsync(world, runId)).ShouldBe(1);
    }

    /// <summary>
    /// AC2/AC3 (T82): every request out of a terminal state is the same no-op returning that terminal state
    /// — the idempotent reconcile and cancel retries the story names — for all three terminal states.
    /// </summary>
    [Fact]
    public async Task TransitionRun_TerminalState_NoOpReturnsCurrentState()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        foreach (var terminal in new[] { "complete", "incomplete", "canceled" })
        {
            var runId = await RunInStateAsync(world, "collecting");
            var drive = DriveTo("collecting").ToList();
            (await TransitionAsync(world, runId, terminal)).Value.State.ShouldBe(terminal);
            var version = await VersionAsync(world, runId);

            foreach (var to in new[] { terminal, "complete", "incomplete", "canceled", "running", "queued", "collecting" })
            {
                var result = await TransitionAsync(world, runId, to);

                result.IsSuccess.ShouldBeTrue($"{terminal} -> {to}: {result.Error?.Message}");
                result.Value.Outcome.ShouldBe(TransitionOutcome.NoOp, $"{terminal} -> {to}");
                result.Value.State.ShouldBe(terminal, $"{terminal} -> {to}");
                result.Value.Version.ShouldBe(version, $"{terminal} -> {to}");
            }

            (await StateAsync(world, runId)).ShouldBe(terminal);
            (await VersionAsync(world, runId)).ShouldBe(version);
            (await OutboxTargetsAsync(world, runId)).ShouldBe(drive.Append(terminal).ToArray());
            (await AuditRowsAsync(world, runId)).ShouldBe(drive.Count + 1);
        }
    }

    /// <summary>
    /// AC3: an undeclared forward-skip between non-terminal states is a <c>Result</c> failure (409
    /// <c>invalid_transition</c>) and is never applied — the state, the version and the two journals are
    /// exactly what they were.
    /// </summary>
    [Fact]
    public async Task TransitionRun_ForwardSkip_RejectedAndNotApplied()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await RunInStateAsync(world, "running");
        var version = await VersionAsync(world, runId);

        foreach (var to in new[] { "complete", "incomplete" })
        {
            var result = await TransitionAsync(world, runId, to);

            result.IsSuccess.ShouldBeFalse(to);
            result.Error!.Code.ShouldBe(ApiErrorCode.InvalidTransition, to);
            result.Error.Status.ShouldBe(409, to);
            (await StateAsync(world, runId)).ShouldBe("running", to);
            (await VersionAsync(world, runId)).ShouldBe(version, to);
            (await OutboxTargetsAsync(world, runId)).ShouldBe(["running"], to);
            (await AuditRowsAsync(world, runId)).ShouldBe(1, to);
        }

        // A queued run cannot jump to collecting either (it has not passed running).
        var queued = await CreateQueuedRunAsync(world);
        var skipped = await TransitionAsync(world, queued, "collecting");

        skipped.Error!.Code.ShouldBe(ApiErrorCode.InvalidTransition);
        (await StateAsync(world, queued)).ShouldBe("queued");
        (await OutboxTargetsAsync(world, queued)).ShouldBeEmpty();
        (await AuditRowsAsync(world, queued)).ShouldBe(0);
    }

    /// <summary>422 <c>request_invalid</c>: a target outside the 00 §8.1 vocabulary is refused before any read.</summary>
    [Fact]
    public async Task TransitionRun_UnknownToState_RequestInvalid()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        var result = await TransitionAsync(world, runId, "finished");

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.RequestInvalid);
        result.Error.Status.ShouldBe(422);
        result.Error.Details!.ShouldContain(detail => detail.Path == "/to_state");
        (await StateAsync(world, runId)).ShouldBe("queued");
        (await OutboxTargetsAsync(world, runId)).ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ tenancy

    /// <summary>
    /// The tenancy boundary of the transition (00 §5.4): an unknown run id and another tenant's run id are
    /// the same 404 — RLS hides the foreign row, so the handler cannot even tell them apart.
    /// </summary>
    [Fact]
    public async Task TransitionRun_UnknownOrForeignRun_NotFound()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");
        await CreateProjectAsync(world.Client, otherUser, otherOrg, "theirs");
        var foreignRun = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, otherOrg);
        (await StateAsync(world, foreignRun)).ShouldBe("created");

        foreach (var runId in new[] { Guid.CreateVersion7(), foreignRun })
        {
            var result = await TransitionAsync(world, runId, "queued");

            result.IsSuccess.ShouldBeFalse(runId.ToString("D"));
            result.Error!.Code.ShouldBe(ApiErrorCode.NotFound, runId.ToString("D"));
            result.Error.Status.ShouldBe(404, runId.ToString("D"));
        }

        (await StateAsync(world, foreignRun)).ShouldBe("created", "the other tenant's run is untouched");
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.outbox where aggregate_id = '{foreignRun}'")).ShouldBe(0);
    }

    // ------------------------------------------------------------------ AC6: optimistic concurrency

    /// <summary>
    /// AC6 (00 §5.5 C4): a transition against a stale <c>version</c> fails 412 <c>precondition_failed</c>
    /// with the row's current version in <c>details</c> and writes nothing; the same request against the
    /// current version applies, and then reports the version it moved the row to.
    /// </summary>
    [Fact]
    public async Task TransitionRun_StaleVersion_412()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        await using (var stale = await RunTransitionScope.BeginAsync(world.Api, world.Org, world.Project))
        {
            var result = await stale.TransitionAsync(new TransitionRunStateCommand(runId, "running", ExpectedVersion: 7));

            result.IsSuccess.ShouldBeFalse();
            result.Error!.Code.ShouldBe(ApiErrorCode.PreconditionFailed);
            result.Error.Status.ShouldBe(412);
            result.Error.Details!.ShouldContain(detail => detail.Path == "If-Match" && detail.Reason == "stale");
            result.Error.Details!.ShouldContain(detail => detail.Path == "version" && detail.Reason == "0");
        }

        (await StateAsync(world, runId)).ShouldBe("queued", "a stale write is never applied");
        (await VersionAsync(world, runId)).ShouldBe(0);
        (await OutboxTargetsAsync(world, runId)).ShouldBeEmpty();

        var applied = await TransitionAsync(world, runId, "running", expectedVersion: 0);
        applied.IsSuccess.ShouldBeTrue(applied.Error?.Message);
        applied.Value.Outcome.ShouldBe(TransitionOutcome.Applied);
        applied.Value.Version.ShouldBe(1);

        await using (var nowStale = await RunTransitionScope.BeginAsync(world.Api, world.Org, world.Project))
        {
            var result = await nowStale.TransitionAsync(new TransitionRunStateCommand(runId, "collecting", ExpectedVersion: 0));

            result.IsSuccess.ShouldBeFalse();
            result.Error!.Code.ShouldBe(ApiErrorCode.PreconditionFailed);
            result.Error.Details!.ShouldContain(detail => detail.Path == "version" && detail.Reason == "1");
        }

        (await StateAsync(world, runId)).ShouldBe("running");
    }

    // ------------------------------------------------------------------ AC5: outbox + audit, one transaction

    /// <summary>
    /// AC5: the state change, its <c>run.state_changed</c> outbox row and its <c>audit_log</c> entry are one
    /// transaction — all three are visible on the scope's own connection before the commit, and a scope that
    /// disposes without committing leaves the state, the outbox and the audit chain untouched.
    /// </summary>
    [Fact]
    public async Task TransitionRun_WritesOutboxAndAudit_SameTransaction()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        // Rolled back: the handler wrote on the transaction, and the dispose took all three writes with it.
        await using (var rollback = await RunTransitionScope.BeginAsync(world.Api, world.Org, world.Project))
        {
            var applied = await rollback.TransitionAsync(new TransitionRunStateCommand(runId, "running"));
            applied.Value.Outcome.ShouldBe(TransitionOutcome.Applied);

            (await rollback.ScalarAsync<long>($"select count(*) from public.outbox where aggregate_id = '{runId}'"))
                .ShouldBe(1, "the outbox row is on this transaction");
            (await rollback.ScalarAsync<long>($"select count(*) from public.audit_log where org_id = '{world.Org}' and object_id = '{runId}'"))
                .ShouldBe(1, "the audit entry is on this transaction");
        }

        (await StateAsync(world, runId)).ShouldBe("queued");
        (await VersionAsync(world, runId)).ShouldBe(0);
        (await OutboxTargetsAsync(world, runId)).ShouldBeEmpty();
        (await AuditRowsAsync(world, runId)).ShouldBe(0);

        // Committed: the same three writes, now durable.
        await using (var commit = await RunTransitionScope.BeginAsync(world.Api, world.Org, world.Project))
        {
            var applied = await commit.TransitionAsync(new TransitionRunStateCommand(runId, "running"));
            applied.Value.Outcome.ShouldBe(TransitionOutcome.Applied);
            await commit.FlushAuditAsync();
            await commit.CommitAsync();
        }

        (await StateAsync(world, runId)).ShouldBe("running");
        (await VersionAsync(world, runId)).ShouldBe(1);
        (await OutboxTargetsAsync(world, runId)).ShouldBe(["running"]);

        // The outbox payload carries the edge and the (still null) evidence pointer the story promises.
        (await _fixture.ColumnAsync<string>(
            world.Database,
            $"select payload ->> 'from' || '->' || (payload ->> 'to') || ':' || coalesce(payload ->> 'evidence_revision_id', 'null') "
            + $"from public.outbox where aggregate_id = '{runId}'")).ShouldBe(["queued->running:null"]);

        var audit = await _fixture.ColumnAsync<string>(
            world.Database,
            $"select actor_type || ':' || actor_id || ':' || object_type || ':' || object_id from public.audit_log "
            + $"where org_id = '{world.Org}' and action = '{StateChangedAction}' and object_id = '{runId}'");
        audit.ShouldBe([$"system:system:run:{runId}"]);
    }

    // ------------------------------------------------------------------ T82: the concurrent first batch

    /// <summary>
    /// AC2/T82: two racing <c>queued → running</c> requests — the second waiting on the first's row lock —
    /// leave exactly one applied transition, one no-op returning <c>running</c>, one outbox row and one
    /// version bump. No 409 or 412 surfaces to ingest.
    /// </summary>
    [Fact]
    public async Task TransitionRun_ConcurrentFirstBatch_OneWinnerOneNoOp()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);
        var command = new TransitionRunStateCommand(runId, "running");

        // The winner takes the row lock first and holds it until its commit.
        await using var winner = await RunTransitionScope.BeginAsync(world.Api, world.Org, world.Project);
        var winnerResult = await winner.TransitionAsync(command);
        winnerResult.IsSuccess.ShouldBeTrue(winnerResult.Error?.Message);
        winnerResult.Value.Outcome.ShouldBe(TransitionOutcome.Applied);

        // The loser starts against the same row while the lock is held, in its own async flow (a second
        // connection and transaction, exactly like a second request).
        var loser = Task.Run(
            async () =>
            {
                await using var scope = await RunTransitionScope.BeginAsync(world.Api, world.Org, world.Project);
                var result = await scope.TransitionAsync(command);
                await scope.FlushAuditAsync();
                await scope.CommitAsync();

                return result;
            },
            TestContext.Current.CancellationToken);

        await winner.FlushAuditAsync();
        await winner.CommitAsync();

        var loserResult = await loser;

        loserResult.IsSuccess.ShouldBeTrue(loserResult.Error?.Message);
        loserResult.Value.Outcome.ShouldBe(TransitionOutcome.NoOp);
        loserResult.Value.State.ShouldBe("running");
        loserResult.Value.Version.ShouldBe(1);

        (await StateAsync(world, runId)).ShouldBe("running");
        (await VersionAsync(world, runId)).ShouldBe(1, "only the winner bumped the version");
        (await OutboxTargetsAsync(world, runId)).ShouldBe(["running"], "exactly one outbox row");
        (await AuditRowsAsync(world, runId)).ShouldBe(1, "exactly one audit entry");
    }

    // ------------------------------------------------------------------ AC4: the cancel race

    /// <summary>
    /// AC4: a cancel committed while a reconciler verdict is in flight wins — the verdict's transaction reads
    /// the row under the same lock, finds it terminal and no-ops, so a canceled run never becomes
    /// <c>complete</c>. The cancel's audit entry carries the caller's own identity (AC5).
    /// </summary>
    [Fact]
    public async Task TransitionRun_CancelRaceWithReconcilerVerdict_StaysCanceled()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await RunInStateAsync(world, "collecting");
        var user = Guid.CreateVersion7();

        await using var cancel = await RunTransitionScope.BeginAsync(
            world.Api, world.Org, world.Project, RequestPrincipal.Member(user));
        var canceled = await cancel.TransitionAsync(
            new TransitionRunStateCommand(runId, "canceled", TransitionActor.Caller));
        canceled.IsSuccess.ShouldBeTrue(canceled.Error?.Message);
        canceled.Value.Outcome.ShouldBe(TransitionOutcome.Applied);
        canceled.Value.State.ShouldBe("canceled");

        var verdict = Task.Run(
            async () =>
            {
                await using var scope = await RunTransitionScope.BeginAsync(world.Api, world.Org, world.Project);
                var result = await scope.TransitionAsync(new TransitionRunStateCommand(runId, "complete"));
                await scope.FlushAuditAsync();
                await scope.CommitAsync();

                return result;
            },
            TestContext.Current.CancellationToken);

        await cancel.FlushAuditAsync();
        await cancel.CommitAsync();

        var verdictResult = await verdict;

        verdictResult.IsSuccess.ShouldBeTrue(verdictResult.Error?.Message);
        verdictResult.Value.Outcome.ShouldBe(TransitionOutcome.NoOp);
        verdictResult.Value.State.ShouldBe("canceled");

        (await StateAsync(world, runId)).ShouldBe("canceled");
        (await VersionAsync(world, runId)).ShouldBe(3, "queued -> running -> collecting -> canceled");
        (await OutboxTargetsAsync(world, runId)).ShouldBe(["running", "collecting", "canceled"]);
        (await AuditRowsAsync(world, runId)).ShouldBe(3);

        var actor = await _fixture.ScalarAsync<string>(
            world.Database,
            $"select actor_type || ':' || actor_id from public.audit_log "
            + $"where org_id = '{world.Org}' and action = '{StateChangedAction}' and object_id = '{runId}' "
            + "and object_type = 'run' order by seq desc limit 1");
        actor.ShouldBe($"user:{user:D}", "the cancel is credited to its caller, not to the system");
    }

    // ------------------------------------------------------------------ P3/P5: the statement budget

    /// <summary>
    /// 05.3.1 §8 (P3/P5): one transition costs one <c>FOR UPDATE</c> read, one <c>UPDATE</c>, one outbox
    /// insert and one audit insert — a fixed, local statement set with no network call and no read of the
    /// planned executions or slots.
    /// </summary>
    [Fact]
    public async Task TransitionRun_CommandCounter_Bounded()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateQueuedRunAsync(world);

        await using var scope = await RunTransitionScope.BeginAsync(world.Api, world.Org, world.Project);
        world.Api.Statements.Clear();

        var applied = await scope.TransitionAsync(new TransitionRunStateCommand(runId, "running"));
        applied.Value.Outcome.ShouldBe(TransitionOutcome.Applied);
        await scope.FlushAuditAsync();

        var statements = world.Api.Statements.Snapshot();

        var runStatements = statements.Where(statement => statement.Contains("public.run", StringComparison.Ordinal)).ToList();
        runStatements.Count.ShouldBe(2, string.Join(Environment.NewLine, statements));
        runStatements.Count(statement => statement.Contains("for update", StringComparison.Ordinal)).ShouldBe(1);
        runStatements.Count(statement => statement.StartsWith("update public.run set state", StringComparison.Ordinal)).ShouldBe(1);
        statements.Count(statement => statement.Contains("public.outbox", StringComparison.Ordinal)).ShouldBe(1);
        statements.Count(statement => statement.Contains("insert into public.audit_log", StringComparison.Ordinal)).ShouldBe(1);
        statements.Count.ShouldBeLessThanOrEqualTo(
            8,
            "one FOR UPDATE read + one UPDATE + one outbox insert + the audit unit's lock/chain/insert: "
            + string.Join(Environment.NewLine, statements));

        await scope.CommitAsync();
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>One API-created run (state <c>queued</c>, version 0).</summary>
    private static async Task<Guid> CreateQueuedRunAsync(RunCreationTests.World world)
    {
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        return created.Id("run_id");
    }

    /// <summary>The committed transitions that drive a fresh run from <c>queued</c> to <paramref name="state"/>.</summary>
    private static IEnumerable<string> DriveTo(string state) => state switch
    {
        "created" or "queued" => [],
        "running" => ["running"],
        "collecting" => ["running", "collecting"],
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "not a non-terminal run state"),
    };

    /// <summary>One committed run in <paramref name="state"/>, driven there through the transition itself.</summary>
    private async Task<Guid> RunInStateAsync(RunCreationTests.World world, string state)
    {
        if (state == "created")
        {
            // The one state the API never writes (05.2.1-T4 inserts `queued`): seeded as the migration role.
            var seeded = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);
            (await StateAsync(world, seeded)).ShouldBe("created");

            return seeded;
        }

        var runId = await CreateQueuedRunAsync(world);
        foreach (var step in DriveTo(state))
        {
            (await TransitionAsync(world, runId, step)).Value.State.ShouldBe(step);
        }

        (await StateAsync(world, runId)).ShouldBe(state);

        return runId;
    }

    /// <summary>
    /// One committed transition in its own tenant transaction: the handler, then the audit flush and the
    /// commit only on success — the <c>TenantTransactionBehaviour</c> order.
    /// </summary>
    private static async Task<Result<TransitionResult>> TransitionAsync(
        RunCreationTests.World world,
        Guid runId,
        string to,
        TransitionActor actor = TransitionActor.System,
        int? expectedVersion = null)
    {
        await using var scope = await RunTransitionScope.BeginAsync(world.Api, world.Org, world.Project);
        var result = await scope.TransitionAsync(new TransitionRunStateCommand(runId, to, actor, expectedVersion));

        if (result.IsSuccess)
        {
            await scope.FlushAuditAsync();
            await scope.CommitAsync();
        }

        return result;
    }

    private Task<string> StateAsync(RunCreationTests.World world, Guid runId) =>
        _fixture.ScalarAsync<string>(world.Database, $"select state from public.run where id = '{runId}'");

    private Task<int> VersionAsync(RunCreationTests.World world, Guid runId) =>
        _fixture.ScalarAsync<int>(world.Database, $"select version from public.run where id = '{runId}'");

    /// <summary>The <c>to</c> of every committed <c>run.state_changed</c> outbox row of one run, in order.</summary>
    private Task<IReadOnlyList<string>> OutboxTargetsAsync(RunCreationTests.World world, Guid runId) =>
        _fixture.ColumnAsync<string>(
            world.Database,
            $"select payload ->> 'to' from public.outbox where aggregate_id = '{runId}' and task_type = '{StateChangedTask}' "
            + "order by created_at, id");

    /// <summary>The committed <c>run.state_changed</c> audit entries of one run.</summary>
    private Task<long> AuditRowsAsync(RunCreationTests.World world, Guid runId) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.audit_log where org_id = '{world.Org}' and action = '{StateChangedAction}' "
            + $"and object_type = 'run' and object_id = '{runId}'");
}

/// <summary>
/// One tenant transaction the test owns, opened the way the C2 pipeline opens it — the transition handler,
/// the audit trail and the tenant scope resolved from the real host's container (the <c>AttemptScope</c>
/// pattern of 05.3.3-T2).
/// <para>
/// Deliberately <b>not</b> <c>async</c> (tests/README.md, "AsyncLocal rule"): the audit unit and the tenant
/// scope are published into the ambient slots synchronously, in the caller's execution context, and
/// <c>ExecutionContext</c> flows only downwards — an <c>async</c> wrapper would strand both publications in
/// its own frame. Each racing scope therefore runs in its own async flow (a <c>Task.Run</c>), which is what
/// gives it its own connection and transaction.
/// </para>
/// </summary>
internal sealed class RunTransitionScope : IAsyncDisposable
{
    private readonly AsyncServiceScope _services;
    private readonly ITenantScope _scope;
    private readonly IAuditTrail _audit;
    private readonly ITenantDb _db;
    private readonly IDisposable? _principalScope;
    private readonly IDisposable _auditUnit;

    private RunTransitionScope(
        AsyncServiceScope services,
        ITenantScope scope,
        IDisposable auditUnit,
        IDisposable? principalScope)
    {
        _services = services;
        _scope = scope;
        _auditUnit = auditUnit;
        _principalScope = principalScope;

        var provider = services.ServiceProvider;
        Transition = provider.GetRequiredService<IRunStateTransition>();
        _audit = provider.GetRequiredService<IAuditTrail>();
        _db = provider.GetRequiredService<ITenantDb>();
    }

    /// <summary>The handler under test, running in this transaction.</summary>
    internal IRunStateTransition Transition { get; }

    /// <summary>
    /// Opens the tenant transaction for <paramref name="org"/> (narrowed to <paramref name="project"/>), the
    /// audit unit and — when a principal is given — that caller, all in the caller's own execution context.
    /// </summary>
    internal static Task<RunTransitionScope> BeginAsync(
        TenancyApiFactory api,
        Guid org,
        Guid? project = null,
        IRequestPrincipal? principal = null)
    {
        var services = api.Services.CreateAsyncScope();
        var provider = services.ServiceProvider;
        var auditUnit = provider.GetRequiredService<IAuditTrail>().BeginUnit();
        var principalScope = principal is null ? null : provider.GetRequiredService<IRequestPrincipalSetter>().Publish(principal);
        var scopes = provider.GetRequiredService<ITenantScopeFactory>();

        // Continued rather than awaited, for the reason in the type's remarks: the factory publishes the
        // scope synchronously, and this continuation runs on that same context.
        return scopes
            .BeginAsync(org, project, false, TestContext.Current.CancellationToken)
            .ContinueWith(
                opened => new RunTransitionScope(services, opened.GetAwaiter().GetResult(), auditUnit, principalScope),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    /// <summary>One transition in this transaction, uncommitted.</summary>
    internal Task<Result<TransitionResult>> TransitionAsync(TransitionRunStateCommand command) =>
        Transition.TransitionAsync(command, TestContext.Current.CancellationToken).AsTask();

    /// <summary>The audit flush the pipeline runs just before the commit (02.7.1).</summary>
    internal Task<int> FlushAuditAsync() =>
        _audit.FlushAsync(_scope.OrgId, _db, TestContext.Current.CancellationToken);

    /// <summary>The commit boundary; the handler itself never commits. The audit flush is the caller's step.</summary>
    internal Task CommitAsync() => _scope.CommitAsync(TestContext.Current.CancellationToken);

    /// <summary>Reads one value on this transaction — the same connection and transaction the handler wrote on.</summary>
    internal Task<T?> ScalarAsync<T>(string sql) =>
        _db.ScalarAsync<T>(sql, null, TestContext.Current.CancellationToken);

    /// <summary>Disposes the transaction (a rollback when <see cref="CommitAsync"/> was not called) and the container scope.</summary>
    public async ValueTask DisposeAsync()
    {
        _auditUnit.Dispose();
        _principalScope?.Dispose();
        await _scope.DisposeAsync();
        await _services.DisposeAsync();
    }
}
