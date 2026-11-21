using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class RunSchemaTests
{
    // ------------------------------------------------------------------ (a) migration: RLS flags and shape

    [Fact]
    public async Task Run_AndPlannedExecution_HaveForcedRls()
    {
        var database = await _fixture.SharedDatabaseAsync();

        foreach (var table in new[] { "run", "planned_execution" })
        {
            (await _fixture.TableExistsAsync(database, table)).ShouldBeTrue(table);
            (await _fixture.RowSecurityAsync(database, table)).ShouldBe((true, true));
        }
    }

    [Fact]
    public async Task TenantPolicies_AreScopedToTheAppRole()
    {
        var database = await _fixture.SharedDatabaseAsync();

        // A policy with no `to` clause applies to PUBLIC; each tenant policy must name evidgate_app
        // only, so the operator's SELECT grant reads zero rows (T76).
        (await _fixture.PoliciesAsync(database, "run")).ShouldBe(
        [
            "run_owner_backfill:evidgate_owner:*:true:true",
            "run_tenant:evidgate_app:*:(org_id = evidgate.current_org_id()):(org_id = evidgate.current_org_id())",
        ]);
        (await _fixture.PoliciesAsync(database, "planned_execution")).ShouldBe(
        [
            "planned_execution_owner_backfill:evidgate_owner:*:true:true",
            "planned_execution_tenant:evidgate_app:*:(org_id = evidgate.current_org_id()):(org_id = evidgate.current_org_id())",
        ]);
    }

    [Fact]
    public async Task Run_Constraints_Exist()
    {
        var database = await _fixture.SharedDatabaseAsync();

        var constraints = await _fixture.ConstraintNamesAsync(database, "run");

        foreach (var name in new[]
                 {
                     "run_pkey", "run_org_id_key", "run_manifest_fk", "run_suite_revision_fk",
                     "run_environment_revision_fk", "run_policy_revision_fk", "run_parent_run_fk",
                     "run_purpose_check", "run_replay_grant_check", "run_state_check", "run_rerun_scope_check",
                     "run_late_window_minutes_check", "run_test_commit_check", "run_ci_run_ref_check",
                     "run_components_check", "run_version_check",
                 })
        {
            constraints.Any(entry => entry.EndsWith(":" + name, StringComparison.Ordinal)).ShouldBeTrue(name);
        }
    }

    [Fact]
    public async Task PlannedExecution_Constraints_Exist()
    {
        var database = await _fixture.SharedDatabaseAsync();

        var constraints = await _fixture.ConstraintNamesAsync(database, "planned_execution");

        foreach (var name in new[]
                 {
                     "planned_execution_pkey", "planned_execution_org_id_key", "planned_execution_run_execution_key",
                     "planned_execution_run_fk", "planned_execution_case_revision_fk",
                     "planned_execution_execution_key_check", "planned_execution_variant_hash_check",
                     "planned_execution_target_env_check", "planned_execution_shard_id_check",
                 })
        {
            constraints.Any(entry => entry.EndsWith(":" + name, StringComparison.Ordinal)).ShouldBeTrue(name);
        }
    }

    [Fact]
    public async Task RunIndexes_ExistInPgIndexes()
    {
        var database = await _fixture.SharedDatabaseAsync();

        (await _fixture.IndexesAsync(database, "run"))
            .Any(index => index.StartsWith("run_project_created_idx:", StringComparison.Ordinal)).ShouldBeTrue();

        // F2: no standalone planned_execution_run_idx. The reconcile read of 05.3.2 (every planned row
        // of one run) is served by the unique constraint's own index, left-prefixed by (org_id, run_id).
        var plannedIndexes = await _fixture.IndexesAsync(database, "planned_execution");
        plannedIndexes.Any(index => index.StartsWith("planned_execution_run_execution_key:", StringComparison.Ordinal)).ShouldBeTrue();
        plannedIndexes.Any(index => index.StartsWith("planned_execution_run_idx:", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public async Task Run_CarriesTheAuditColumnsAndTheVersionTrigger()
    {
        var database = await _fixture.SharedDatabaseAsync();

        // 00 §7: a mutable business table carries created_at/updated_at/version and the shared
        // trigger, so 05.3.1's optimistic-concurrency write cannot be won by two writers (M1W3-01).
        var columns = await _fixture.ColumnsAsync(database, "run");
        columns.Any(column => column.StartsWith("updated_at:", StringComparison.Ordinal)).ShouldBeTrue();
        columns.Any(column => column.StartsWith("version:", StringComparison.Ordinal)).ShouldBeTrue();

        (await _fixture.ScalarAsync<bool>(
            database,
            "select exists (select 1 from pg_catalog.pg_trigger where tgrelid = 'public.run'::regclass and tgname = 'run_set_updated_at_and_version')"))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task DeferredRunKeys_ExistAndAreValidated()
    {
        var database = await _fixture.SharedDatabaseAsync();

        // The two keys 0028/0029 deferred: added `not valid`, orphan-checked, then validated.
        (await _fixture.ScalarAsync<bool>(
            database,
            "select convalidated from pg_catalog.pg_constraint where conrelid = 'public.artifact'::regclass and conname = 'artifact_run_fk'"))
            .ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(
            database,
            "select convalidated from pg_catalog.pg_constraint where conrelid = 'public.producer_slot'::regclass and conname = 'producer_slot_run_fk'"))
            .ShouldBeTrue();
    }

    // ------------------------------------------------------------------ (b) grants

    [Fact]
    public async Task AppRole_HasOnlySelectInsert_OnRunAndPlannedExecution()
    {
        var database = await _fixture.SharedDatabaseAsync();

        foreach (var table in new[] { "run", "planned_execution" })
        {
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "select")).ShouldBeTrue(table);
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "insert")).ShouldBeTrue(table);
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "update")).ShouldBeFalse(table);
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "delete")).ShouldBeFalse(table);

            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_operator", table, "select")).ShouldBeTrue(table);
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_worker_sched", table, "select")).ShouldBeFalse(table);
        }
    }

    [Fact]
    public async Task AppRole_CannotUpdateOrDelete_Run()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        await InsertRunAsync(world, parents);

        // 0031 revoked table-level UPDATE and 0034 granted `updated_at` (the T91 row lock); 05.3.1's 0036 adds
        // `state` alone, so the arbitrary-column update below still fails at the privilege check while the
        // guarded `state` write is the one column the app role may name.
        (await CaptureAppErrorAsync(world, world.Org, "update public.run set purpose = 'replay'")).SqlState.ShouldBe("42501");
        (await CaptureAppErrorAsync(world, world.Org, "delete from public.run")).SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task AppRole_CannotUpdateOrDelete_PlannedExecution()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(world, parents);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(InsertPlannedExecutionSql(world.Org, runId, parents.TestRevisionId))).ShouldBe(1);
            await scope.CommitAsync();
        }

        (await CaptureAppErrorAsync(world, world.Org, "update public.planned_execution set required = false")).SqlState.ShouldBe("42501");
        (await CaptureAppErrorAsync(world, world.Org, "delete from public.planned_execution")).SqlState.ShouldBe("42501");
    }

    // ------------------------------------------------------------------ (f) the migration re-run

    [Fact]
    public async Task Migration_ReRun_KeepsAValidArtifactRow()
    {
        // Dedicated: it re-applies the migration and counts the artifact table whole, so it owns the database.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(dedicated);
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(world, parents);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(InsertArtifactSql(world.Org, world.Project, runId))).ShouldBe(1);
            await scope.CommitAsync();
        }

        // The file is idempotent: the tables, policies, grants and both deferred keys already exist,
        // the orphan check counts zero and `validate constraint` is a no-op — the row survives.
        await ReRunMigrationAsync(world.Database);

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.artifact")).ShouldBe(1);
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            "select convalidated from pg_catalog.pg_constraint where conrelid = 'public.artifact'::regclass and conname = 'artifact_run_fk'"))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Migration_ReRun_WithAnOrphanArtifact_FailsWithTheClearMessage()
    {
        // Dedicated: it drops a key and re-applies the migration, both database-wide.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(dedicated);

        // Drop the key so an orphan row can exist, exactly the state the orphan check guards.
        await _fixture.ExecuteAsync(world.Database, "alter table public.artifact drop constraint artifact_run_fk");

        var orphanId = Guid.CreateVersion7();
        await using (var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            (await scope.ExecuteAsync(
                "insert into public.artifact (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type, checksum, sensitivity) "
                + $"values ('{orphanId}', '{world.Org}', '{world.Project}', '{Guid.CreateVersion7()}', '{ExecutionKey}', 1, 'trace', 'pending/{world.Org}/{orphanId}', 'application/zip', '{VariantHash}', 'normal')"))
                .ShouldBe(1);
            await scope.CommitAsync();
        }

        var error = await ReRunMigrationExpectingErrorAsync(world.Database);

        error.SqlState.ShouldBe("23503");
        error.MessageText.ShouldContain("artifact_run_fk");
        error.MessageText.ShouldContain("no run in the same org");
    }
}
