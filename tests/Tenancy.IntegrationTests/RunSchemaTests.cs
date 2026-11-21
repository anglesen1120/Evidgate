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
/// PostgreSQL 18 container per assembly and one database for the run (tests isolate by tenant), with a
/// private database only where a case re-applies the migration.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class RunSchemaTests : IClassFixture<TenancyHostPool>
{
    /// <summary>The migration under test, re-run by the existing-row-safety cases.</summary>
    private const string MigrationFile = "0031_orchestration_run.sql";

    /// <summary>A well-formed execution key: the 00 §7 sha256 hex.</summary>
    private const string ExecutionKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>A well-formed variant hash, <c>sha256:&lt;64 hex&gt;</c> (03.2.1).</summary>
    private const string VariantHash = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly PostgresTenancyFixture _fixture;
    private readonly TenancyHostPool _hosts;
    public RunSchemaTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) => (_fixture, _hosts) = (fixture, hosts);

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

    /// <summary>The parent rows a <c>run</c> row needs, seeded as the migration role.</summary>
    private sealed record Parents(Guid SuiteRevisionId, Guid EnvironmentRevisionId, Guid ManifestId, Guid TestRevisionId);

    /// <summary>An org and a project on the shared database, created through the real registry routes.</summary>
    /// <param name="dedicated">A private database for the cases that need one; the world drops it.</param>
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
}
