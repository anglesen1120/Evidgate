using System.Net;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceRevisionSchemaTests
{
    // ------------------------------------------------------------------ (e) T5.2: the pointer

    /// <summary>
    /// T5.2: the pointer moves to the latest revision of its run, and 05.3.1's read route resolves it — after
    /// a second revision is appended and the pointer moved, <c>GET /v1/runs/{r}</c> reports the newest one.
    /// </summary>
    [Fact]
    public async Task RunPointer_MovesToTheLatestRevision_AndGetRunReportsIt()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
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
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
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
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
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
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
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
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
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
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
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
        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.evidence_revision where org_id = '{world.Org}' and run_id = '{runId:D}'"))
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
        // Dedicated: the plan needs this database's unique key dropped and seqscan off, both table-wide.
        await using var db = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await RunCreationTests.StartAsync(_fixture, database: db.Name);
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
        // Dedicated: the re-run applies migration DDL to the whole database and counts its single revision table-wide.
        await using var db = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await RunCreationTests.StartAsync(_fixture, database: db.Name);
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

}
