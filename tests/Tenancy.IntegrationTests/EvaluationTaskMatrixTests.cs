using System.Security.Cryptography;
using Evidgate.Evaluation;
using Evidgate.Modules.Policy.Application.Evaluation;
using Npgsql;
using NpgsqlTypes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The decision matrix against the stored evaluation: each row seeds a run, its attempts, the frozen revision and the
/// pinned policy body, runs the evaluation command, reloads the committed row and compares its columns to the matrix
/// values, so a stored verdict is proven and not only the in-memory function.
/// <para>
/// Rows M10–M12, M14, M15, M17 and M18 are absent because the layers they name have not landed: the evaluator is
/// registered with no decision layer and the reason catalogue carries none of <c>flaky_within_budget</c>,
/// <c>flaky_critical_retry_pass</c>, <c>quarantined_case_failed</c>, <c>waiver_approved</c>, <c>quarantine_expired</c>
/// or <c>shadow_not_evidence</c>, so a stored row for them could only be invented. M8 is absent because the stored
/// policy body cannot allow an empty suite: the reader pins <c>emptySuiteAllowed</c> to false, so a catalog-declared
/// empty suite never passes through this path and the row stays a property of the in-memory function; M7 seeds the same
/// branch from the other side instead, a manifest that planned nothing while its suite still declares cases, with the
/// revision frozen complete: an incomplete revision that names no code is read as <c>missing_result</c>, which would
/// replace the expected-count-zero reason the row is about.
/// </para>
/// </summary>
public sealed partial class EvaluationTaskTests
{
    /// <summary>
    /// The digest of the empty exception snapshot, pinned as a literal: no waiver and no quarantine entry active gives
    /// <c>sha256</c> of the RFC 8785 form <c>{"quarantine_ids":[],"waiver_ids":[]}</c>. Pinned so the stored column's
    /// wire format is a contract, not whatever the resolver currently computes.
    /// </summary>
    private const string EmptyExceptionSetHash =
        "sha256:79f9842e0f0a695e1da355253d752e89c5d5fa56f15a4a9eeff6ffbf0b964945";

    /// <summary>
    /// The digest of one approved waiver beside one active quarantine entry, pinned as a literal: both lists appear in
    /// canonical key order, ids in the <c>D</c> format, no whitespace.
    /// </summary>
    private const string PinnedExceptionSetHash =
        "sha256:270db3f4071325c536c7a10115ca398602e17227bdb3319b5a21631e919f3c8e";

    /// <summary>The waiver id behind <see cref="PinnedExceptionSetHash"/>.</summary>
    private static readonly Guid PinnedWaiverId = Guid.Parse("0190f0a0-0000-7000-8000-0000000000b2");

    /// <summary>The quarantine entry id behind <see cref="PinnedExceptionSetHash"/>.</summary>
    private static readonly Guid PinnedQuarantineId = Guid.Parse("0190f0a0-0000-7000-8000-0000000000a1");

    public static TheoryData<string> PersistedRows =>
        ["M1", "M2", "M3", "M4", "M5", "M6", "M7", "M9", "M13", "M16", "M19"];

    /// <summary>
    /// One matrix row end to end through the database: the command stores a row, the reload finds the same one, and
    /// every deciding column plus both content addresses equals the matrix value.
    /// </summary>
    [Theory]
    [MemberData(nameof(PersistedRows))]
    public async Task Matrix_Row_IsStored_AndReloadsAsTheContractValues(string name)
    {
        var row = MatrixRow(name);
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var seed = await SeedMatrixAsync(world, row);
        var cancellationToken = TestContext.Current.CancellationToken;

        var evaluated = await EvaluateAsync(world, seed, row.RequestedMode, cancellationToken);
        var stored = await ReloadAsync(world, seed, row.ExpectedMode);

        stored.Id.ShouldBe(evaluated.Id, "the command answers with the row the reload finds");
        stored.Completeness.ShouldBe(row.Completeness);
        stored.Missing.ShouldBe(row.Missing);
        stored.Outcome.ShouldBe(row.Outcome);
        stored.Decision.ShouldBe(row.Decision);
        stored.Reasons.ShouldBe(row.Reasons);
        stored.Mode.ShouldBe(row.ExpectedMode);
        stored.PolicyHash.ShouldBe(seed.PolicyHash, "the row names the policy revision the run is pinned to");
        stored.ManifestHash.ShouldBe(seed.ManifestHash, "and the manifest the evidence froze against");
        stored.EvaluatorVersion.ShouldBe(IPolicyEvaluator.EvaluatorVersion);
        stored.ExceptionsHash.ShouldBe(EmptyExceptionSetHash, "this run has no waiver or quarantine entry in force");
        stored.ExceptionsAsOf.ShouldBeGreaterThan(
            new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), "the row stores the instant its snapshot was read at");

