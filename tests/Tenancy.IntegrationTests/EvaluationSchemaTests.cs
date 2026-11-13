// Migration 0059 through a real PostgreSQL: the stored evaluation's two idempotency keys, the immutability
// of a row that is evidence rather than state, the closed token sets, the composite tenant keys and grants.
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 0059 raises <c>evaluation</c> to the persisted verdict of a run: one row per
/// <c>(org_id, run_id, evidence_revision, policy_revision_id, evaluator_version, mode)</c>, at most one
/// <c>live</c> row per frozen evidence revision, and no UPDATE or DELETE path for any role. Every write runs
/// as <c>evidgate_app</c> in a tenant-scoped transaction unless a case speaks as the owner on purpose.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class EvaluationSchemaTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) : IClassFixture<TenancyHostPool>
{
    /// <summary>The evaluator's version token, stored verbatim.</summary>
    private const string EvaluatorVersion = "1.0.0";

    /// <summary>A well-formed manifest hash, well inside the 1..200 bound.</summary>
    private static readonly string ManifestHash = "sha256:" + new string('e', 64);

    private readonly PostgresTenancyFixture _fixture = fixture;
    // Reuse one API host per class; each world still owns its tenant data. Dedicated databases and overlapping worlds keep private hosts.
    private readonly TenancyHostPool _hosts = hosts;

    // ------------------------------------------------------------------ (a) the migration

    /// <summary>AC1/AC2 storage: the table exists with ENABLE + FORCE row level security and the tenant policy.</summary>
    [Fact]
    public async Task Evaluation_Migration_AppliesTheTable_WithForcedRls()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        (await _fixture.TableExistsAsync(world.Database, "evaluation")).ShouldBeTrue();
        (await _fixture.RowSecurityAsync(world.Database, "evaluation"))
            .ShouldBe((true, true), "DECISIONS F5: ENABLE + FORCE row level security");
        (await _fixture.PoliciesAsync(world.Database, "evaluation"))
            .ShouldContain(policy => policy.StartsWith("evaluation_app_tenant:evidgate_app:", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ (b) immutability

    /// <summary>
    /// A stored evaluation is never rewritten or removed: <c>evidgate_app</c> holds SELECT + INSERT only, not
    /// even column-level UPDATE, so the refusal is the ACL's (42501) before any trigger sees the row. The
    /// operator keeps read only and the scheduler holds nothing.
    /// </summary>
    [Fact]
    public async Task Evaluation_AppRole_IsSelectAndInsertOnly()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var seed = await SeedAsync(world);

        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", "evaluation", "select")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", "evaluation", "insert")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", "evaluation", "update"))
            .ShouldBeFalse("a stored evaluation has no update path");
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", "evaluation", "delete"))
            .ShouldBeFalse("a stored evaluation has no delete path");

        // No column grant hides behind the table-level refusal.
        (await _fixture.ScalarAsync<long>(
            world.Database,
            """
            select count(*)
              from information_schema.columns
             where table_schema = 'public' and table_name = 'evaluation'
               and has_column_privilege('evidgate_app', 'public.evaluation', column_name, 'UPDATE')
            """))
            .ShouldBe(0, "the app role must not be able to move any column of a stored evaluation");

        (await CaptureAppErrorAsync(world, world.Org, "update public.evaluation set decision = 'FAIL'"))
            .SqlState.ShouldBe("42501");
        (await CaptureAppErrorAsync(world, world.Org, "delete from public.evaluation")).SqlState.ShouldBe("42501");

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.evaluation where id = '{seed.Id:D}'"))
            .ShouldBe(1, "the refused statements leave the row in place");

        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_operator", "evaluation", "select")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_operator", "evaluation", "insert")).ShouldBeFalse();
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_worker_sched", "evaluation", "select")).ShouldBeFalse();
    }

    /// <summary>
    /// The trigger is the backstop the grants cannot be: the migration role reaches the row, and both an
    /// UPDATE (whatever column) and a DELETE are refused 23514 — a verdict that could be edited is not a
    /// verdict.
    /// </summary>
    [Fact]
    public async Task Evaluation_OwnerRole_TriggerRefusesUpdateAndDelete()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var seed = await SeedAsync(world);

        // One transaction per refused statement: once the UPDATE is refused its transaction is aborted (25P02), so
        // the DELETE needs its own to reach the trigger instead of reading the first refusal's aftermath.
        var rewritten = await CaptureOwnerErrorAsync(
            world,
            "update public.evaluation set reasons = array['setup_error']::text[] where id = @id",
            ("id", seed.Id));
        rewritten.SqlState.ShouldBe("23514");
        rewritten.MessageText.ShouldContain("never rewritten");

        var removed = await CaptureOwnerErrorAsync(
            world,
            "delete from public.evaluation where id = @id",
            ("id", seed.Id));
        removed.SqlState.ShouldBe("23514");
        removed.MessageText.ShouldContain("never deleted");

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.evaluation where id = '{seed.Id:D}' and decision = 'PASS'"))
            .ShouldBe(1, "the row is unchanged and still present");
    }

    // ------------------------------------------------------------------ (c) the idempotency keys

    /// <summary>
    /// The wider unique key is the arbiter: the same six columns cannot be stored twice. A live duplicate trips both
    /// keys, so the six-column key is pinned on a shadow duplicate where the partial index cannot apply; another
    /// policy revision is a new evaluation in the shadow slot.
    /// </summary>
    [Fact]
    public async Task Evaluation_ResultKey_RefusesTheSameVerdictTwice()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CreateRunAsync(world);
        var first = await SeedAsync(world, runId: runId);

        // A duplicate of the live row trips both keys, and the server names the one it checks first, so the refusal
        // is pinned here rather than the index order. The six-column key is pinned exactly on the shadow duplicate.
        var duplicate = await CaptureInsertErrorAsync(world, world.Org, first with { Id = Guid.CreateVersion7() });
        duplicate.SqlState.ShouldBe("23505");
        duplicate.ConstraintName.ShouldBeOneOf("evaluation_result_key", "evaluation_live_key");

        // A shadow verdict beside the live one is legal, and its duplicate is refused by the six-column key
        // alone: the partial index does not cover `mode = 'shadow'`.
        var shadow = first with { Id = Guid.CreateVersion7(), Mode = "shadow" };
        (await InsertAsync(world, world.Org, shadow))
            .ShouldBe(1, "a shadow verdict is stored beside the live one");

        var shadowDuplicate =
            await CaptureInsertErrorAsync(world, world.Org, shadow with { Id = Guid.CreateVersion7() });
        shadowDuplicate.SqlState.ShouldBe("23505");
        shadowDuplicate.ConstraintName.ShouldBe("evaluation_result_key");

        // Another policy revision over the same evidence is a new evaluation once it is the shadow opinion; a
        // second `live` row for one evidence revision is what the partial index forbids (the next case proves it).
        var otherPolicy = await SeedPolicyRevisionAsync(world, world.Org, world.Project, 'c');
        (await InsertAsync(
            world,
            world.Org,
            first with
            {
                Id = Guid.CreateVersion7(),
                PolicyRevisionId = otherPolicy.Id,
                PolicyHash = otherPolicy.Hash,
                Mode = "shadow",
            })).ShouldBe(1);

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.evaluation where org_id = '{world.Org}' and run_id = '{runId:D}'"))
            .ShouldBe(3, "one live row, one shadow row, and a shadow row under another policy revision");
    }

    /// <summary>
    /// Two <c>live</c> rows for one evidence revision are refused by the partial unique index however they
    /// differ elsewhere, while the shadow row for the same pair is accepted.
    /// </summary>
    [Fact]
    public async Task Evaluation_LiveIndex_AllowsOneLiveRowPerEvidenceRevision()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CreateRunAsync(world);
        var first = await SeedAsync(world, runId: runId);

        var otherPolicy = await SeedPolicyRevisionAsync(world, world.Org, world.Project, 'd');
        var secondLive = await CaptureInsertErrorAsync(
            world,
            world.Org,
            first with
            {
                Id = Guid.CreateVersion7(),
                PolicyRevisionId = otherPolicy.Id,
                PolicyHash = otherPolicy.Hash,
                EvaluatorVersion = "2.0.0",
            });
        secondLive.SqlState.ShouldBe("23505");
        secondLive.ConstraintName.ShouldBe("evaluation_live_key");

        (await InsertAsync(world, world.Org, first with { Id = Guid.CreateVersion7(), Mode = "shadow" }))
            .ShouldBe(1, "a shadow verdict for the same pair is legal and never collides with the live row");

        // A different evidence revision of the same run owns its own live slot.
        (await InsertAsync(world, world.Org, first with { Id = Guid.CreateVersion7(), EvidenceRevision = 2 }))
            .ShouldBe(1);
    }

    /// <summary>
    /// Two concurrent inserts of the same verdict leave exactly one row and one 23505, which is what lets
    /// the handler re-read the winner instead of reporting a failure.
    /// </summary>
    [Fact]
    public async Task Evaluation_ConcurrentSameVerdict_StoresOneRow()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CreateRunAsync(world);

        // The verdict is not stored before the race: the two racers must be the first writers of this key, or the
        // seeded row would hold the live slot and make both of them losers.
        var verdict = await BuildSpecAsync(world, runId);

        var outcomes = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(_ => InsertOrErrorAsync(
                world,
                world.Org,
                verdict with { Id = Guid.CreateVersion7() })));

        outcomes.Single(outcome => outcome.Error is null).Affected.ShouldBe(1, "one racer wins and stores its row");

        var loser = outcomes.Single(outcome => outcome.Error is not null).Error!;
        loser.SqlState.ShouldBe("23505", "the loser is refused by a unique key, not by a timeout or a deadlock");
        // Both keys cover an identical live row; the server names the one it checks first, so the refusal is
        // what the race is pinned to, not the index order.
        loser.ConstraintName.ShouldBeOneOf("evaluation_result_key", "evaluation_live_key");

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.evaluation where org_id = '{world.Org}' and run_id = '{runId:D}'"))
            .ShouldBe(1);
    }

    // ------------------------------------------------------------------ (d) the closed token sets

    /// <summary>
    /// Every closed column refuses a token outside the evaluator's vocabulary and accepts the ones it writes. A
    /// case is kept off the migration's two cross-column invariants, so its refusal names the token constraint and
    /// not the invariant that token happens to break as well.
    /// </summary>
    [Fact]
    public async Task Evaluation_ClosedTokenSets_AreEnforced()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CreateRunAsync(world);
        var spec = await SeedAsync(world, runId: runId);

        var refusals = new (string Column, SpecChange Change, string Constraint)[]
        {
            ("completeness", s => s with { Completeness = "partial", Decision = "INCONCLUSIVE" },
                "evaluation_completeness_check"),
            ("outcome", s => s with { Outcome = "unknown" }, "evaluation_outcome_check"),
            ("decision", s => s with { Decision = "pass" }, "evaluation_decision_check"),
            ("mode", s => s with { Mode = "advisory" }, "evaluation_mode_check"),
            ("policy_hash", s => s with { PolicyHash = "sha256:short" }, "evaluation_policy_hash_check"),
            ("exceptions_hash", s => s with { ExceptionsHash = "deadbeef" }, "evaluation_exceptions_hash_check"),
            ("manifest_hash", s => s with { ManifestHash = "" }, "evaluation_manifest_hash_check"),
            ("evaluator_version", s => s with { EvaluatorVersion = "" }, "evaluation_evaluator_version_check"),
            ("evidence_revision", s => s with { EvidenceRevision = 0 }, "evaluation_evidence_revision_check"),
            ("missing",
                s => s with { Completeness = "incomplete", Missing = ["not_a_reason"], Decision = "INCONCLUSIVE" },
                "evaluation_missing_check"),
            ("reasons", s => s with { Reasons = ["not_a_reason"] }, "evaluation_reasons_check"),
        };

        foreach (var (column, change, constraint) in refusals)
        {
            var error = await CaptureInsertErrorAsync(
                world,
                world.Org,
                change(spec) with { Id = Guid.CreateVersion7() });
            error.SqlState.ShouldBe("23514", column);
            error.ConstraintName.ShouldBe(constraint, column);
        }

        // The positive control: every accepted token stores, so the refusals above are the token and not the row.
        (await InsertAsync(world, world.Org, spec with
        {
            Id = Guid.CreateVersion7(),
            EvidenceRevision = 2,
            Completeness = "incomplete",
            Missing = ["setup_error", "missing_result"],
            Outcome = "no_results",
            Decision = "INCONCLUSIVE",
            Reasons = ["setup_error", "critical_not_executed", "flaky_budget_exceeded"],
            Mode = "shadow",
        })).ShouldBe(1);
    }

    // ------------------------------------------------------------------ (e) the verdict's consistency

    /// <summary>
    /// A <c>complete</c> row that names a missing reason code is refused 23514 by the table itself, so a handler
    /// that under-covered the evidence cannot store a complete verdict. The row is otherwise well formed: the
    /// decision is not <c>PASS</c> and the code is in the closed set, so the refusal is this invariant.
    /// </summary>
    [Fact]
    public async Task Evaluation_CompleteWithMissing_OwnerInsertIsRefused()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var spec = await BuildSpecAsync(world);

        var error = await CaptureOwnerErrorAsync(
            world,
            InsertSql,
            Parameters(spec with
            {
                Missing = ["missing_shard"],
                Outcome = "no_results",
                Decision = "INCONCLUSIVE",
                Reasons = ["missing_shard"],
            }));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("evaluation_complete_has_no_missing_check");
    }

    /// <summary>
    /// <c>decision = PASS</c> over an <c>incomplete</c> verdict with an empty <c>missing</c> is refused 23514: a
    /// pass claims the evaluator saw everything, and the empty gap list keeps the completeness invariant out of
    /// the way, so this refusal is the pass invariant alone.
    /// </summary>
    [Fact]
    public async Task Evaluation_PassOverIncomplete_OwnerInsertIsRefused()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var spec = await BuildSpecAsync(world);

        var error = await CaptureOwnerErrorAsync(
            world,
            InsertSql,
            Parameters(spec with { Completeness = "incomplete" }));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("evaluation_pass_requires_complete_check");
    }

    // ------------------------------------------------------------------ (f) tenant isolation

    /// <summary>
    /// FORCE RLS plus the composite tenant keys: another org cannot read the row, cannot write a row that
    /// claims this org (the policy's WITH CHECK answers 42501), and cannot point its own row at this org's
    /// run or policy revision (the composite FKs answer 23503).
    /// </summary>
    [Fact]
    public async Task Evaluation_CrossTenant_IsInvisibleAndRefused()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CreateRunAsync(world);
        var seed = await SeedAsync(world, runId: runId);

        var otherOrg = await CreateSecondTenantAsync(world, Unique.Slug("beta"));
        var foreignRun = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, otherOrg);

        (await ScalarAsOrgAsync<long>(world, otherOrg, "select count(*) from public.evaluation")).ShouldBe(0);

        // A row that claims this org while written under the other org's scope: the policy refuses it.
        (await CaptureInsertErrorAsync(
            world,
            otherOrg,
            seed with { Id = Guid.CreateVersion7(), RowOrg = world.Org }))
            .SqlState.ShouldBe("42501");

        // A row of the other org naming this org's run: the composite run key is the structural backstop.
        var foreignRunRef = await CaptureInsertErrorAsync(
            world,
            otherOrg,
            seed with { Id = Guid.CreateVersion7(), RowOrg = otherOrg, RunId = runId });
        foreignRunRef.SqlState.ShouldBe("23503");
        foreignRunRef.ConstraintName.ShouldBe("evaluation_run_fk");

        // Its own run, but this org's policy revision: the same backstop on the second key.
        var foreignPolicyRef = await CaptureInsertErrorAsync(
            world,
            otherOrg,
            seed with { Id = Guid.CreateVersion7(), RowOrg = otherOrg, RunId = foreignRun });
        foreignPolicyRef.SqlState.ShouldBe("23503");
        foreignPolicyRef.ConstraintName.ShouldBe("evaluation_policy_revision_fk");

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.evaluation where org_id = '{world.Org:D}'"))
            .ShouldBe(1, "this org's seeded row is the only one the refused writes could have added to");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>One row of a seeded policy revision: the id its evaluations point at and the hash they copy.</summary>
    private sealed record PolicyRow(Guid Id, string Hash);

    /// <summary>One stored evaluation's insert, so a case can vary exactly one value.</summary>
    private sealed record EvaluationSpec(
        Guid Id,
        Guid RowOrg,
        Guid RunId,
        int EvidenceRevision,
        Guid PolicyRevisionId,
        string PolicyHash,
        string ManifestHash,
        string Completeness,
        string[] Missing,
        string Outcome,
        string Decision,
        string[] Reasons,
        string EvaluatorVersion,
        string Mode,
        string ExceptionsHash);

    private delegate EvaluationSpec SpecChange(EvaluationSpec spec);

    /// <summary>Inserts one policy revision as the app role and returns its id and content address.</summary>
    private async Task<PolicyRow> SeedPolicyRevisionAsync(
        RunCreationTests.World world,
        Guid org,
        Guid project,
        char fill)
    {
        var id = Guid.CreateVersion7();
        var hash = "sha256:" + new string(fill, 64);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);
        (await scope.ExecuteAsync(
            """
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by)
            select @id, @org_id, @project_id, coalesce(max(revision_no), 0) + 1, @hash, '{}', @created_by
              from public.policy_revision where org_id = @org_id and project_id = @project_id
            """,
            ("id", id),
            ("org_id", org),
            ("project_id", project),
            ("hash", hash),
            ("created_by", Guid.CreateVersion7()))).ShouldBe(1);
        await scope.CommitAsync();

        return new PolicyRow(id, hash);
    }

    /// <summary>Seeds one run (unless given), one policy revision and one live evaluation for this org.</summary>
    private async Task<EvaluationSpec> SeedAsync(RunCreationTests.World world, Guid? runId = null)
    {
        var spec = await BuildSpecAsync(world, runId);

        (await InsertAsync(world, world.Org, spec)).ShouldBe(1);

        return spec;
    }

    /// <summary>Builds the row a case would store — one run and one policy revision — without storing it.</summary>
    private async Task<EvaluationSpec> BuildSpecAsync(RunCreationTests.World world, Guid? runId = null)
    {
        var run = runId ?? await CreateRunAsync(world);
        var policy = await SeedPolicyRevisionAsync(world, world.Org, world.Project, 'b');
        return new EvaluationSpec(
            Id: Guid.CreateVersion7(),
            RowOrg: world.Org,
            RunId: run,
            EvidenceRevision: 1,
            PolicyRevisionId: policy.Id,
            PolicyHash: policy.Hash,
            ManifestHash: ManifestHash,
            Completeness: "complete",
            Missing: [],
            Outcome: "all_passed",
            Decision: "PASS",
            Reasons: [],
            EvaluatorVersion: EvaluatorVersion,
            Mode: "live",
            ExceptionsHash: "sha256:" + new string('f', 64));
    }

    private static async Task<Guid> CreateRunAsync(RunCreationTests.World world)
    {
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));

        return created.Id("run_id");
    }

    /// <summary>A second org with the project its runs hang off, through the real registry routes.</summary>
    private static async Task<Guid> CreateSecondTenantAsync(RunCreationTests.World world, string slug)
    {
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(world.Client, user, slug);
        await CreateProjectAsync(world.Client, user, org, slug + "-project");

        return org;
    }
}
