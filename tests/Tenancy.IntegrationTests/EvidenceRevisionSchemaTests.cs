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
public sealed class EvidenceRevisionSchemaTests
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

    private readonly PostgresTenancyFixture _fixture;

    public EvidenceRevisionSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ (a) AC9: append-only

    /// <summary>
    /// 00 §5.5 C6 / AC9: <c>evidgate_app</c> holds SELECT + INSERT on <c>evidence_revision</c> and no UPDATE
    /// or DELETE — not even column-level — so a revision is a row nobody can rewrite or remove. The
    /// rejection is the ACL's (42501), before any trigger or policy sees the row (0028/0034/0046 precedent).
    /// </summary>
    [Fact]
    public async Task EvidenceRevision_UpdateDelete_Revoked()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
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

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.evidence_revision")).ShouldBe(1);

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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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
        await using var world = await RunCreationTests.StartAsync(_fixture);
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

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.evidence_revision")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ (e) T5.2: the pointer

    /// <summary>
    /// T5.2: the pointer moves to the latest revision of its run, and 05.3.1's read route resolves it — after
    /// a second revision is appended and the pointer moved, <c>GET /v1/runs/{r}</c> reports the newest one.
    /// </summary>
    [Fact]
    public async Task RunPointer_MovesToTheLatestRevision_AndGetRunReportsIt()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateRunAsync(world);

        var first = await AppendRevisionAsync(world, world.Org, runId, revisionNo: 1);
        (await MovePointerAsync(world, world.Org, runId, first)).ShouldBe(1);
        (await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select current_evidence_revision_id from public.run where id = '{runId}'"))
            .ShouldBe(first);

        var second = await AppendRevisionAsync(world, world.Org, runId, revisionNo: 2);
        (await MovePointerAsync(world, world.Org, runId, second)).ShouldBe(1);

        var answer = await world.Client.SendAsync(HttpMethod.Get, $"/v1/runs/{runId}", world.Admin);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("current_evidence_revision_id").GetGuid().ShouldBe(second);
        (await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select current_evidence_revision_id from public.run where id = '{runId}'"))
            .ShouldBe(second);
    }

    /// <summary>
    /// T5.2's guard, every app-role refusal case in one place: a stale revision, another run's revision of the
    /// same org and another org's revision are all 23514, the un-granted columns of <c>run</c> stay 42501, and
    /// the app role can never clear a set pointer (the migration role's erasure sweep is the one exception —
    /// <see cref="RunPointer_ClearingIsTheOwnerErasureStep"/>).
    /// </summary>
    [Fact]
    public async Task RunPointer_RefusesStaleCrossRunCrossOrgAndNull()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateRunAsync(world);
        var first = await AppendRevisionAsync(world, world.Org, runId, revisionNo: 1);
        var second = await AppendRevisionAsync(world, world.Org, runId, revisionNo: 2);

        // Another run of the same org, with its own revision 1.
        var siblingRun = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, world.Org);
        var siblingRevision = await AppendRevisionAsync(world, world.Org, siblingRun, revisionNo: 1);

        // Another org's run, with its own revision 1.
        var otherOrg = await CreateSecondTenantAsync(world, "beta");
        var foreignRun = await RunRowSeeder.SeedRunAsync(_fixture, world.Database, otherOrg);
        var foreignRevision = await AppendRevisionAsync(world, otherOrg, foreignRun, revisionNo: 1);

        // A revision of this run that is no longer the latest: the FK alone would accept it.
        (await MovePointerErrorAsync(world, world.Org, runId, first)).SqlState.ShouldBe("23514");

        // The latest revision of another run in the same org: the FK accepts it, the guard does not.
        (await MovePointerErrorAsync(world, world.Org, runId, siblingRevision)).SqlState.ShouldBe("23514");

        // Another org's revision fails the same guard first (RLS hides the target, so the guard's lookup
        // finds nothing and it raises 23514); the composite FK below is the structural backstop for the
        // case where the guard is out of the picture. Coordinator decision D-S115-Q1: expected state is
        // 23514, never 23503.
        (await MovePointerErrorAsync(world, world.Org, runId, foreignRevision)).SqlState.ShouldBe("23514");

        // (org_id, current_evidence_revision_id) -> evidence_revision (org_id, id): the catalog reports the
        // two columns on each side, so this is the composite FK and not a single-column pointer.
        (await _fixture.ConstraintsAsync(world.Database, "run"))
            .ShouldContain(
                entry => entry.StartsWith("f:run_current_evidence_revision_fk:", StringComparison.Ordinal)
                    && entry.Contains(
                        "FOREIGN KEY (org_id, current_evidence_revision_id) REFERENCES evidence_revision(org_id, id)",
                        StringComparison.Ordinal),
                "the pointer FK is composite on both sides");

        // A pointer that never was set moves once, and the app role can never clear it again.
        (await MovePointerAsync(world, world.Org, runId, second)).ShouldBe(1);
        (await MovePointerErrorAsync(world, world.Org, runId, null)).SqlState.ShouldBe("23514");

        // 0049 grants the pointer column alone: every other run column outside 0034/0036 is still 42501.
        (await CaptureAppErrorAsync(world, world.Org, $"update public.run set purpose = 'replay' where id = '{runId}'"))
            .SqlState.ShouldBe("42501");
        (await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select current_evidence_revision_id from public.run where id = '{runId}'"))
            .ShouldBe(second);
    }

    /// <summary>
    /// D-S115-Q1 inside the database: two writers of one run serialise on the run row, not on the unique key.
    /// Writer A inserts revision 1 and, with no explicit lock of its own, holds the row lock
    /// <c>guard_evidence_revision_insert</c> took <c>FOR NO KEY UPDATE</c> until it commits. Writer B's own
    /// insert of revision 1 is then observed in <c>pg_stat_activity</c> waiting on a lock held by A's pid (not
    /// slept on), and once A commits B is refused 23514 by the guard — a duplicate number, which
    /// <c>evidence_revision_run_revision_key</c> would answer 23505 — so both the wait and the refusal are
    /// the trigger's, before any unique index check.
    /// </summary>
    [Fact]
    public async Task EvidenceRevision_InsertGuard_SerialisesTwoWritersOfOneRun()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateRunAsync(world);

        await using var holding = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var holderPid = await holding.ScalarAsync<int>("select pg_backend_pid()");
        var first = RevisionInsert(Guid.CreateVersion7(), world.Org, runId, revisionNo: 1);
        (await holding.ExecuteAsync(first.Sql, first.Parameters)).ShouldBe(1);

        await using var competing = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var blockedPid = await competing.ScalarAsync<int>("select pg_backend_pid()");
        var second = RevisionInsert(Guid.CreateVersion7(), world.Org, runId, revisionNo: 1);
        var attempt = competing.ExecuteAsync(second.Sql, second.Parameters);

        (await WaitForLockWaitAsync(world.Database, blockedPid, holderPid))
            .ShouldBeTrue($"writer B (pid {blockedPid}) never showed a lock wait on writer A (pid {holderPid})");
        attempt.IsCompleted.ShouldBeFalse("the second insert waits inside the trigger, before the unique key is reached");

        await holding.CommitAsync();

        var refusal = await Should.ThrowAsync<PostgresException>(
            async () => await attempt.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        refusal.SqlState.ShouldBe("23514", "the guard refuses it; the unique key would answer 23505");
        refusal.MessageText.ShouldContain("revision_no must be");

        (await _fixture.ColumnAsync<int>(
            world.Database,
            $"select revision_no from public.evidence_revision where org_id = '{world.Org}' and run_id = '{runId}'"))
            .ShouldHaveSingleItem().ShouldBe(1, "the refused insert left nothing behind");
    }

    /// <summary>
    /// The CHECK boundaries around the reason list and the watermark: a negative <c>event_watermark</c> is
    /// 23514, a <c>missing</c> array holding a NULL element is 23514, a two-dimensional <c>missing</c> array is
    /// 23514 (the column holds a flat list) and a repeated code is accepted — the reason list is a set, not a
    /// ranking. Every case runs on an <c>incomplete</c> row and names its constraint, so no refusal can pass
    /// on <c>evidence_revision_complete_has_no_missing_check</c> instead.
    /// </summary>
    [Fact]
    public async Task EvidenceRevision_MissingArrayAndWatermark_Boundaries()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateRunAsync(world);

        var negativeWatermark = await CaptureInsertErrorAsync(
            world, world.Org, runId, revisionNo: 1, completeness: "incomplete", eventWatermark: -1);
        negativeWatermark.SqlState.ShouldBe("23514");
        negativeWatermark.ConstraintName.ShouldBe("evidence_revision_watermark_check");

        var nullElement = await CaptureInsertErrorAsync(
            world, world.Org, runId, revisionNo: 1, completeness: "incomplete", missing: "{NULL}");
        nullElement.SqlState.ShouldBe("23514");
        nullElement.ConstraintName.ShouldBe("evidence_revision_missing_check");

        var nested = await CaptureInsertErrorAsync(
            world,
            world.Org,
            runId,
            revisionNo: 1,
            completeness: "incomplete",
            missingExpression: "array[array['setup_error', 'setup_error']]::text[]");
        nested.SqlState.ShouldBe("23514");
        nested.ConstraintName.ShouldBe("evidence_revision_missing_check");

        await AppendRevisionAsync(
            world,
            world.Org,
            runId,
            revisionNo: 1,
            completeness: "incomplete",
            missing: "{setup_error,setup_error}",
            outcome: null);

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.evidence_revision where org_id = '{world.Org}' and run_id = '{runId}' "
            + "and missing = '{setup_error,setup_error}'::text[]"))
            .ShouldBe(1, "a repeated code is the same set, and the row exists");
    }

    /// <summary>
    /// A fresh run has no revisions, so its pointer starts NULL: an INSERT that already names one is refused
    /// 23514 by <c>run_evidence_pointer_insert_guard</c>, while <c>run_current_evidence_revision_fk</c> alone
    /// would accept any revision of the same org. The same copy with a NULL pointer is accepted, so the
    /// refusal is the pointer and not the shape of the row.
    /// </summary>
    [Fact]
    public async Task RunPointer_InsertWithAPointer_IsRefused()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var template = await CreateRunAsync(world);
        var revision = await AppendRevisionAsync(world, world.Org, template, revisionNo: 1);

        var clean = Guid.CreateVersion7();
        (await InsertRunCopyAsync(world, clean, template, null))
            .ShouldBe(1, "without a pointer the same INSERT is legal");

        var refusedId = Guid.CreateVersion7();
        var refusal = await InsertRunCopyErrorAsync(world, refusedId, template, revision);
        refusal.SqlState.ShouldBe("23514");
        refusal.MessageText.ShouldContain("must be null on insert");

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.run where id in ('{clean:D}', '{refusedId:D}')"))
            .ShouldBe(1, "only the pointer-less copy exists");
    }

    /// <summary>
    /// The one clearing path: the app role may move the pointer forward but never back to NULL (23514, the
    /// same guard as <see cref="RunPointer_RefusesStaleCrossRunCrossOrgAndNull"/>), while the migration role —
    /// the erasure sweep — may clear it, because the <c>run</c> ↔ <c>evidence_revision</c> foreign-key cycle
    /// has no <c>ON DELETE</c> action.
    /// </summary>
    [Fact]
    public async Task RunPointer_ClearingIsTheOwnerErasureStep()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateRunAsync(world);
        var revision = await AppendRevisionAsync(world, world.Org, runId, revisionNo: 1);
        (await MovePointerAsync(world, world.Org, runId, revision)).ShouldBe(1);

        var appClearing = await MovePointerErrorAsync(world, world.Org, runId, null);
        appClearing.SqlState.ShouldBe("23514");
        appClearing.MessageText.ShouldContain("cannot be cleared");

        await using var erasure = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        (await erasure.ExecuteAsync(
            "update public.run set current_evidence_revision_id = null where org_id = @org_id and id = @run_id",
            ("org_id", world.Org),
            ("run_id", runId))).ShouldBe(1);
        await erasure.CommitAsync();

        (await _fixture.ScalarAsync<bool>(
            world.Database,
            $"select current_evidence_revision_id is null from public.run where id = '{runId:D}'"))
            .ShouldBeTrue("the owner's erasure step clears the pointer");
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.evidence_revision"))
            .ShouldBe(1, "clearing the pointer never touches the append-only revision row");
    }

    // ------------------------------------------------------------------ (f) 00 §5.7 P4: the lookup index

    /// <summary>
    /// Plan §4 / 00 §5.7 P4: <c>evidence_revision_run_idx (org_id, run_id, revision_no desc)</c> exists and
    /// serves the current-revision lookup. Two things would hide that on a three-row table: the planner
    /// prefers a sequential scan, and the unique key <c>evidence_revision_run_revision_key (org_id, run_id,
    /// revision_no)</c> answers the same predicate and the same ORDER BY at identical cost (its key read
    /// backwards), so on a tie the path built first — the unique key's, created with the table — wins. This
    /// per-test database therefore drops that key (nothing references it; the pointer FK targets
    /// <c>(org_id, id)</c>) and turns <c>enable_seqscan</c> off transaction-locally, which leaves the index
    /// under test as the only ordered path. The assertion is about the index being usable for this lookup,
    /// not about a production row count.
    /// </summary>
    [Fact]
    public async Task EvidenceRevision_RunIndex_ServesTheCurrentRevisionLookup()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateRunAsync(world);
        for (var revisionNo = 1; revisionNo <= 3; revisionNo++)
        {
            await AppendRevisionAsync(world, world.Org, runId, revisionNo);
        }

        (await _fixture.IndexesAsync(world.Database, "evidence_revision"))
            .ShouldContain(index => index.StartsWith("evidence_revision_run_idx:", StringComparison.Ordinal)
                && index.Contains("(org_id, run_id, revision_no DESC)", StringComparison.Ordinal));

        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database), world.Org);
        await scope.ExecuteAsync("alter table public.evidence_revision drop constraint evidence_revision_run_revision_key");
        await scope.ExecuteAsync("analyze public.evidence_revision");
        await scope.ExecuteAsync("set local enable_seqscan = off");
        var plan = await scope.ScalarAsync<string>(
            """
            explain (format json)
            select id
              from public.evidence_revision
             where org_id = @org_id and run_id = @run_id
             order by revision_no desc
             limit 1
            """,
            ("org_id", world.Org),
            ("run_id", runId));

        plan.ShouldContain("\"Node Type\": \"Index Scan\"");
        plan.ShouldContain("\"Index Name\": \"evidence_revision_run_idx\"");
        plan.ShouldNotContain("Seq Scan", Case.Insensitive);
    }

    // ------------------------------------------------------------------ (g) the migration re-run

    /// <summary>
    /// The file is idempotent (<c>if not exists</c> / <c>drop … if exists</c> plus a catalog-guarded FK): a
    /// re-run as the migration role keeps the table, its row, the pointer FK, the grants and exactly one of
    /// each trigger, and the pointer still moves afterwards.
    /// </summary>
    [Fact]
    public async Task EvidenceRevision_MigrationReRun_KeepsTableForeignKeyGrantsAndTriggers()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CreateRunAsync(world);
        var existing = await AppendRevisionAsync(world, world.Org, runId, revisionNo: 1);

        await ReRunMigrationAsync(world.Database);

        (await _fixture.TableExistsAsync(world.Database, "evidence_revision")).ShouldBeTrue();
        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.evidence_revision")).ShouldBe(1);
        (await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.evidence_revision where org_id = '{world.Org}'"))
            .ShouldBe(existing);
        (await _fixture.ConstraintsAsync(world.Database, "run"))
            .Count(constraint => constraint.StartsWith("f:run_current_evidence_revision_fk:", StringComparison.Ordinal))
            .ShouldBe(1, "the catalog-guarded FK is added once");
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            "select has_column_privilege('evidgate_app', 'public.run', 'current_evidence_revision_id', 'update')"))
            .ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            "select has_function_privilege('evidgate_app', 'public.guard_run_evidence_pointer_insert()', 'execute')"))
            .ShouldBeTrue("the app role fires the insert guard of 0049");
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", "evidence_revision", "update")).ShouldBeFalse();
        (await TriggerCountAsync(world.Database, "public.evidence_revision", "evidence_revision_guard_insert")).ShouldBe(1);
        (await TriggerCountAsync(world.Database, "public.run", "run_evidence_pointer_guard")).ShouldBe(1);
        (await TriggerCountAsync(world.Database, "public.run", "run_evidence_pointer_insert_guard")).ShouldBe(1);
        (await _fixture.IndexesAsync(world.Database, "evidence_revision"))
            .Count(index => index.StartsWith("evidence_revision_run_idx:", StringComparison.Ordinal))
            .ShouldBe(1, "create index if not exists is a no-op on the re-run");
        (await _fixture.IndexesAsync(world.Database, "run"))
            .Count(index => index.StartsWith("run_current_evidence_revision_idx:", StringComparison.Ordinal))
            .ShouldBe(1, "create index if not exists is a no-op on the re-run");

        var appended = await AppendRevisionAsync(world, world.Org, runId, revisionNo: 2);
        (await MovePointerAsync(world, world.Org, runId, appended))
            .ShouldBe(1, "the grant, the trigger and the FK all survive the re-run");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>One run through the served route; returns its id.</summary>
    private static async Task<Guid> CreateRunAsync(RunCreationTests.World world)
    {
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));

        return created.Id("run_id");
    }

    /// <summary>A second org (and the project its runs hang off) through the real registry routes.</summary>
    private static async Task<Guid> CreateSecondTenantAsync(RunCreationTests.World world, string slug)
    {
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(world.Client, user, slug);
        await CreateProjectAsync(world.Client, user, org, slug + "-project");

        return org;
    }

    /// <summary>Appends one revision as the app role in its own committed transaction.</summary>
    private async Task<Guid> AppendRevisionAsync(
        RunCreationTests.World world,
        Guid org,
        Guid runId,
        int revisionNo,
        string completeness = "complete",
        string missing = "{}",
        string? outcome = "all_passed")
    {
        var id = Guid.CreateVersion7();
        var insert = RevisionInsert(id, org, runId, revisionNo, completeness, missing, outcome);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);
        (await scope.ExecuteAsync(insert.Sql, insert.Parameters)).ShouldBe(1);
        await scope.CommitAsync();

        return id;
    }

    /// <summary>
    /// One concurrent writer (D-S115-Q1): the run row locked <c>FOR NO KEY UPDATE</c> first, then
    /// <c>max + 1</c> read under that lock, then the insert, then the commit. A refusal or a deadlock is
    /// returned rather than thrown so the assertion names it.
    /// </summary>
    private async Task<(int RevisionNo, PostgresException? Error)> AppendNextRevisionAsync(
        RunCreationTests.World world,
        Guid org,
        Guid runId)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);

        try
        {
            (await scope.ScalarAsync<int>(
                "select 1 from public.run where org_id = @org_id and id = @run_id for no key update",
                ("org_id", org),
                ("run_id", runId)))
                .ShouldBe(1);

            var revisionNo = await scope.ScalarAsync<int>(
                "select coalesce(max(revision_no), 0) + 1 from public.evidence_revision "
                + "where org_id = @org_id and run_id = @run_id",
                ("org_id", org),
                ("run_id", runId));

            var insert = RevisionInsert(Guid.CreateVersion7(), org, runId, revisionNo);
            (await scope.ExecuteAsync(insert.Sql, insert.Parameters)).ShouldBe(1);
            await scope.CommitAsync();

            return (revisionNo, null);
        }
        catch (PostgresException error)
        {
            return (0, error);
        }
    }

    /// <summary>Runs one pointer UPDATE as the app role and returns the affected row count.</summary>
    private async Task<int> MovePointerAsync(
        RunCreationTests.World world,
        Guid org,
        Guid runId,
        Guid? revisionId)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);
        var affected = await scope.ExecuteAsync(
            "update public.run set current_evidence_revision_id = @revision_id where org_id = @org_id and id = @run_id",
            ("revision_id", revisionId is { } id ? (object)id : DBNull.Value),
            ("org_id", org),
            ("run_id", runId));
        await scope.CommitAsync();

        return affected;
    }

    /// <summary>Runs one pointer UPDATE as the app role and returns the server error.</summary>
    private async Task<PostgresException> MovePointerErrorAsync(
        RunCreationTests.World world,
        Guid org,
        Guid runId,
        Guid? revisionId)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);

        return await scope.CaptureErrorAsync(
            "update public.run set current_evidence_revision_id = @revision_id where org_id = @org_id and id = @run_id",
            ("revision_id", revisionId is { } id ? (object)id : DBNull.Value),
            ("org_id", org),
            ("run_id", runId));
    }

    /// <summary>
    /// Polls <c>pg_stat_activity</c> until the backend <paramref name="blockedPid"/> waits on a lock held by
    /// <paramref name="holderPid"/> (<c>pg_blocking_pids</c> resolves the transaction-id wait a row lock shows
    /// as), so a test observes the block instead of sleeping and hoping. Returns false on timeout; the caller
    /// asserts on that.
    /// </summary>
    private async Task<bool> WaitForLockWaitAsync(string database, int blockedPid, int holderPid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var waiting = await _fixture.ScalarAsync<long>(
                database,
                "select count(*) from pg_stat_activity "
                + $"where pid = {blockedPid} and wait_event_type = 'Lock' "
                + $"and array_position(pg_blocking_pids(pid), {holderPid}) is not null");
            if (waiting == 1)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        }

        return false;
    }

    /// <summary>One run row of the same org, copied from an existing run through the app role; the pointer is overridable.</summary>
    private const string RunCopyInsert =
        """
        insert into public.run
          (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id,
           collection_deadline_at, late_window_minutes, current_evidence_revision_id)
        select @id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id,
               collection_deadline_at, late_window_minutes, @revision_id::uuid
          from public.run
         where org_id = @org_id and id = @template_run_id
        """;

    /// <summary>Writes one copy of <paramref name="template"/> as the app role and returns the affected row count.</summary>
    private async Task<int> InsertRunCopyAsync(
        RunCreationTests.World world,
        Guid id,
        Guid template,
        Guid? revision)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        var affected = await scope.ExecuteAsync(RunCopyInsert, RunCopyParameters(id, world.Org, template, revision));
        await scope.CommitAsync();

        return affected;
    }

    /// <summary>Writes one copy of <paramref name="template"/> as the app role and returns the server error.</summary>
    private async Task<PostgresException> InsertRunCopyErrorAsync(
        RunCreationTests.World world,
        Guid id,
        Guid template,
        Guid? revision)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        return await scope.CaptureErrorAsync(RunCopyInsert, RunCopyParameters(id, world.Org, template, revision));
    }

    private static (string Name, object Value)[] RunCopyParameters(Guid id, Guid org, Guid template, Guid? revision) =>
    [
        ("id", id),
        ("org_id", org),
        ("template_run_id", template),
        ("revision_id", revision is { } value ? (object)value : DBNull.Value),
    ];

    /// <summary>
    /// Runs one INSERT as the app role in a fresh transaction and returns the server error.
    /// <paramref name="rowOrg"/> defaults to the scope's org; a different value writes a row that claims
    /// another tenant.
    /// </summary>
    private async Task<PostgresException> CaptureInsertErrorAsync(
        RunCreationTests.World world,
        Guid org,
        Guid runId,
        int revisionNo,
        string completeness = "complete",
        string missing = "{}",
        Guid? rowOrg = null,
        long eventWatermark = 0,
        string? missingExpression = null)
    {
        var insert = RevisionInsert(
            Guid.CreateVersion7(),
            rowOrg ?? org,
            runId,
            revisionNo,
            completeness,
            missing,
            eventWatermark: eventWatermark,
            missingExpression: missingExpression);
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);

        return await scope.CaptureErrorAsync(insert.Sql, insert.Parameters);
    }

    /// <summary>Runs one statement as the app role inside a fresh tenant scope and returns the server error.</summary>
    private async Task<PostgresException> CaptureAppErrorAsync(
        RunCreationTests.World world,
        Guid org,
        string sql)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);

        return await scope.CaptureErrorAsync(sql);
    }

    /// <summary>Runs one scalar read as the app role for <paramref name="org"/>.</summary>
    private async Task<T> ScalarAsOrgAsync<T>(RunCreationTests.World world, Guid org, string sql)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);

        return await scope.ScalarAsync<T>(sql);
    }

    /// <summary>How many non-internal triggers of one name sit on the table.</summary>
    private Task<long> TriggerCountAsync(string database, string table, string trigger) =>
        _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from pg_catalog.pg_trigger where tgrelid = '{table}'::regclass and tgname = '{trigger}' and not tgisinternal");

    /// <summary>
    /// One raw INSERT of an <c>evidence_revision</c> row as the app role; the bounded columns are overridable.
    /// <paramref name="missingExpression"/> replaces the quoted array literal (a nested array needs the
    /// <c>array[…]</c> constructor, which no literal expresses).
    /// </summary>
    private static (string Sql, (string Name, object Value)[] Parameters) RevisionInsert(
        Guid id,
        Guid org,
        Guid runId,
        int revisionNo,
        string completeness = "complete",
        string missing = "{}",
        string? outcome = "all_passed",
        long eventWatermark = 0,
        string? missingExpression = null)
    {
        var missingSql = missingExpression ?? $"'{missing}'::text[]";
        var sql =
            $"""
            insert into public.evidence_revision
              (id, org_id, run_id, revision_no, completeness, missing, outcome, event_watermark, manifest_hash)
            values (@id, @org_id, @run_id, @revision_no, @completeness, {missingSql}, @outcome, @event_watermark, @manifest_hash)
            """;

        return (sql,
        [
            ("id", id),
            ("org_id", org),
            ("run_id", runId),
            ("revision_no", revisionNo),
            ("completeness", completeness),
            ("outcome", (object?)outcome ?? DBNull.Value),
            ("event_watermark", eventWatermark),
            ("manifest_hash", ManifestHash),
        ]);
    }

    /// <summary>The migration file's own SQL, executed as the migration role — the re-run path.</summary>
    private async Task ReRunMigrationAsync(string database)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await scope.ExecuteAsync(await MigrationSqlAsync());
        await scope.CommitAsync();
    }

    private static Task<string> MigrationSqlAsync() =>
        File.ReadAllTextAsync(
            Path.Combine(PostgresTenancyFixture.RepoMigrationsPath, MigrationFile),
            TestContext.Current.CancellationToken);
}
