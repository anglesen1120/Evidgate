using System.Globalization;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.3.3-T1.1/T1.2 through a real PostgreSQL: migration 0032's <c>case_attempt</c> (FORCE RLS, the
/// tenant keys, the composite tenant FKs, the app-role grants, the bound CHECKs and the two covering
/// indexes) plus the deferred composite attempt key on <c>artifact</c> (0028).
/// <para>
/// The fixture is this project's own <see cref="PostgresTenancyFixture"/> — one digest-pinned
/// PostgreSQL 18 container per assembly and one migrated database for the run (tests isolate by tenant);
/// only the plan-reading and migration re-run cases copy a private database.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class CaseAttemptSchemaTests
{
    /// <summary>The migration under test, re-run by the existing-row-safety cases.</summary>
    private const string MigrationFile = "0032_orchestration_case_attempt.sql";

    /// <summary>A well-formed execution key: the 00 §7 sha256 hex.</summary>
    private const string ExecutionKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>A second well-formed execution key, so two runs can hold different planned keys.</summary>
    private const string OtherExecutionKey = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    /// <summary>A well-formed variant hash, <c>sha256:&lt;64 hex&gt;</c> (03.2.1).</summary>
    private const string VariantHash = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly PostgresTenancyFixture _fixture;

    public CaseAttemptSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        DedicatedDatabase? Owned = null) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => WorldDisposal.DisposeAsync(Client, Owned, Api);
    }

    /// <summary>The parent rows a <c>case_attempt</c> row needs, seeded as the migration role.</summary>
    private sealed record Parents(Guid SuiteRevisionId, Guid EnvironmentRevisionId, Guid ManifestId, Guid CaseId, Guid TestRevisionId);

    /// <summary>An org and a project on the shared database, created through the real registry routes.</summary>
    /// <param name="dedicated">A private database for the EXPLAIN and migration re-run cases; the world drops it.</param>
    private async Task<World> StartAsync(DedicatedDatabase? dedicated = null)
    {
        var database = dedicated?.Name ?? await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project, dedicated);
    }

    /// <summary>An org and a project in it, created through the real routes.</summary>
    private static async Task<(Guid Org, Guid Project)> CreateOtherTenantAsync(World world, string slug)
    {
        var user = Guid.CreateVersion7();
        var unique = Unique.Slug(slug);
        var org = await CreateOrgAsync(world.Client, user, unique);
        var project = await CreateProjectAsync(world.Client, user, org, unique + "-project");

        return (org, project);
    }

    /// <summary>
    /// The manifest/suite/environment/test-revision parents of one <c>run</c>, written as the
    /// migration role (the owner backfill policies of 0004/0005 permit it) so a test can insert the
    /// rows under test as the app role.
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

        return new Parents(suiteRevisionId, environmentRevisionId, manifestId, caseId, testRevisionId);
    }

    /// <summary>One raw insert of a run row as the app role.</summary>
    private static string InsertRunSql(Guid org, Guid project, Parents parents, Guid id) =>
        "insert into public.run "
        + "(id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id, collection_deadline_at, late_window_minutes) "
        + $"values ('{id}', '{org}', '{project}', '{parents.ManifestId}', '{parents.SuiteRevisionId}', '{parents.EnvironmentRevisionId}', now() + interval '2 hours', 1440)";

    /// <summary>One raw insert of a planned-execution row as the app role.</summary>
    private static string InsertPlannedExecutionSql(Guid org, Guid runId, Guid caseRevisionId, string executionKey = ExecutionKey) =>
        "insert into public.planned_execution "
        + "(id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id) "
        + $"values ('{Guid.CreateVersion7()}', '{org}', '{runId}', '{executionKey}', '{caseRevisionId}', '{VariantHash}', 'staging', 'shard-1')";

    /// <summary>One raw insert of a case-attempt row as the app role; every bounded column is overridable.</summary>
    private static string InsertCaseAttemptSql(
        Guid org,
        Guid runId,
        Guid caseId,
        Guid? id = null,
        string executionKey = ExecutionKey,
        int attemptNo = 1,
        string rawStatus = "passed",
        string? normalizedStatus = null,
        int? durationMs = null,
        string? startedAt = "now()",
        string? finishedAt = null,
        Guid? attemptId = null) =>
        "insert into public.case_attempt "
        + "(id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status, started_at, finished_at, duration_ms) "
        + $"values ('{id ?? Guid.CreateVersion7()}', '{org}', '{runId}', '{executionKey}', '{caseId}', {attemptNo}, "
        + $"'{rawStatus}', '{normalizedStatus ?? CaseAttemptStatus.Normalize(rawStatus)}', "
        + $"{(startedAt is null ? "null" : startedAt)}, {(finishedAt is null ? "null" : finishedAt)}, "
        + $"{(durationMs is null ? "null" : durationMs.Value.ToString(CultureInfo.InvariantCulture))})";

    /// <summary>One raw insert of an artifact row as the app role, using the 06.1.2 staging key shape.</summary>
    private static string InsertArtifactSql(Guid org, Guid project, Guid runId, Guid? attemptId = null)
    {
        var id = Guid.CreateVersion7();

        return "insert into public.artifact "
            + "(id, org_id, project_id, run_id, execution_key, attempt_no, attempt_id, kind, object_key, media_type, checksum, sensitivity) "
            + $"values ('{id}', '{org}', '{project}', '{runId}', '{ExecutionKey}', 1, "
            + $"{(attemptId is null ? "null" : $"'{attemptId}'")}, 'trace', 'pending/{org}/{id}', 'application/zip', '{VariantHash}', 'normal')";
    }

    /// <summary>A committed run plus its planned execution for <paramref name="world"/>'s own org.</summary>
    private Task<Guid> SeedRunAndPlanAsync(World world, Parents parents, string executionKey = ExecutionKey) =>
        SeedRunAndPlanAsync(world, world.Org, world.Project, parents, executionKey);

    /// <summary>
    /// A committed run plus its planned execution for one tenant, written as the app role under that
    /// tenant's own scope. <paramref name="parents"/> must be the parents seeded for
    /// <paramref name="org"/>/<paramref name="project"/>: the run's composite manifest key refuses a
    /// run that names another tenant's manifest/suite/environment.
    /// </summary>
    private async Task<Guid> SeedRunAndPlanAsync(World world, Guid org, Guid project, Parents parents, string executionKey = ExecutionKey)
    {
        var runId = Guid.CreateVersion7();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);
        (await scope.ExecuteAsync(InsertRunSql(org, project, parents, runId))).ShouldBe(1);
        (await scope.ExecuteAsync(InsertPlannedExecutionSql(org, runId, parents.TestRevisionId, executionKey))).ShouldBe(1);
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

    private static Task<string> MigrationSqlAsync() =>
        File.ReadAllTextAsync(
            Path.Combine(PostgresTenancyFixture.RepoMigrationsPath, MigrationFile),
            TestContext.Current.CancellationToken);

    // ------------------------------------------------------------------ (a) migration: RLS flags and shape

    [Fact]
    public async Task CaseAttempt_HasForcedRls()
    {
        var database = await _fixture.SharedDatabaseAsync();

        (await _fixture.TableExistsAsync(database, "case_attempt")).ShouldBeTrue();
        (await _fixture.RowSecurityAsync(database, "case_attempt")).ShouldBe((true, true));
    }

    [Fact]
    public async Task TenantPolicies_AreScopedToTheAppRole()
    {
        var database = await _fixture.SharedDatabaseAsync();

        // A policy with no `to` clause applies to PUBLIC; the tenant policy must name evidgate_app
        // only, so the operator's SELECT grant reads zero rows (T76).
        (await _fixture.PoliciesAsync(database, "case_attempt")).ShouldBe(
        [
            "case_attempt_owner_backfill:evidgate_owner:*:true:true",
            "case_attempt_tenant:evidgate_app:*:(org_id = evidgate.current_org_id()):(org_id = evidgate.current_org_id())",
        ]);
    }

    [Fact]
    public async Task CaseAttempt_Constraints_Exist()
    {
        var database = await _fixture.SharedDatabaseAsync();

        var constraints = await _fixture.ConstraintNamesAsync(database, "case_attempt");

        foreach (var name in new[]
                 {
                     "case_attempt_pkey", "case_attempt_org_id_key", "case_attempt_run_execution_attempt_key",
                     "case_attempt_run_fk", "case_attempt_planned_execution_fk", "case_attempt_case_fk",
                     "case_attempt_execution_key_check", "case_attempt_attempt_no_check",
                     "case_attempt_raw_status_check", "case_attempt_normalized_status_check",
                     "case_attempt_duration_ms_check", "case_attempt_finished_after_started_check",
                 })
        {
            constraints.Any(entry => entry.EndsWith(":" + name, StringComparison.Ordinal)).ShouldBeTrue(name);
        }
    }

    [Fact]
    public async Task CaseAttemptIndexes_ExistInPgIndexes()
    {
        var database = await _fixture.SharedDatabaseAsync();

        var indexes = await _fixture.IndexesAsync(database, "case_attempt");
        indexes.Any(index => index.StartsWith("case_attempt_run_status_idx:", StringComparison.Ordinal)).ShouldBeTrue();
        indexes.Any(index => index.StartsWith("case_attempt_case_started_idx:", StringComparison.Ordinal)).ShouldBeTrue();

        // The unique key's own index already serves (org_id, run_id, execution_key, attempt_no) joins.
        indexes.Any(index => index.StartsWith("case_attempt_run_execution_attempt_key:", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public async Task ArtifactAttemptKey_ExistsAndIsValidated()
    {
        var database = await _fixture.SharedDatabaseAsync();

        // The key 0028 deferred: added `not valid`, orphan-checked, then validated.
        (await _fixture.ScalarAsync<bool>(
            database,
            "select convalidated from pg_catalog.pg_constraint where conrelid = 'public.artifact'::regclass and conname = 'artifact_attempt_fk'"))
            .ShouldBeTrue();
    }

    // ------------------------------------------------------------------ (b) grants

    [Fact]
    public async Task AppRole_HasOnlySelectInsert_OnCaseAttempt()
    {
        var database = await _fixture.SharedDatabaseAsync();

        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "case_attempt", "select")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "case_attempt", "insert")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "case_attempt", "update")).ShouldBeFalse();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "case_attempt", "delete")).ShouldBeFalse();

        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_operator", "case_attempt", "select")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_worker_sched", "case_attempt", "select")).ShouldBeFalse();
    }

    [Fact]
    public async Task CaseAttempt_UpdateDelete_Revoked()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(InsertCaseAttemptSql(world.Org, runId, parents.CaseId))).ShouldBe(1);
            await scope.CommitAsync();
        }

        (await CaptureAppErrorAsync(world, world.Org, "update public.case_attempt set normalized_status = 'failed'")).SqlState.ShouldBe("42501");
        (await CaptureAppErrorAsync(world, world.Org, "delete from public.case_attempt")).SqlState.ShouldBe("42501");
    }

    // ------------------------------------------------------------------ (c) cross-org composite FKs

    [Fact]
    public async Task CaseAttempt_CrossTenant_404()
    {
        await using var world = await StartAsync();
        var mine = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var myRun = await SeedRunAndPlanAsync(world, mine);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(InsertCaseAttemptSql(world.Org, myRun, mine.CaseId))).ShouldBe(1);
            await scope.CommitAsync();
        }

        // Under org B's tenant scope a SELECT of org A's attempt returns zero rows (RLS).
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "beta");
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), otherOrg))
        {
            (await scope.ScalarAsync<long>("select count(*) from public.case_attempt")).ShouldBe(0);
        }

        // Inserting an attempt for org A's run under org B's scope is refused — the RLS with check
        // passes because the tenant setting is org B, so the rejection is the composite run FK.
        var error = await CaptureAppErrorAsync(
            world, otherOrg, InsertCaseAttemptSql(otherOrg, myRun, mine.CaseId));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("case_attempt_run_fk");
    }

    [Fact]
    public async Task CaseAttempt_PlannedExecution_OtherOrg_ViolatesFk()
    {
        await using var world = await StartAsync();
        var mine = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var myRun = await SeedRunAndPlanAsync(world, mine);
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "beta");
        var theirs = await SeedParentsAsync(world.Database, otherOrg, otherProject);
        // Their run plans a DIFFERENT execution key, so the only key that can reject the row below is
        // the composite planned-execution key: (otherOrg, theirRun, ExecutionKey) has no planned row.
        var theirRun = await SeedRunAndPlanAsync(world, otherOrg, otherProject, theirs, OtherExecutionKey);

        // The row is filed under the other org but names this org's execution key on their run.
        var error = await CaptureAppErrorAsync(
            world, otherOrg, InsertCaseAttemptSql(otherOrg, theirRun, theirs.CaseId, executionKey: ExecutionKey));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("case_attempt_planned_execution_fk");

        // Positive control: the same row with the key their run did plan is accepted, so the refusal
        // above is the cross-org mismatch and not a bad seed.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), otherOrg))
        {
            (await scope.ExecuteAsync(InsertCaseAttemptSql(
                otherOrg, theirRun, theirs.CaseId, executionKey: OtherExecutionKey))).ShouldBe(1);
            await scope.CommitAsync();
        }
    }

    [Fact]
    public async Task CaseAttempt_CaseId_OtherOrg_ViolatesFk()
    {
        await using var world = await StartAsync();
        var mine = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "beta");
        var theirs = await SeedParentsAsync(world.Database, otherOrg, otherProject);
        var theirRun = await SeedRunAndPlanAsync(world, otherOrg, otherProject, theirs);

        // The row is filed under the other org and names their run and their planned key, but points
        // at this org's case: only the composite case key can reject it.
        var error = await CaptureAppErrorAsync(
            world, otherOrg, InsertCaseAttemptSql(otherOrg, theirRun, mine.CaseId));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("case_attempt_case_fk");

        // Positive control: the same row naming their own case is accepted, so the refusal above is
        // the cross-org mismatch and not a bad seed.
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), otherOrg))
        {
            (await scope.ExecuteAsync(InsertCaseAttemptSql(otherOrg, theirRun, theirs.CaseId))).ShouldBe(1);
            await scope.CommitAsync();
        }
    }

    [Fact]
    public async Task Artifact_AttemptId_OtherOrg_ViolatesFk()
    {
        // 06.1.1-T1's deferred composite attempt key, added by 0032.
        await using var world = await StartAsync();
        var mine = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var myRun = await SeedRunAndPlanAsync(world, mine);
        var attemptId = Guid.CreateVersion7();

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(InsertCaseAttemptSql(world.Org, myRun, mine.CaseId, id: attemptId))).ShouldBe(1);
            await scope.CommitAsync();
        }

        var (otherOrg, otherProject) = await CreateOtherTenantAsync(world, "beta");
        var theirs = await SeedParentsAsync(world.Database, otherOrg, otherProject);
        var theirRun = await SeedRunAndPlanAsync(world, otherOrg, otherProject, theirs);

        // The artifact is filed under the other org and names their run, but points at this org's
        // attempt: only the composite attempt key can reject it.
        var error = await CaptureAppErrorAsync(
            world, otherOrg, InsertArtifactSql(otherOrg, otherProject, theirRun, attemptId));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("artifact_attempt_fk");

        // Positive control: an artifact under the other org naming their own attempt is accepted, so
        // the refusal above is the cross-org mismatch and not a bad seed.
        var theirAttemptId = Guid.CreateVersion7();
        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), otherOrg))
        {
            (await scope.ExecuteAsync(InsertCaseAttemptSql(otherOrg, theirRun, theirs.CaseId, id: theirAttemptId))).ShouldBe(1);
            (await scope.ExecuteAsync(InsertArtifactSql(otherOrg, otherProject, theirRun, theirAttemptId))).ShouldBe(1);
            await scope.CommitAsync();
        }
    }

    [Fact]
    public async Task Artifact_AttemptId_Null_IsAccepted()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(InsertArtifactSql(world.Org, world.Project, runId))).ShouldBe(1);
        await scope.CommitAsync();
    }

    // ------------------------------------------------------------------ (d) CHECK boundaries

    [Fact]
    public async Task NormalizedStatus_OutsideTheSix_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertCaseAttemptSql(world.Org, runId, parents.CaseId, normalizedStatus: "flaky"));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("case_attempt_normalized_status_check");
    }

    [Fact]
    public async Task AttemptNo_Zero_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertCaseAttemptSql(world.Org, runId, parents.CaseId, attemptNo: 0));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("case_attempt_attempt_no_check");
    }

    [Fact]
    public async Task RawStatus_TooLong_FailsCheck()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertCaseAttemptSql(world.Org, runId, parents.CaseId, rawStatus: new string('x', 65)));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe("case_attempt_raw_status_check");
    }

    [Fact]
    public async Task Duplicate_RunExecutionAttempt_FailsUnique()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(InsertCaseAttemptSql(world.Org, runId, parents.CaseId))).ShouldBe(1);
            await scope.CommitAsync();
        }

        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertCaseAttemptSql(world.Org, runId, parents.CaseId));

        error.SqlState.ShouldBe("23505");
        error.ConstraintName.ShouldBe("case_attempt_run_execution_attempt_key");
    }

    [Fact]
    public async Task UnplannedExecutionKey_FailsFk()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        // A key with no planned_execution row for this run: 04 refuses it with 422
        // unknown_execution_key, so the database key is the second line of defence.
        var error = await CaptureAppErrorAsync(
            world, world.Org, InsertCaseAttemptSql(world.Org, runId, parents.CaseId, executionKey: new string('b', 64)));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("case_attempt_planned_execution_fk");
    }

    // ------------------------------------------------------------------ (e) raw + normalised status

    [Fact]
    public async Task WriteAttempt_EmptyRawStatus_IsAcceptedAndNormalizedError()
    {
        await using var world = await StartAsync();
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        // The envelope allows an empty status (maxLength 64, no minLength), so the writer accepts it
        // and it normalises to 'error'.
        CaseAttemptStatus.Normalize(string.Empty).ShouldBe("error");

        await using var scope = await AttemptScope.BeginAsync(world.Api, world.Org);
        (await scope.WriteAsync(runId, new CaseAttemptInput(ExecutionKey, 1, string.Empty)))
            .ShouldHaveSingleItem().Status.ShouldBe(CaseAttemptWriteStatus.Accepted);
        await scope.CommitAsync();

        await using var read = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await read.ScalarAsync<string>(
            $"select raw_status from public.case_attempt where org_id = '{world.Org}'")).ShouldBe(string.Empty);
        (await read.ScalarAsync<string>(
            $"select normalized_status from public.case_attempt where org_id = '{world.Org}'")).ShouldBe("error");
    }

    // ------------------------------------------------------------------ (f) index coverage (AC6)

    [Fact]
    public async Task CaseAttempt_ListByRunAndStatus_UsesIndex()
    {
        // Dedicated: it seeds 40 rows, ANALYZEs and reads the plan with sequential scans disabled.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(dedicated);
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        await SeedAttemptsAsync(world, parents, runId, count: 40);

        var plan = await ExplainAsync(
            world,
            world.Org,
            $"select id from public.case_attempt where org_id = '{world.Org}' and run_id = '{runId}' and normalized_status = 'passed'");

        plan.ShouldContain("case_attempt_run_status_idx");
    }

    [Fact]
    public async Task CaseAttempt_HistoryByCaseAndTime_UsesIndex()
    {
        // Dedicated: it seeds 40 rows, ANALYZEs and reads the plan with sequential scans disabled.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(dedicated);
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        await SeedAttemptsAsync(world, parents, runId, count: 40);

        var plan = await ExplainAsync(
            world,
            world.Org,
            $"select id from public.case_attempt where org_id = '{world.Org}' and case_id = '{parents.CaseId}' order by started_at");

        plan.ShouldContain("case_attempt_case_started_idx");
    }

    /// <summary>Seeds <paramref name="count"/> attempts for one run, each a distinct attempt_no.</summary>
    private async Task SeedAttemptsAsync(World world, Parents parents, Guid runId, int count)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        await scope.ExecuteAsync(
            $"""
            insert into public.case_attempt
              (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status, started_at)
            select gen_random_uuid(), '{world.Org}', '{runId}', '{ExecutionKey}', '{parents.CaseId}', g,
                   'passed', 'passed', now() + g * interval '1 second'
              from generate_series(1, {count}) g;
            analyze public.case_attempt;
            """);

        await scope.CommitAsync();
    }

    /// <summary>
    /// The EXPLAIN (FORMAT JSON) plan of one query as the app role, with sequential scans disabled on
    /// this connection only so the planner must choose the covering index (the 07.3.1-T2 precedent).
    /// </summary>
    private async Task<string> ExplainAsync(World world, Guid org, string sql)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);
        await scope.SetAsync("enable_seqscan", "off");

        return await scope.ScalarAsync<string>($"explain (format json) {sql}");
    }

    // ------------------------------------------------------------------ (g) the migration re-run

    [Fact]
    public async Task Migration_ReRun_KeepsAValidAttemptRow()
    {
        // Dedicated: it re-applies the migration and counts the table whole, so it owns the database.
        await using var dedicated = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(dedicated);
        var parents = await SeedParentsAsync(world.Database, world.Org, world.Project);
        var runId = await SeedRunAndPlanAsync(world, parents);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(InsertCaseAttemptSql(world.Org, runId, parents.CaseId))).ShouldBe(1);
            await scope.CommitAsync();
        }

        // The file is idempotent: the table, policies, grants and the deferred key already exist, the
        // orphan check counts zero and `validate constraint` is a no-op — the row survives.
        await ReRunMigrationAsync(world.Database);

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.case_attempt")).ShouldBe(1);
        (await _fixture.ScalarAsync<bool>(
            world.Database,
            "select convalidated from pg_catalog.pg_constraint where conrelid = 'public.artifact'::regclass and conname = 'artifact_attempt_fk'"))
            .ShouldBeTrue();
    }
}
