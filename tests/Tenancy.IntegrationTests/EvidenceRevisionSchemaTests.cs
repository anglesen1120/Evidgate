// 05.3.2-T5.1/T5.2: migration 0049 through a real PostgreSQL — the append-only `evidence_revision`, the
// gap-free `revision_no` allocation, the closed `missing[]` code list, the composite pointer FK that 0036
// deferred to this story and the guards that keep `run.current_evidence_revision_id` on the latest revision
// of its own run (NULL on insert, forward only, and cleared by the migration role's erasure step alone).
using System.Net;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.3.2-T5 (0049) through a real PostgreSQL: AC9's append-only revision table, AC4's completeness rule,
/// the 00 §8.5 reason codes (T14's <c>setup_error</c> included) and T5.2's pointer wiring —
/// <c>evidence_revision_run_idx</c> for the current-revision lookup, the composite
/// <c>(org_id, current_evidence_revision_id) -&gt; evidence_revision (org_id, id)</c> key on <c>run</c>, and
/// the three guards: <c>guard_evidence_revision_insert</c> (gap-free numbers, serialised on the run row),
/// <c>guard_run_evidence_pointer</c> (forward to the latest revision only, and only the migration role's
/// erasure sweep may clear it) and <c>guard_run_evidence_pointer_insert</c> (a fresh run starts NULL).
/// <para>
/// The fixture is this suite's own <see cref="PostgresTenancyFixture"/> — one digest-pinned PostgreSQL 18
/// container per assembly, a database brought to head by the real <c>Evidgate.Migrator</c>.
/// Every write runs as <c>evidgate_app</c> inside a tenant-scoped transaction (00 §5.5 C3) unless a case
/// deliberately speaks as the owner (the migration role, for a re-run, the row lock order or the erasure
/// step that clears the pointer).
/// </para>
/// <para>
/// Lock order [DECISION D-S115-Q1]: every writer of a run's evidence takes the <c>run</c> row first
/// (<c>FOR NO KEY UPDATE</c>, or the pointer UPDATE that implies it), then inserts the revision, then moves
/// the pointer — all in one transaction. The insert trigger re-takes the same lock, so concurrent inserters
/// of one run queue on <c>run</c> instead of deadlocking.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class EvidenceRevisionSchemaTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) : IClassFixture<TenancyHostPool>
{
    /// <summary>The migration under test, re-run by the idempotency case.</summary>
    private const string MigrationFile = "0049_orchestration_evidence_revision.sql";

    /// <summary>
    /// 00 §8.5 reason codes: the closed list the migration's CHECK carries (T14 adds <c>setup_error</c>, and
    /// <c>0056_reconcile_missing_result.sql</c> re-creates the CHECK over the same codes plus the plan-coverage
    /// <c>missing_result</c>).
    /// </summary>
    private static readonly string[] MissingCodes =
    [
        "missing_shard",
        "count_mismatch",
        "artifact_missing",
        "build_mismatch",
        "unknown_status",
        "setup_error",
        "stale_epoch",
        "missing_result",
    ];

    /// <summary>A well-formed manifest hash: the 00 §7 RFC 8785 content address.</summary>
    private static readonly string ManifestHash = "sha256:" + new string('e', 64);

    private readonly PostgresTenancyFixture _fixture = fixture;
    // Reuse one API host per class; each world still owns its tenant data. Dedicated databases and overlapping worlds keep private hosts.
    private readonly TenancyHostPool _hosts = hosts;

    // ------------------------------------------------------------------ (a) AC9: append-only

    /// <summary>
    /// 00 §5.5 C6 / AC9: <c>evidgate_app</c> holds SELECT + INSERT on <c>evidence_revision</c> and no UPDATE
    /// or DELETE — not even column-level — so a revision is a row nobody can rewrite or remove. The
    /// rejection is the ACL's (42501), before any trigger or policy sees the row (0028/0034/0046 precedent).
    /// </summary>
    [Fact]
    public async Task EvidenceRevision_UpdateDelete_Revoked()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CreateRunAsync(world);
        await AppendRevisionAsync(world, world.Org, runId, revisionNo: 1);

        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", "evidence_revision", "select")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", "evidence_revision", "insert")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", "evidence_revision", "update")).ShouldBeFalse("C6: no update path");
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", "evidence_revision", "delete")).ShouldBeFalse("C6: no delete path");

        // No column grant hides behind the table-level refusal.
        (await _fixture.ScalarAsync<long>(
            world.Database,
            """
            select count(*)
              from information_schema.columns
             where table_schema = 'public' and table_name = 'evidence_revision'
               and has_column_privilege('evidgate_app', 'public.evidence_revision', column_name, 'UPDATE')
            """))
            .ShouldBe(0, "the app role must not be able to move any column of a stored revision");

        (await CaptureAppErrorAsync(world, world.Org, "update public.evidence_revision set outcome = 'has_failures'"))
            .SqlState.ShouldBe("42501");
        (await CaptureAppErrorAsync(world, world.Org, "delete from public.evidence_revision")).SqlState.ShouldBe("42501");

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.evidence_revision where org_id = '{world.Org}' and run_id = '{runId}'"))
            .ShouldBe(1);

        // The operator keeps read only and the scheduler holds nothing (no policy until 02.8.1, TL T76/D1).
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_operator", "evidence_revision", "select")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_operator", "evidence_revision", "insert")).ShouldBeFalse();
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_worker_sched", "evidence_revision", "select")).ShouldBeFalse();
    }

    // ------------------------------------------------------------------ (b) revision_no allocation

    /// <summary>
    /// The gap-free allocation under contention: eight app-role writers append to one run at the same time,
    /// each taking the run row first and reading <c>max(revision_no) + 1</c> under that lock. The result is
    /// revision 1..8 with no duplicate, no gap and no deadlock (40P01) — the plan's unique key alone would
    /// only forbid the duplicate.
    /// </summary>
    [Fact]
    public async Task EvidenceRevision_ConcurrentInserts_ProduceAGapFreeSequence()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CreateRunAsync(world);

        const int Writers = 8;

        var outcomes = await Task.WhenAll(
            Enumerable.Range(0, Writers).Select(_ => AppendNextRevisionAsync(world, world.Org, runId)));

        var failures = outcomes
            .Where(outcome => outcome.Error is not null)
            .Select(outcome => $"{outcome.Error!.SqlState}: {outcome.Error.MessageText}")
            .ToArray();
        failures.ShouldBeEmpty(string.Join(" | ", failures));
        outcomes.Select(o => o.RevisionNo).Order().ShouldBe(Enumerable.Range(1, Writers));

        var stored = await _fixture.ColumnAsync<int>(
            world.Database,
            $"select revision_no from public.evidence_revision where org_id = '{world.Org}' and run_id = '{runId}' order by revision_no");
        stored.ShouldBe(Enumerable.Range(1, Writers).ToArray(), "revision_no is 1..N with no gap and no duplicate");
    }

    /// <summary>
    /// The trigger's rule in isolation: the next revision of a run is <c>max + 1</c> (1 for the first), so
    /// 0, a first value other than 1, a duplicate and a gap are all 23514.
    /// </summary>
    [Fact]
    public async Task EvidenceRevision_RevisionNo_MustBeTheNextOneForTheRun()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CreateRunAsync(world);

        // The column's own floor.
        (await CaptureInsertErrorAsync(world, world.Org, runId, revisionNo: 0)).SqlState.ShouldBe("23514");

        // A first revision above 1: the unique key alone would accept it, the allocation trigger does not.
        (await CaptureInsertErrorAsync(world, world.Org, runId, revisionNo: 2)).SqlState.ShouldBe("23514");

        await AppendRevisionAsync(world, world.Org, runId, revisionNo: 1);

        (await CaptureInsertErrorAsync(world, world.Org, runId, revisionNo: 1)).SqlState.ShouldBe("23514");
        (await CaptureInsertErrorAsync(world, world.Org, runId, revisionNo: 3)).SqlState.ShouldBe("23514");

        var afterRefusals = await _fixture.ColumnAsync<int>(
            world.Database,
            $"select revision_no from public.evidence_revision where org_id = '{world.Org}' and run_id = '{runId}' order by revision_no");
        afterRefusals.ShouldHaveSingleItem().ShouldBe(1, "a refused insert leaves nothing behind");
    }

    // ------------------------------------------------------------------ (c) AC4 / 00 §8.5 completeness

    /// <summary>
    /// AC4 and the 00 §8.5 closed list: a <c>complete</c> row with a reason code is 23514, a code outside
    /// the list is 23514, and every code of the list — <c>setup_error</c> (T14) included — is accepted on an
    /// <c>incomplete</c> row.
    /// </summary>
    [Fact]
    public async Task EvidenceRevision_CompletenessAndMissingCodes_AreEnforced()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CreateRunAsync(world);

        var completeWithMissing = await CaptureInsertErrorAsync(
            world, world.Org, runId, revisionNo: 1, missing: "{missing_shard}");
        completeWithMissing.SqlState.ShouldBe("23514");
        completeWithMissing.ConstraintName.ShouldBe("evidence_revision_complete_has_no_missing_check");

        var unknownCode = await CaptureInsertErrorAsync(
            world, world.Org, runId, revisionNo: 1, completeness: "incomplete", missing: "{not_a_reason}");
        unknownCode.SqlState.ShouldBe("23514");
        unknownCode.ConstraintName.ShouldBe("evidence_revision_missing_check");

        for (var index = 0; index < MissingCodes.Length; index++)
        {
            await AppendRevisionAsync(
                world,
                world.Org,
                runId,
                revisionNo: index + 1,
                completeness: "incomplete",
                missing: "{" + MissingCodes[index] + "}",
                outcome: null);
        }

        (await _fixture.ColumnAsync<string>(
            world.Database,
            $"select array_to_string(missing, ',') from public.evidence_revision where org_id = '{world.Org}' and run_id = '{runId}' order by revision_no"))
            .ShouldBe(MissingCodes);

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.evidence_revision where org_id = '{world.Org}' and run_id = '{runId}' and 'setup_error' = any(missing)"))
            .ShouldBe(1, "T14: setup_error is an accepted reason code");
    }

    // ------------------------------------------------------------------ (d) T3: tenant isolation

    /// <summary>
    /// T2/T3: <c>FORCE RLS</c> plus the tenant policy on <c>evidence_revision</c> — a second org sees none of
    /// the first org's revisions, cannot write a row that claims the first org (the <c>WITH CHECK</c> of
    /// policy <c>evidence_revision_tenant</c> answers 42501) and cannot name the first org's run from a row
    /// of its own tenant (the composite <c>evidence_revision_run_fk</c> answers 23503).
    /// </summary>
    [Fact]
    public async Task EvidenceRevision_CrossTenant_IsInvisibleAndRefused()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var runId = await CreateRunAsync(world);
        await AppendRevisionAsync(world, world.Org, runId, revisionNo: 1);

        var otherOrg = await CreateSecondTenantAsync(world, "beta");

        (await _fixture.RowSecurityAsync(world.Database, "evidence_revision"))
            .ShouldBe((true, true), "DECISIONS F5: ENABLE + FORCE row level security");
        (await _fixture.PoliciesAsync(world.Database, "evidence_revision"))
            .ShouldContain(policy => policy.StartsWith("evidence_revision_tenant:evidgate_app:", StringComparison.Ordinal));

        (await ScalarAsOrgAsync<long>(world, otherOrg, "select count(*) from public.evidence_revision"))
            .ShouldBe(0, "another org's revisions are invisible under RLS");

        // (a) A row that claims org A while written under org B's scope: the trigger's run lock finds
        // nothing (RLS hides org A's run), the revision check passes (org A's revisions are invisible, so
        // the expected number is 1) and the policy's WITH CHECK refuses the row.
        (await CaptureInsertErrorAsync(world, otherOrg, runId, revisionNo: 1, rowOrg: world.Org))
            .SqlState.ShouldBe("42501");

        // (b) A row of org B naming org A's run: WITH CHECK passes (the row is org B's), the revision
        // check passes for the same reason as (a), and the composite (org_id, run_id) foreign key refuses
        // the tenant-crossing reference last — the structural backstop behind the policy.
        var foreignRunRef = await CaptureInsertErrorAsync(world, otherOrg, runId, revisionNo: 1);
        foreignRunRef.SqlState.ShouldBe("23503");
        foreignRunRef.ConstraintName.ShouldBe("evidence_revision_run_fk");

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.evidence_revision where org_id = '{world.Org}' and run_id = '{runId}'"))
            .ShouldBe(1);
    }

}