        if (!row.Replay)
        {
            return;
        }

        // The replay row is advice only: the live slot stays empty and a redelivered evaluation answers the same row.
        (await StoredRowAsync(world, seed, EvaluationMode.Live)).ShouldBeNull("a replay never writes the live row");
        (await EvaluateAsync(world, seed, EvaluationMode.Live, cancellationToken)).Id.ShouldBe(
            stored.Id, "the second call lands on the same key and reads the stored row back");
        (await CountAsync(world, seed)).ShouldBe(1, "one evidence revision, one replay verdict");
    }

    /// <summary>
    /// The stored-row replay: a committed evaluation is reloaded, the same durable inputs and the same empty snapshot
    /// are recomputed, and the recomputation's columns are byte-identical to the stored ones — the row is a function of
    /// its inputs, not of the attempt that wrote it.
    /// </summary>
    [Fact]
    public async Task StoredRow_RecomputedFromTheSameInputs_IsByteIdentical()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var seed = await SeedMatrixAsync(world, MatrixRow("M2"));
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await EvaluateAsync(world, seed, EvaluationMode.Live, cancellationToken);
        var stored = await ReloadAsync(world, seed, "live");
        DecidingOf(first).ShouldBe(stored.Deciding, "the command's answer and the reloaded row agree");

        // The recompute runs in the slot the stored row does not cover, so it recomputes instead of reading the winner
        // back, and its rollback leaves the stored row as the only evidence.
        var recomputed = await EvaluateThenFailAsync(world, seed, EvaluationMode.Shadow, cancellationToken);
        (await CountAsync(world, seed)).ShouldBe(1, "the recompute left no row behind");
        DecidingOf(recomputed).ShouldBe(stored.Deciding, "a reload and recompute must reproduce every stored column");

        // Persisted again in the other slot, the identical inputs store the identical deciding columns.
        await EvaluateAsync(world, seed, EvaluationMode.Shadow, cancellationToken);
        (await ReloadAsync(world, seed, "shadow")).Deciding.ShouldBe(stored.Deciding);
    }

    /// <summary>
    /// The stored <c>exceptions_hash</c> is the pinned canonical digest of the active set, and a different set gives a
    /// different one: the column is a content address of the ids, not a timestamp or a count.
    /// </summary>
    [Fact]
    public async Task StoredExceptionsHash_IsThePinnedCanonicalDigestOfTheActiveSet()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var seed = await SeedMatrixAsync(world, MatrixRow("M1"));
        await InsertPinnedExceptionsAsync(world, seed);

        var evaluated = await EvaluateAsync(
            world, seed, EvaluationMode.Live, TestContext.Current.CancellationToken);

        evaluated.ExceptionsHash.ShouldBe(PinnedExceptionSetHash);
        var stored = await ReloadAsync(world, seed, "live");
        stored.ExceptionsHash.ShouldBe(PinnedExceptionSetHash);
        stored.ExceptionsHash.ShouldNotBe(EmptyExceptionSetHash, "an active waiver changes the set, so it changes the hash");
        stored.Decision.ShouldBe("PASS", "a waiver is displayed, never a reason to change an otherwise passing verdict");
    }

    /// <summary>One matrix row: the evidence and policy it seeds, and the columns it must store.</summary>
    /// <param name="Body">The pinned policy body, built from the run's own case ids when a row needs one.</param>
    /// <param name="RevisionCompleteness">The frozen revision's own <c>completeness</c> token.</param>
    /// <param name="RevisionMissing">The frozen revision's own <c>missing[]</c> codes.</param>
    /// <param name="FirstCaseStatus">The status of the first case's attempts; every other attempt passes.</param>
    /// <param name="NoAttempts">Seeds a revision whose planned executions recorded no attempt at all.</param>
    /// <param name="ZeroPlan">Seeds a run whose manifest planned no execution.</param>
    /// <param name="Replay">Marks the run a replay, which may only ever store a shadow row.</param>
    /// <param name="RequestedMode">The slot the caller asks for.</param>
    /// <param name="ExpectedMode">The slot the row must actually hold.</param>
    /// <param name="Completeness">Expected stored <c>completeness</c>.</param>
    /// <param name="Missing">Expected stored <c>missing[]</c>, in order.</param>
    /// <param name="Outcome">Expected stored <c>outcome</c>.</param>
    /// <param name="Decision">Expected stored <c>decision</c>.</param>
    /// <param name="Reasons">Expected stored <c>reasons[]</c>, in order.</param>
    private sealed record Matrix(
        Func<Cases, string> Body,
        string RevisionCompleteness,
        string[] RevisionMissing,
        string FirstCaseStatus,
        bool NoAttempts,
        bool ZeroPlan,
        bool Replay,
        EvaluationMode RequestedMode,
        string ExpectedMode,
        string Completeness,
        string[] Missing,
        string Outcome,
        string Decision,
        string[] Reasons);

    /// <summary>The run's first planned case, by name: the case a row makes fail, skip or block.</summary>
    private sealed record Cases(Guid FirstId, string FirstName);

    private static Matrix Row(
        string completeness,
        string[] missing,
        string outcome,
        string decision,
        string[] reasons,
        Func<Cases, string>? body = null,
        string firstCaseStatus = "passed",
        bool noAttempts = false,
        bool zeroPlan = false,
        bool replay = false,
        string? revisionCompleteness = null) => new(
            body ?? (_ => "{}"),
            revisionCompleteness ?? completeness,
            missing,
            firstCaseStatus,
            noAttempts,
            zeroPlan,
            replay,
            EvaluationMode.Live,
            replay ? "shadow" : "live",
            completeness,
            missing,
            outcome,
            decision,
            reasons);

    private static Matrix MatrixRow(string name) => name switch
    {
        "M1" => Row("complete", [], "all_passed", "PASS", ["scope_build_policy_pinned"]),
        "M2" => Row(
            "incomplete", ["missing_shard"], "has_failures", "FAIL", ["required_assertion_failed"],
            firstCaseStatus: "failed"),
        "M3" => Row("incomplete", ["missing_shard"], "all_passed", "INCONCLUSIVE", ["missing_shard"]),
        "M4" => Row("incomplete", ["build_mismatch"], "all_passed", "INCONCLUSIVE", ["build_mismatch"]),
        "M5" => Row("incomplete", ["unknown_status"], "all_passed", "INCONCLUSIVE", ["unknown_status"]),
        "M6" => Row(
            "incomplete", ["setup_error"], "has_failures", "INCONCLUSIVE", ["setup_error"],
            firstCaseStatus: "blocked"),
        "M7" => Row(
            "incomplete", [], "no_results", "INCONCLUSIVE", ["expected_count_zero"],
            zeroPlan: true, revisionCompleteness: "complete"),
        "M9" => Row(
            "complete", [], "all_passed", "INCONCLUSIVE", ["critical_not_executed"],
            body: cases => $$"""{"critical_cases":["{{cases.FirstId}}"]}""",
            firstCaseStatus: "skipped"),
        "M13" => Row("incomplete", ["artifact_missing"], "all_passed", "INCONCLUSIVE", ["artifact_missing"]),
        "M16" => Row("incomplete", ["stale_epoch"], "no_results", "INCONCLUSIVE", ["stale_epoch_only"], noAttempts: true),
        "M19" => Row("complete", [], "all_passed", "PASS", ["scope_build_policy_pinned"], replay: true),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a matrix row this harness stores"),
    };

    /// <summary>
    /// Writes the row's world in the order the real path uses: the run and its plan, then the pinned policy, then the
    /// attempts, then the revision that froze over them. A row that needs a zero plan drops the plan before the freeze.
    /// </summary>
    private async Task<Seed> SeedMatrixAsync(RunCreationTests.World world, Matrix matrix)
    {
        var (runId, manifestHash) = await CreateRunForSeedAsync(world);
        var cases = await FirstPlannedCaseAsync(world, runId);
        var (policyId, policyHash) = await PinBodyAsync(world, runId, matrix.Body(cases));

        if (matrix.ZeroPlan)
        {
            await DropPlanAsync(world, runId);
        }
        else if (!matrix.NoAttempts)
        {
            await InsertAttemptsAsync(world, runId, cases.FirstName, matrix.FirstCaseStatus);
        }

        var revisionId = await FreezeAsync(
            world, runId, manifestHash, matrix.RevisionCompleteness, matrix.RevisionMissing);
        var seed = new Seed(world.Org, world.Project, runId, revisionId, policyId, policyHash, manifestHash);

        if (matrix.Replay)
        {
            await MarkReplayAsync(world, seed);
        }

        return seed;
    }

    /// <summary>The first required planned execution's case, which every row's special status is applied to.</summary>
    private async Task<Cases> FirstPlannedCaseAsync(RunCreationTests.World world, Guid runId)
    {
        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select tc.id, tc.name
              from public.planned_execution pe
              join public.test_revision tr on tr.org_id = pe.org_id and tr.id = pe.case_revision_id
              join public.test_case tc on tc.org_id = tr.org_id and tc.id = tr.case_id
             where pe.org_id = @org_id and pe.run_id = @run_id and pe.required
             order by tc.name, tc.id
             limit 1
            """;
        command.Parameters.AddWithValue("org_id", world.Org);
        command.Parameters.AddWithValue("run_id", runId);

        await using var rows = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await rows.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue(
            "the run's plan must hold a required execution, or a confirmed failure could not gate the decision");
        return new Cases(rows.GetGuid(0), rows.GetString(1));
    }

    /// <summary>Pins the row's policy body to the run as its first revision and answers its id and content address.</summary>
    private async Task<(Guid Id, string Hash)> PinBodyAsync(
        RunCreationTests.World world, Guid runId, string body)
    {
        var policyId = Guid.CreateVersion7();
        var policyHash = "sha256:" + new string('b', 64);
        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by)
            values (@id, @org_id, @project_id, 1, @hash, @body::jsonb, @created_by);

            update public.run set policy_revision_id = @id where org_id = @org_id and id = @run_id;
            """;
        command.Parameters.AddWithValue("id", policyId);
        command.Parameters.AddWithValue("org_id", world.Org);
        command.Parameters.AddWithValue("project_id", world.Project);
        command.Parameters.AddWithValue("hash", policyHash);
        command.Parameters.AddWithValue("body", body);
        command.Parameters.AddWithValue("created_by", Guid.CreateVersion7());
        command.Parameters.AddWithValue("run_id", runId);
        (await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).ShouldBeGreaterThan(0);
        return (policyId, policyHash);
    }

    /// <summary>One attempt per planned execution, all passing except the first case's, which carries the row's status.</summary>
    private async Task InsertAttemptsAsync(
        RunCreationTests.World world, Guid runId, string firstCaseName, string firstCaseStatus)
    {
        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into public.case_attempt
              (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status)
            select gen_random_uuid(), pe.org_id, pe.run_id, pe.execution_key, tr.case_id, 1,
                   case when tc.name = @first_case then @status else 'passed' end,
                   case when tc.name = @first_case then @status else 'passed' end
              from public.planned_execution pe
              join public.test_revision tr on tr.org_id = pe.org_id and tr.id = pe.case_revision_id
              join public.test_case tc on tc.org_id = tr.org_id and tc.id = tr.case_id
             where pe.org_id = @org_id and pe.run_id = @run_id
            """;
        command.Parameters.AddWithValue("org_id", world.Org);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("first_case", firstCaseName);
        command.Parameters.AddWithValue("status", firstCaseStatus);

        var inserted = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        (inserted > 0).ShouldBeTrue("the run-creation path writes the plan the evidence is judged against");
    }

    /// <summary>
    /// A run whose manifest planned no execution at all: the plan rows and their attempts go, and the run is locked to a
    /// second manifest of the same project and suite that declares zero executions. A manifest is append-only (a BEFORE
    /// UPDATE/DELETE trigger refuses every role), so the seed inserts the zero-count row and repoints the run instead of
    /// editing the original.
    /// </summary>
    private async Task DropPlanAsync(RunCreationTests.World world, Guid runId)
    {
        var zeroManifestId = Guid.CreateVersion7();
        var zeroManifestHash = "sha256:" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            delete from public.case_attempt where org_id = @org_id and run_id = @run_id;
            delete from public.planned_execution where org_id = @org_id and run_id = @run_id;

            insert into public.manifest
              (id, org_id, project_id, manifest_hash, schema_version, suite_revision_id, environment_revision_id,
               shard_plan, matrix, env_needs, planned_count, document, verified_by)
            select @zero_manifest, org_id, project_id, @zero_hash, schema_version, suite_revision_id,
                   environment_revision_id, shard_plan, matrix, env_needs, 0, document, verified_by
              from public.manifest
             where org_id = @org_id
               and id = (select manifest_id from public.run where org_id = @org_id and id = @run_id);

            update public.run
               set manifest_id = @zero_manifest
             where org_id = @org_id
               and id = @run_id;
            """;
        command.Parameters.AddWithValue("org_id", world.Org);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("zero_manifest", zeroManifestId);
        command.Parameters.AddWithValue("zero_hash", zeroManifestHash);
        (await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).ShouldBeGreaterThan(0);
    }

    /// <summary>The revision that froze over the row's attempts, with the completeness the row's evidence carries.</summary>
    private async Task<Guid> FreezeAsync(
        RunCreationTests.World world,
        Guid runId,
        string manifestHash,
        string completeness,
        string[] missing)
    {
        var revisionId = Guid.CreateVersion7();
        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into public.evidence_revision
              (id, org_id, run_id, revision_no, completeness, missing, outcome, event_watermark, manifest_hash)
            values (@id, @org_id, @run_id, 1, @completeness, @missing, null, 0, @manifest_hash)
            """;
        command.Parameters.AddWithValue("id", revisionId);
        command.Parameters.AddWithValue("org_id", world.Org);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("completeness", completeness);
        command.Parameters.Add(new NpgsqlParameter("missing", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = missing,
        });
        command.Parameters.AddWithValue("manifest_hash", manifestHash);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return revisionId;
    }

    /// <summary>One approved waiver and one active quarantine entry, with the ids the pinned digest names.</summary>
    private async Task InsertPinnedExceptionsAsync(RunCreationTests.World world, Seed seed)
    {
        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into public.waiver
              (id, org_id, project_id, scope, target_id, reason, requested_by, approved_by, state, expires_at)
            values (@waiver, @org_id, @project_id, 'case', @target, 'known issue', @requester, @approver,
                    'approved', now() + interval '1 hour');

            insert into public.quarantine_entry
              (id, org_id, project_id, case_id, owner, reason, state, expires_at)
            select @quarantine, @org_id, @project_id, c.id, m.id, 'flaky', 'active', now() + interval '1 hour'
              from (select id from public.test_case where org_id = @org_id order by name limit 1) c,
                   (select id from public.membership where org_id = @org_id limit 1) m
            """;
        command.Parameters.AddWithValue("waiver", PinnedWaiverId);
        command.Parameters.AddWithValue("quarantine", PinnedQuarantineId);
        command.Parameters.AddWithValue("org_id", seed.Org);
        command.Parameters.AddWithValue("project_id", seed.Project);
        command.Parameters.AddWithValue("target", Guid.CreateVersion7());
        command.Parameters.AddWithValue("requester", Guid.CreateVersion7());
        command.Parameters.AddWithValue("approver", Guid.CreateVersion7());

        (await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).ShouldBe(
            2, "the snapshot must see one waiver and one quarantine entry in force");
    }

    /// <summary>The committed row of one slot, reloaded rather than taken from the command's answer.</summary>
    private async Task<StoredRow> ReloadAsync(RunCreationTests.World world, Seed seed, string mode)
    {
        await using var connection = await OwnerAsync(world);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, completeness, missing, outcome, decision, reasons, policy_hash, manifest_hash,
                   evaluator_version, mode, exceptions_hash, exceptions_as_of
              from public.evaluation
             where org_id = @org_id and run_id = @run_id and evidence_revision = 1 and mode = @mode
            """;
        command.Parameters.AddWithValue("org_id", seed.Org);
        command.Parameters.AddWithValue("run_id", seed.RunId);
        command.Parameters.AddWithValue("mode", mode);

        await using var rows = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await rows.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue(
            $"the evaluation of run {seed.RunId:D} must have stored its {mode} row");

        return new StoredRow(
            rows.GetGuid(0),
            rows.GetString(1),
            await rows.GetFieldValueAsync<string[]>(2, TestContext.Current.CancellationToken),
            rows.GetString(3),
            rows.GetString(4),
            await rows.GetFieldValueAsync<string[]>(5, TestContext.Current.CancellationToken),
            rows.GetString(6),
            rows.GetString(7),
            rows.GetString(8),
            rows.GetString(9),
            rows.GetString(10),
            rows.GetDateTime(11));
    }
}
