using System.Diagnostics.Metrics;
using System.Net;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Policy.Application.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The run evaluation against a real PostgreSQL and the real host: one stored verdict per question, a shadow row beside
/// the live one, an attempt that fails before its commit leaves nothing behind, and one latency measurement per
/// committed evaluation.
/// <para>
/// The two-caller case runs each call on its own <see cref="ExecutionContext"/>: a tenant scope is per async flow, so
/// two callers sharing one flow would be refused a second scope — which is a property of the host, not of two workers.
/// Every call therefore starts on a clean flow, exactly as two processes would.
/// </para>
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed partial class EvaluationTaskTests
{
    private readonly PostgresTenancyFixture _fixture;

    public EvaluationTaskTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>How many evaluation rows the database holds right now.</summary>
    private static string CountSql => "select count(*) from public.evaluation";

    // ------------------------------------------------------------------ AC1: two callers, one row

    /// <summary>
    /// Two concurrent evaluations of the same run and revision store exactly one row, and both callers receive that
    /// row: the insert's six-column key admits one writer, the loser reads the winner back instead of writing.
    /// </summary>
    [Fact]
    public async Task Evaluate_TwoConcurrentInvocations_StoreOneRowAndBothReadIt()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var seed = await SeedAsync(world);

        var first = OnOwnFlow(token => EvaluateAsync(world, seed, EvaluationMode.Live, token));
        var second = OnOwnFlow(token => EvaluateAsync(world, seed, EvaluationMode.Live, token));
        var rows = await Task.WhenAll(first, second);

        rows[0].Id.ShouldBe(rows[1].Id, "both callers read the row the winner stored");
        rows[0].Decision.ShouldBe("PASS", "every planned execution passed");
        rows[0].Completeness.ShouldBe("complete");
        rows[0].PolicyHash.ShouldBe(seed.PolicyHash, "the row names the policy revision the run is pinned to");
        rows[0].ManifestHash.ShouldBe(seed.ManifestHash, "and the manifest the evidence froze against");
        (await CountAsync(world, seed)).ShouldBe(1, "the unique key, not the handler, decides how many rows exist");
    }

    // ------------------------------------------------------------------ AC2: shadow beside live

    /// <summary>
    /// A shadow verdict for the same pair is a second row outside the live predicate, and a second live verdict for the
    /// same evidence revision under a different policy revision is refused by the live partial key — never
    /// overwritten, never read back as this attempt's verdict.
    /// </summary>
    [Fact]
    public async Task Evaluate_ShadowBesideLive_InsertsAndSecondLiveKeyIsRefused()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var seed = await SeedAsync(world);

        var live = await EvaluateAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken);
        var shadow = await EvaluateAsync(world, seed, EvaluationMode.Shadow, TestContext.Current.CancellationToken);

        shadow.Id.ShouldNotBe(live.Id);
        shadow.Mode.ShouldBe("shadow");
        shadow.Decision.ShouldBe(live.Decision, "the same evidence and rules decide the same way");
        (await CountAsync(world, seed)).ShouldBe(2, "shadow sits beside live, never instead of it");
        (await CountAsync(world, seed, "where mode = 'live'")).ShouldBe(1);

        // A second live verdict can only differ by policy revision or evaluator version; repin the run to a new
        // revision, so the six-column key is free and the live partial key is the one that refuses the row.
        await RepinAsync(world, seed);

        // The insert handles every unique key with `do nothing`, so the refusal is visible as "no row inserted" and the
        // six-column read-back finds nothing under the new policy revision: the attempt fails closed instead of
        // answering with a verdict that was decided under the previous revision.
        var refusal = await Should.ThrowAsync<InvalidOperationException>(
            () => EvaluateAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken));
        refusal.Message.ShouldContain("already exists under a different policy revision or evaluator version");
        (await CountAsync(world, seed)).ShouldBe(2, "the refused attempt stored nothing");
        (await CountAsync(world, seed, "where mode = 'live'")).ShouldBe(
            1,
            "the live partial key still admits one live row per evidence revision");
    }

    // ------------------------------------------------------------------ AC5: a failed attempt stores nothing

    /// <summary>
    /// An attempt that computes and inserts but never commits leaves no row, and the retry stores the same verdict: the
    /// decision, reasons and hashes are a function of the durable inputs, so a redelivery cannot disagree with the
    /// attempt it replaced.
    /// </summary>
    [Fact]
    public async Task Evaluate_AfterFaultBeforeCommit_ReRunsToIdenticalRow()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var seed = await SeedAsync(world);

        var computed = await EvaluateThenFailAsync(
            world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken);

        (await CountAsync(world, seed)).ShouldBe(0, "the scope rolled back, so the attempt stored nothing");

        var retried = await EvaluateAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken);

        retried.Decision.ShouldBe(computed.Decision);
        retried.Completeness.ShouldBe(computed.Completeness);
        retried.Outcome.ShouldBe(computed.Outcome);
        retried.Reasons.ShouldBe(computed.Reasons);
        retried.Missing.ShouldBe(computed.Missing);
        retried.PolicyHash.ShouldBe(computed.PolicyHash);
        retried.ManifestHash.ShouldBe(computed.ManifestHash);
        retried.ExceptionsHash.ShouldBe(computed.ExceptionsHash);
        retried.EvaluatorVersion.ShouldBe(computed.EvaluatorVersion);
        (await CountAsync(world, seed)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ the task handler

    /// <summary>
    /// The frozen-evidence task handler, called directly with a tenant scope, evaluates the run its payload names: the
    /// stored row carries the pinned policy's hash and the revision's manifest hash, which only the durable reads can
    /// supply. The queue's claim and dispatch are exercised by the reconciler tests.
    /// </summary>
    [Fact]
    public async Task Evaluate_HandlerCalledDirectlyWithScope_LoadsBothInputs()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var seed = await SeedAsync(world);

        await using var scope = await OpenScopeAsync(world, seed, TestContext.Current.CancellationToken);
        await using var services = world.Api.Services.CreateAsyncScope();
        var handler = services.ServiceProvider.GetRequiredService<EvidenceRevisionFrozenTaskHandler>();

        await handler.HandleAsync(
            new EvidenceRevisionFrozenPayload(seed.Org, seed.Project, seed.RunId, seed.RevisionId, 1),
            scope,
            TestContext.Current.CancellationToken);

        await scope.CommitAsync(TestContext.Current.CancellationToken);

        var stored = await StoredRowAsync(world, seed, EvaluationMode.Live);
        stored.ShouldNotBeNull();
        stored!.PolicyHash.ShouldBe(seed.PolicyHash);
        stored.ManifestHash.ShouldBe(seed.ManifestHash);
        stored.Decision.ShouldBe("PASS");
        stored.Mode.ShouldBe("live");
    }

    // ------------------------------------------------------------------ the histogram

    /// <summary>
    /// Exactly one latency measurement per committed evaluation: the action is registered on the scope and runs after
    /// its commit, so the failed attempt and the conflict-loser path record nothing.
    /// </summary>
    [Fact]
    public async Task Evaluate_CommittedEvaluation_RecordsOneLatencyMeasurement()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var seed = await SeedAsync(world);

        var measured = new List<double>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, subscribing) =>
            {
                if (instrument.Meter.Name == EvidgateTelemetry.MeterName
                    && instrument.Name == EvidgateTelemetry.EvaluationDecisionLatencyMetricName)
                {
                    subscribing.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) =>
        {
            lock (measured)
            {
                measured.Add(value);
            }
        });
        listener.Start();

        await EvaluateThenFailAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken);
        lock (measured)
        {
            measured.ShouldBeEmpty("a rolled-back attempt commits nothing, so nothing is measured");
        }

        await EvaluateAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken);
        lock (measured)
        {
            measured.Count.ShouldBe(1, "one committed evaluation, one measurement");
            measured[0].ShouldBeGreaterThanOrEqualTo(0);
        }

        await EvaluateAsync(world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken);
        lock (measured)
        {
            measured.Count.ShouldBe(1, "the duplicate reads the winner's row and measures nothing new");
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>What a seeded run holds: the row the evaluation names, its policy revision and its evidence revision.</summary>
    private sealed record Seed(
        Guid Org,
        Guid Project,
        Guid RunId,
        Guid RevisionId,
        Guid PolicyRevisionId,
        string PolicyHash,
        string ManifestHash);

    /// <summary>One stored evaluation's deciding columns.</summary>
    private sealed record StoredEvaluation(
        string PolicyHash,
        string ManifestHash,
        string Completeness,
        string Outcome,
        string Decision,
        string Mode);

    /// <summary>Runs one call on its own <see cref="ExecutionContext"/>, as a separate worker process would.</summary>
    /// <remarks>The token is read on this flow first: <see cref="TestContext.Current"/> is an <c>AsyncLocal</c>, so an
    /// inherited-late child would never observe the test's cancellation.</remarks>
    private static Task<T> OnOwnFlow<T>(Func<CancellationToken, Task<T>> work)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(() => work(cancellationToken), cancellationToken);
        }
    }

    /// <summary>Opens the tenant scope the handler runs in, as the app role.</summary>
    /// <remarks>Not <c>async</c>: the factory writes the ambient <c>AsyncLocal</c> synchronously, and a write inside an
    /// async method never reaches the caller, so <c>Require()</c> would throw
    /// <c>TenantScopeMissingException</c>.</remarks>
    private static Task<ITenantScope> OpenScopeAsync(
        RunCreationTests.World world,
        Seed seed,
        CancellationToken cancellationToken) =>
        world.Api.Services.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(seed.Org, cancellationToken: cancellationToken);

    /// <summary>One handler invocation on its own scope, committed.</summary>
    private static async Task<EvaluationRecord> EvaluateAsync(
        RunCreationTests.World world,
        Seed seed,
        EvaluationMode mode,
        CancellationToken cancellationToken)
    {
        await using var scope = await OpenScopeAsync(world, seed, cancellationToken);
        await using var services = world.Api.Services.CreateAsyncScope();
        var handler = services.ServiceProvider.GetRequiredService<EvaluateRunCommandHandler>();

        var result = await handler.Handle(new EvaluateRunCommand(seed.RunId, 1, mode), cancellationToken);

        await scope.CommitAsync(cancellationToken);
        result.IsSuccess.ShouldBeTrue("the evaluation handler raises instead of returning a failure");
        return result.Value;
    }

    /// <summary>
    /// One handler invocation whose attempt dies after the write and before the commit: the scope is disposed
    /// uncommitted, which is the rollback a crashed worker performs, and the record it computed is what the retry must
    /// reproduce.
    /// </summary>
    private static async Task<EvaluationRecord> EvaluateThenFailAsync(
        RunCreationTests.World world,
        Seed seed,
        EvaluationMode mode,
        CancellationToken cancellationToken)
    {
        var scope = await OpenScopeAsync(world, seed, cancellationToken);
        try
        {
            await using var services = world.Api.Services.CreateAsyncScope();
            var handler = services.ServiceProvider.GetRequiredService<EvaluateRunCommandHandler>();
            var result = await handler.Handle(new EvaluateRunCommand(seed.RunId, 1, mode), cancellationToken);
            result.IsSuccess.ShouldBeTrue();

            // The fault: nothing commits, so the disposal below rolls the row back.
            return result.Value;
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }

    private async Task<long> CountAsync(
        RunCreationTests.World world,
        Seed seed,
        string suffix = "")
    {
        _ = seed;
        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{CountSql} {suffix}";
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<StoredEvaluation?> StoredRowAsync(
        RunCreationTests.World world,
        Seed seed,
        EvaluationMode mode)
    {
        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select policy_hash, manifest_hash, completeness, outcome, decision, mode
              from public.evaluation
             where org_id = @org_id and run_id = @run_id and evidence_revision = 1 and mode = @mode
            """;
        command.Parameters.AddWithValue("org_id", seed.Org);
        command.Parameters.AddWithValue("run_id", seed.RunId);
        command.Parameters.AddWithValue("mode", EvaluationModeCode.Of(mode));

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            return null;
        }

        return new StoredEvaluation(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5));
    }

    /// <summary>
    /// Pins the run to a second policy revision, so the next live verdict differs from the stored one in the six-column
    /// key and only the live partial key can refuse it.
    /// </summary>
    private async Task RepinAsync(RunCreationTests.World world, Seed seed)
    {
        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            with next as (
                insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by)
                select @policy_id, @org_id, @project_id,
                       coalesce((select max(revision_no) from public.policy_revision
                                  where org_id = @org_id and project_id = @project_id), 0) + 1,
                       @hash, '{}', @created_by
                returning id)
            update public.run set policy_revision_id = (select id from next)
             where org_id = @org_id and id = @run_id
            """;
        command.Parameters.AddWithValue("policy_id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("org_id", seed.Org);
        command.Parameters.AddWithValue("project_id", seed.Project);
        command.Parameters.AddWithValue("hash", "sha256:" + new string('c', 64));
        command.Parameters.AddWithValue("created_by", Guid.CreateVersion7());
        command.Parameters.AddWithValue("run_id", seed.RunId);

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Seeds what only the owner may write, on top of the run the real creation path wrote.</summary>
    /// <remarks>The attempts come before the revision, as in the real path: a revision freezes evidence that already
    /// exists, and the results read is bounded by the revision's freeze stamp.</remarks>
    private async Task<Seed> SeedAsync(RunCreationTests.World world)
    {
        var (runId, manifestHash) = await CreateRunForSeedAsync(world);
        var (policyId, policyHash) = await PinPolicyAsync(world, runId);
        await PassEveryPlannedExecutionAsync(world, runId);
        var revisionId = await FreezeRevisionAsync(world, runId, manifestHash);
        return new Seed(world.Org, world.Project, runId, revisionId, policyId, policyHash, manifestHash);
    }

    /// <summary>POST /runs, then the manifest hash the evidence has to freeze against.</summary>
    private async Task<(Guid RunId, string ManifestHash)> CreateRunForSeedAsync(RunCreationTests.World world)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        await using var connection = await OwnerAsync(world);
        await using var read = connection.CreateCommand();
        read.CommandText =
            """
            select manifest_hash
              from public.manifest
             where org_id = @org_id and id = @manifest_id
            """;
        read.Parameters.AddWithValue("org_id", world.Org);
        read.Parameters.AddWithValue("manifest_id", world.ManifestId);
        await using var rows = await read.ExecuteReaderAsync(cancellationToken);
        (await rows.ReadAsync(cancellationToken)).ShouldBeTrue(
            "the world's manifest must be readable, or a verdict cannot name its evidence");
        return (created.Id("run_id"), rows.GetString(0));
    }

    /// <summary>Writes the policy revision the run pins, then pins it: one draft revision requiring nothing.</summary>
    private async Task<(Guid PolicyId, string PolicyHash)> PinPolicyAsync(
        RunCreationTests.World world, Guid runId)
    {
        var policyId = Guid.CreateVersion7();
        var policyHash = "sha256:" + new string('b', 64);
        await using var connection = await OwnerAsync(world);
        await using var policy = connection.CreateCommand();
        policy.CommandText =
            """
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by)
            values (@id, @org_id, @project_id, 1, @hash, '{}', @created_by);

            update public.run set policy_revision_id = @id where org_id = @org_id and id = @run_id;
            """;
        policy.Parameters.AddWithValue("id", policyId);
        policy.Parameters.AddWithValue("org_id", world.Org);
        policy.Parameters.AddWithValue("project_id", world.Project);
        policy.Parameters.AddWithValue("hash", policyHash);
        policy.Parameters.AddWithValue("created_by", Guid.CreateVersion7());
        policy.Parameters.AddWithValue("run_id", runId);
        await policy.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return (policyId, policyHash);
    }

    /// <summary>The run's first evidence revision: complete, nothing missing, every shard accounted for.</summary>
    private async Task<Guid> FreezeRevisionAsync(RunCreationTests.World world, Guid runId, string manifestHash)
    {
        var revisionId = Guid.CreateVersion7();
        await using var connection = await OwnerAsync(world);
        await using var revision = connection.CreateCommand();
        revision.CommandText =
            """
            insert into public.evidence_revision
              (id, org_id, run_id, revision_no, completeness, missing, outcome, event_watermark, manifest_hash)
            values (@id, @org_id, @run_id, 1, 'complete', '{}', 'all_passed', 0, @manifest_hash)
            """;
        revision.Parameters.AddWithValue("id", revisionId);
        revision.Parameters.AddWithValue("org_id", world.Org);
        revision.Parameters.AddWithValue("run_id", runId);
        revision.Parameters.AddWithValue("manifest_hash", manifestHash);
        await revision.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return revisionId;
    }

    /// <summary>One passing attempt per planned execution, checked against the plan the run wrote.</summary>
    private async Task PassEveryPlannedExecutionAsync(RunCreationTests.World world, Guid runId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OwnerAsync(world);
        await using var attempts = connection.CreateCommand();
        attempts.CommandText =
            """
            insert into public.case_attempt
              (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status)
            select gen_random_uuid(), pe.org_id, pe.run_id, pe.execution_key, tr.case_id, 1, 'passed', 'passed'
              from public.planned_execution pe
              join public.test_revision tr on tr.org_id = pe.org_id and tr.id = pe.case_revision_id
             where pe.org_id = @org_id and pe.run_id = @run_id
               and not exists (
                 select 1 from public.case_attempt a
                  where a.org_id = pe.org_id and a.run_id = pe.run_id
                    and a.execution_key = pe.execution_key)
            """;
        attempts.Parameters.AddWithValue("org_id", world.Org);
        attempts.Parameters.AddWithValue("run_id", runId);
        var inserted = await attempts.ExecuteNonQueryAsync(cancellationToken);

        await using var plan = connection.CreateCommand();
        plan.CommandText =
            "select count(*) from public.planned_execution where org_id = @org_id and run_id = @run_id";
        plan.Parameters.AddWithValue("org_id", world.Org);
        plan.Parameters.AddWithValue("run_id", runId);
        var planned = (long)(await plan.ExecuteScalarAsync(cancellationToken))!;

        (planned > 0).ShouldBeTrue("the run-creation path writes the plan the evidence is judged against");
        ((long)inserted).ShouldBe(planned, "a partial seed would freeze an incomplete verdict");
    }

    private async Task<NpgsqlConnection> OwnerAsync(RunCreationTests.World world)
    {
        var connection = new NpgsqlConnection(_fixture.OwnerConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }
}
