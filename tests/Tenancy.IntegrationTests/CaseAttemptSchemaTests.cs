using System.Globalization;
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
public sealed partial class CaseAttemptSchemaTests : IClassFixture<TenancyHostPool>
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
    private readonly TenancyHostPool _hosts;
    public CaseAttemptSchemaTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

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
        var api = _hosts.RealResolver(database);
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
}
