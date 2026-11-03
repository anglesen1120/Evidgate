using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.2.1-T1.1 through a real PostgreSQL: migration 0031's <c>run</c> and <c>planned_execution</c>
/// (FORCE RLS, the tenant keys, the composite tenant FKs, the app-role grants and the bound CHECKs)
/// plus the two deferred composite run keys on <c>artifact</c> (0028) and <c>producer_slot</c> (0029).
/// <para>
/// The fixture is this project's own <see cref="PostgresTenancyFixture"/> — one digest-pinned
/// PostgreSQL 18 container per assembly, a fresh database per test brought to head by the real
/// <c>Evidgate.Migrator</c> — the 06.1.1-T1 / 04.1.2-T4 precedent for a new table in this suite.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed class RunSchemaTests
{
    /// <summary>The migration under test, re-run by the existing-row-safety cases.</summary>
    private const string MigrationFile = "0031_orchestration_run.sql";

    /// <summary>A well-formed execution key: the 00 §7 sha256 hex.</summary>
    private const string ExecutionKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>A well-formed variant hash, <c>sha256:&lt;64 hex&gt;</c> (03.2.1).</summary>
    private const string VariantHash = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly PostgresTenancyFixture _fixture;

    public RunSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, DedicatedDatabase Owned, Guid User, Guid Org, Guid Project) : IAsyncDisposable
    {
        public string Database => Owned.Name;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
            await Owned.DisposeAsync();
        }
    }

    /// <summary>The parent rows a <c>run</c> row needs, seeded as the migration role.</summary>
    private sealed record Parents(Guid SuiteRevisionId, Guid EnvironmentRevisionId, Guid ManifestId, Guid TestRevisionId);

    /// <summary>One migrated database with an org and a project, created through the real registry routes.</summary>
    private async Task<World> StartAsync()
    {
        var owned = await _fixture.CreateDedicatedDatabaseAsync();
        var database = owned.Name;
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, owned, user, org, project);
    }

    /// <summary>An org and a project in it, created through the real routes.</summary>
    private static async Task<(Guid Org, Guid Project)> CreateOtherTenantAsync(World world, string slug)
    {
        var unique = Unique.Slug(slug);
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(world.Client, user, unique);
        var project = await CreateProjectAsync(world.Client, user, org, unique + "-project");

        return (org, project);
    }

    /// <summary>
    /// The manifest/suite/environment/test-revision parents of one <c>run</c>, written as the
    /// migration role (the owner backfill policies of 0004/0005 permit it) so a test can insert the
    /// row under test as the app role.
    /// </summary>
    private async Task<Parents> SeedParentsAsync(string database, Guid org, Guid project)
    {
        var suiteId = Guid.CreateVersion7();
        var suiteRevisionId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        var environmentRevisionId = Guid.CreateVersion7();
        var manifestId = Guid.CreateVersion7();
        var caseId = Guid.CreateVersion7();
        var testRevisionId = Guid.CreateVersion7();

        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));

        await scope.ExecuteAsync(
            "insert into public.suite (id, org_id, project_id, name, slug) values (@id, @org_id, @project_id, 'probe', 'probe-suite')",
            ("id", suiteId), ("org_id", org), ("project_id", project));
        await scope.ExecuteAsync(
            """
            insert into public.suite_revision (id, org_id, suite_id, revision_no, revision_hash, shard_strategy, retry_policy)
            values (@id, @org_id, @suite_id, 1, @hash, '{"kind":"none"}', '{"max_retries":0}')
            """,
            ("id", suiteRevisionId), ("org_id", org), ("suite_id", suiteId), ("hash", "sha256:" + new string('a', 64)));
        await scope.ExecuteAsync(
            "insert into public.environment (id, org_id, project_id, environment_key, name) values (@id, @org_id, @project_id, 'staging', 'Staging')",
            ("id", environmentId), ("org_id", org), ("project_id", project));
        await scope.ExecuteAsync(
            """
            insert into public.environment_revision (id, org_id, project_id, environment_id, revision_no, base_urls, dependency_versions, revision_hash, created_by)
            values (@id, @org_id, @project_id, @environment_id, 1, '{}', '{}', @hash, 'probe')
            """,
            ("id", environmentRevisionId), ("org_id", org), ("project_id", project), ("environment_id", environmentId),
            ("hash", "sha256:" + new string('c', 64)));
        await scope.ExecuteAsync(
            """
            insert into public.manifest (id, org_id, project_id, manifest_hash, schema_version, suite_revision_id, environment_revision_id, shard_plan, matrix, env_needs, planned_count, document, verified_by)
            values (@id, @org_id, @project_id, @hash, 1, @suite_revision_id, @environment_revision_id, '{}', '{}', '{}', 0, '{}', 'probe')
            """,
            ("id", manifestId), ("org_id", org), ("project_id", project), ("hash", "sha256:" + new string('d', 64)),
            ("suite_revision_id", suiteRevisionId), ("environment_revision_id", environmentRevisionId));
        await scope.ExecuteAsync(
            """
            insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit)
            values (@id, @org_id, @project_id, @annotation_id, 'probe', 'playwright', 'abcdef0')
            """,
            ("id", caseId), ("org_id", org), ("project_id", project), ("annotation_id", Guid.CreateVersion7()));
        await scope.ExecuteAsync(
            """
            insert into public.test_revision (id, org_id, case_id, revision_hash, commit_sha, file_path, framework, content_sha256, assertion_hash)
            values (@id, @org_id, @case_id, @hash, 'abcdef0', 'a', 'playwright', @hash, @hash)
            """,
            ("id", testRevisionId), ("org_id", org), ("case_id", caseId), ("hash", "sha256:" + new string('a', 64)));

        await scope.CommitAsync();

        return new Parents(suiteRevisionId, environmentRevisionId, manifestId, testRevisionId);
    }

    /// <summary>One raw insert of a run row as the app role; every bounded column is overridable.</summary>
    private static string InsertRunSql(
        Guid org,
        Guid project,
        Parents parents,
        Guid? id = null,
        Guid? manifestId = null,
        Guid? policyRevisionId = null,
        Guid? parentRunId = null,
        string? testCommit = null,
        string? ciRunRef = null,
        string components = "'{}'::jsonb",
        string? rerunScope = null,
        string purpose = "regular",
        Guid? replayGrantId = null,
        string state = "created",
        int lateWindowMinutes = 1440,
        string collectionDeadline = "now() + interval '2 hours'",
        int version = 0) =>
        "insert into public.run "
        + "(id, org_id, project_id, manifest_id, policy_revision_id, suite_revision_id, environment_revision_id, test_commit, ci_run_ref, components, parent_run_id, rerun_scope, purpose, replay_grant_id, state, collection_deadline_at, late_window_minutes, version) "
        + $"values ('{id ?? Guid.CreateVersion7()}', '{org}', '{project}', '{manifestId ?? parents.ManifestId}', "
        + $"{(policyRevisionId is null ? "null" : $"'{policyRevisionId}'")}, '{parents.SuiteRevisionId}', '{parents.EnvironmentRevisionId}', "
        + $"{(testCommit is null ? "null" : $"'{testCommit}'")}, {(ciRunRef is null ? "null" : $"'{ciRunRef}'::jsonb")}, {components}, "
        + $"{(parentRunId is null ? "null" : $"'{parentRunId}'")}, {(rerunScope is null ? "null" : $"'{rerunScope}'")}, '{purpose}', "
        + $"{(replayGrantId is null ? "null" : $"'{replayGrantId}'")}, '{state}', {collectionDeadline}, {lateWindowMinutes}, {version})";

    /// <summary>One raw insert of a planned-execution row as the app role; every bounded column is overridable.</summary>
    private static string InsertPlannedExecutionSql(
        Guid org,
        Guid runId,
        Guid caseRevisionId,
        Guid? id = null,
        string executionKey = ExecutionKey,
        string variantHash = VariantHash,
        string targetEnv = "staging",
        string shardId = "shard-1") =>
        "insert into public.planned_execution "
        + "(id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id) "
        + $"values ('{id ?? Guid.CreateVersion7()}', '{org}', '{runId}', '{executionKey}', '{caseRevisionId}', '{variantHash}', '{targetEnv}', '{shardId}')";

    /// <summary>One raw insert of an artifact row as the app role, using the 06.1.2 staging key shape.</summary>
    private static string InsertArtifactSql(Guid org, Guid project, Guid runId)
    {
        var id = Guid.CreateVersion7();

        return "insert into public.artifact "
            + "(id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type, checksum, sensitivity) "
            + $"values ('{id}', '{org}', '{project}', '{runId}', '{ExecutionKey}', 1, 'trace', 'pending/{org}/{id}', 'application/zip', '{VariantHash}', 'normal')";
    }

    /// <summary>One raw insert of a producer-slot row as the app role.</summary>
    private static string InsertProducerSlotSql(Guid org, Guid runId) =>
        "insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id) "
        + $"values ('{Guid.CreateVersion7()}', '{org}', '{runId}', 1, 'shard-1')";

    /// <summary>One committed run row for <paramref name="world"/>'s org, written as the app role.</summary>
    private async Task<Guid> InsertRunAsync(World world, Parents parents, Guid? id = null)
    {
        var runId = id ?? Guid.CreateVersion7();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(InsertRunSql(world.Org, world.Project, parents, runId))).ShouldBe(1);
        await scope.CommitAsync();

        return runId;
    }

    /// <summary>One committed run row for an arbitrary org, written as the app role.</summary>
    private async Task<Guid> InsertRunAsync(World world, Guid org, Guid project, Parents parents)
    {
        var runId = Guid.CreateVersion7();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);
        (await scope.ExecuteAsync(InsertRunSql(org, project, parents, runId))).ShouldBe(1);
        await scope.CommitAsync();

        return runId;
    }

    /// <summary>Runs one statement as the app role inside a fresh tenant scope and returns the server error.</summary>
    private async Task<PostgresException> CaptureAppErrorAsync(World world, Guid org, string sql)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);

        return await scope.CaptureErrorAsync(sql);
    }

    /// <summary>The migration file's own SQL, executed as the migration role — the re-run path.</summary>
    private async Task ReRunMigrationAsync(string database)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));
        await scope.ExecuteAsync(await MigrationSqlAsync());
        await scope.CommitAsync();
    }

    /// <summary>The migration file's own SQL, executed as the migration role, expecting a rejection.</summary>
    private async Task<PostgresException> ReRunMigrationExpectingErrorAsync(string database)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));

        return await scope.CaptureErrorAsync(await MigrationSqlAsync());
    }

    private static Task<string> MigrationSqlAsync() =>
        File.ReadAllTextAsync(
            Path.Combine(PostgresTenancyFixture.RepoMigrationsPath, MigrationFile),
            TestContext.Current.CancellationToken);

    // ------------------------------------------------------------------ (a) migration: RLS flags and shape

    [Fact]
    public async Task Run_AndPlannedExecution_HaveForcedRls()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        var database = owned.Name;

        foreach (var table in new[] { "run", "planned_execution" })
        {
            (await _fixture.TableExistsAsync(database, table)).ShouldBeTrue(table);
            (await _fixture.RowSecurityAsync(database, table)).ShouldBe((true, true));
        }
    }

    [Fact]
    public async Task TenantPolicies_AreScopedToTheAppRole()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        var database = owned.Name;

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
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        var database = owned.Name;

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
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        var database = owned.Name;

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
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        var database = owned.Name;

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
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        var database = owned.Name;

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
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        var database = owned.Name;

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
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        var database = owned.Name;

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

    // ------------------------------------------------------------------ (c) cross-org composite FKs

    [Fact]
    public async Task Run_Manifest_OtherOrg_ViolatesFk()
    {
        await using var world = await StartAsync();
        var mine = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "beta");
        var theirs = await SeedParentsAsync(world.Database, otherOrg, otherProject);

        // The row is filed under the other org but points at this org's manifest: only the composite
        // key can reject it (the RLS with check passes because the tenant setting is the other org).
        var error = await CaptureAppErrorAsync(
            world, otherOrg, InsertRunSql(otherOrg, otherProject, theirs, manifestId: mine.ManifestId));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("run_manifest_fk");
    }

    [Fact]
    public async Task PlannedExecution_Run_OtherOrg_ViolatesFk()
    {
        await using var world = await StartAsync();
        var mine = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var myRun = await InsertRunAsync(world, mine);
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "beta");
        var theirs = await SeedParentsAsync(world.Database, otherOrg, otherProject);

        var error = await CaptureAppErrorAsync(
            world, otherOrg, InsertPlannedExecutionSql(otherOrg, myRun, theirs.TestRevisionId));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("planned_execution_run_fk");
    }

    [Fact]
    public async Task PlannedExecution_CaseRevision_OtherOrg_ViolatesFk()
    {
        await using var world = await StartAsync();
        var mine = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "beta");
        var theirs = await SeedParentsAsync(world.Database, otherOrg, otherProject);
        var theirRun = await InsertRunAsync(world, otherOrg, otherProject, theirs);

        var error = await CaptureAppErrorAsync(
            world, otherOrg, InsertPlannedExecutionSql(otherOrg, theirRun, mine.TestRevisionId));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("planned_execution_case_revision_fk");
    }

    [Fact]
    public async Task RunId_OtherOrg_ViolatesFk()
    {
        // 06.1.1 §9: the artifact's deferred composite run key, added by 0031.
        await using var world = await StartAsync();
        var mine = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var myRun = await InsertRunAsync(world, mine);
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "beta");

        var error = await CaptureAppErrorAsync(world, otherOrg, InsertArtifactSql(otherOrg, otherProject, myRun));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("artifact_run_fk");
    }

    [Fact]
    public async Task ProducerSlot_RunId_OtherOrg_ViolatesFk()
    {
        // 04.1.2-T4's deferred composite run key, added by 0031.
        await using var world = await StartAsync();
        var mine = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var myRun = await InsertRunAsync(world, mine);
        var (otherOrg, _) = await CreateOtherTenantAsync(world, "beta");

        var error = await CaptureAppErrorAsync(world, otherOrg, InsertProducerSlotSql(otherOrg, myRun));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("producer_slot_run_fk");
    }

    // ------------------------------------------------------------------ (d) run CHECK boundaries

    [Theory]
    [InlineData(-1)]
    [InlineData(525601)]
    public async Task LateWindowMinutes_OutsideWidenedBounds_FailsCheck(int minutes)
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertRunSql(world.Org, world.Project, parents, lateWindowMinutes: minutes));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("run_late_window_minutes_check");
    }

    // 0033 widens the bound to 0..525600 (owner decision Q4): both ends are accepted values, not refusals.
    [Theory]
    [InlineData(0)]
    [InlineData(525600)]
    public async Task LateWindowMinutes_WidenedBounds_AreAccepted(int minutes)
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(InsertRunSql(world.Org, world.Project, parents, lateWindowMinutes: minutes))).ShouldBe(1);
            await scope.CommitAsync();
        }

        (await _fixture.ScalarAsync<int>(
            world.Database,
            $"select late_window_minutes from public.run where org_id = '{world.Org}'")).ShouldBe(minutes);
    }

    [Fact]
    public async Task State_Unknown_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertRunSql(world.Org, world.Project, parents, state: "paused"));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("run_state_check");
    }

    [Fact]
    public async Task RerunScope_Unknown_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertRunSql(world.Org, world.Project, parents, rerunScope: "everything"));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("run_rerun_scope_check");
    }

    [Fact]
    public async Task Purpose_ReplayWithoutGrant_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertRunSql(world.Org, world.Project, parents, purpose: "replay"));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("run_replay_grant_check");
    }

    [Fact]
    public async Task Purpose_Unknown_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertRunSql(world.Org, world.Project, parents, purpose: "adhoc"));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("run_purpose_check");
    }

    [Fact]
    public async Task TestCommit_TooLong_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertRunSql(world.Org, world.Project, parents, testCommit: new string('a', 129)));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("run_test_commit_check");
    }

    [Theory]
    [InlineData("{\"provider\":\"github_actions\",\"repository_id\":1,\"run_id\":2}")]
    [InlineData("{\"provider\":\"github_actions\",\"repository_id\":1,\"run_id\":2,\"run_attempt\":1,\"extra\":true}")]
    [InlineData("[1,2,3]")]
    public async Task CiRunRef_OutsideTheExactShape_FailsCheck(string ciRunRef)
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertRunSql(world.Org, world.Project, parents, ciRunRef: ciRunRef));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("run_ci_run_ref_check");
    }

    [Fact]
    public async Task CiRunRef_ExactShape_IsAccepted()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(InsertRunSql(
            world.Org,
            world.Project,
            parents,
            ciRunRef: "{\"provider\":\"github_actions\",\"repository_id\":1,\"run_id\":2,\"run_attempt\":1}"))).ShouldBe(1);
        await scope.CommitAsync();
    }

    [Fact]
    public async Task Components_NotAnObject_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertRunSql(world.Org, world.Project, parents, components: "'[]'::jsonb"));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("run_components_check");
    }

    // ------------------------------------------------------------------ (e) planned_execution CHECK boundaries

    [Theory]
    [InlineData("PLACEHOLDER_EXECUTION_KEY")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("../../etc/passwd")]
    public async Task ExecutionKey_OutsideTheHexShape_FailsCheck(string executionKey)
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(world, parents);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertPlannedExecutionSql(world.Org, runId, parents.TestRevisionId, executionKey: executionKey));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("planned_execution_execution_key_check");
    }

    [Theory]
    [InlineData("sha256:short")]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public async Task VariantHash_OutsideTheContentAddress_FailsCheck(string variantHash)
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(world, parents);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertPlannedExecutionSql(world.Org, runId, parents.TestRevisionId, variantHash: variantHash));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("planned_execution_variant_hash_check");
    }

    [Theory]
    [InlineData("shard-0")]
    [InlineData("shard-")]
    [InlineData("1")]
    public async Task ShardId_OutsideTheDerivedShape_FailsCheck(string shardId)
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(world, parents);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertPlannedExecutionSql(world.Org, runId, parents.TestRevisionId, shardId: shardId));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("planned_execution_shard_id_check");
    }

    [Fact]
    public async Task TargetEnv_TooLong_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(world, parents);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertPlannedExecutionSql(world.Org, runId, parents.TestRevisionId, targetEnv: new string('e', 65)));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("planned_execution_target_env_check");
    }

    [Fact]
    public async Task SameRun_ExecutionKey_IsUnique()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await InsertRunAsync(world, parents);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(InsertPlannedExecutionSql(world.Org, runId, parents.TestRevisionId))).ShouldBe(1);
            await scope.CommitAsync();
        }

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertPlannedExecutionSql(world.Org, runId, parents.TestRevisionId));

        error.SqlState.ShouldBe("23505");
        error.ConstraintName.ShouldBe("planned_execution_run_execution_key");
    }

    // ------------------------------------------------------------------ (f) the migration re-run

    [Fact]
    public async Task Migration_ReRun_KeepsAValidArtifactRow()
    {
        await using var world = await StartAsync();
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
        await using var world = await StartAsync();

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
