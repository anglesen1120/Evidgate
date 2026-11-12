using Evidgate.Migrator;
using Npgsql;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary>
/// The pre-checks of <c>0102</c> and <c>0103</c>: a database at head-minus-one that already holds a run whose manifest belongs to
/// another project stops the migration with the counts and rewrites nothing; once the row is repaired the same file
/// applies and leaves every run key validated.
/// </summary>
[Collection(MigratorPostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class RunProjectKeyMigrationTests
{
    private const string KeyFile = "0102_run_project_fk.sql";
    private const string SecondKeyFile = "0103_run_manifest_suite_parent_keys.sql";

    private readonly PostgresMigratorFixture _fixture;

    public RunProjectKeyMigrationTests(PostgresMigratorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task PreCheck_Raises_With_The_Counts_Leaves_The_Row_Untouched_Then_Applies_Once_Repaired()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await _fixture.CreateDatabaseAsync();

        using var migrations = TempMigrations.Create();
        migrations.CopyReal([.. PostgresMigratorFixture.RealMigrationFileNames.Where(name => string.CompareOrdinal(name, KeyFile) < 0)]);
        MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database))
            .ExitCode.ShouldBe(MigratorExitCodes.Success);

        var org = Guid.CreateVersion7();
        var projectA = Guid.CreateVersion7();
        var projectB = Guid.CreateVersion7();
        var suite = Guid.CreateVersion7();
        var suiteRevision = Guid.CreateVersion7();
        var environment = Guid.CreateVersion7();
        var environmentRevision = Guid.CreateVersion7();
        var manifestA = Guid.CreateVersion7();
        var manifestB = Guid.CreateVersion7();
        var run = Guid.CreateVersion7();
        var environmentB = Guid.CreateVersion7();
        var environmentRevisionB = Guid.CreateVersion7();
        await ExecuteAsync(
            database,
            """
            insert into public.organization (id, slug, name) values (@org, 'precheck', 'precheck');
            insert into public.project (id, org_id, name, slug) values (@a, @org, 'A', 'alpha'), (@b, @org, 'B', 'beta');
            insert into public.suite (id, org_id, project_id, name, slug) values (@suite, @org, @a, 'probe', 'probe-suite');
            insert into public.suite_revision (id, org_id, suite_id, revision_no, revision_hash, shard_strategy, retry_policy)
              values (@sr, @org, @suite, 1, 'sha256:' || repeat('a', 64), '{"kind":"none"}', '{"max_retries":0}');
            insert into public.environment (id, org_id, project_id, environment_key, name) values (@env, @org, @a, 'staging', 'Staging');
            insert into public.environment_revision (id, org_id, project_id, environment_id, revision_no, base_urls, dependency_versions, revision_hash, created_by)
              values (@er, @org, @a, @env, 1, '{}', '{}', 'sha256:' || repeat('c', 64), 'probe');
            insert into public.environment (id, org_id, project_id, environment_key, name) values (@envb, @org, @b, 'staging', 'Staging');
            insert into public.environment_revision (id, org_id, project_id, environment_id, revision_no, base_urls, dependency_versions, revision_hash, created_by)
              values (@erb, @org, @b, @envb, 1, '{}', '{}', 'sha256:' || repeat('c', 64), 'probe');
            insert into public.manifest (id, org_id, project_id, manifest_hash, schema_version, suite_revision_id, environment_revision_id, shard_plan, matrix, env_needs, planned_count, document, verified_by)
              values (@ma, @org, @a, 'sha256:' || repeat('d', 64), 1, @sr, @er, '{}', '{}', '{}', 0, '{}', 'probe'),
                     (@mb, @org, @b, 'sha256:' || repeat('e', 64), 1, @sr, @erb, '{}', '{}', '{}', 0, '{}', 'probe');
            insert into public.run (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id, collection_deadline_at, late_window_minutes)
              values (@run, @org, @a, @mb, @sr, @er, now() + interval '2 hours', 1440);
            """,
            [("org", org), ("a", projectA), ("b", projectB), ("suite", suite), ("sr", suiteRevision), ("env", environment),
             ("er", environmentRevision), ("envb", environmentB), ("erb", environmentRevisionB), ("ma", manifestA), ("mb", manifestB), ("run", run)],
            token);

        migrations.CopyReal(KeyFile);
        var refused = MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database));
        refused.ExitCode.ShouldBe(MigratorExitCodes.MigrationFailed);
        refused.Transcript.ShouldContain("manifest of another project 1");

        (await ScalarAsync(database, "select count(*) from public.run where id = @run and manifest_id = @mb", [("run", run), ("mb", manifestB)], token))
            .ShouldBe(1, "the pre-check never deletes or rewrites the violating row");
        (await ScalarAsync(database, "select count(*) from pg_constraint where conname = 'run_project_fk'", [], token))
            .ShouldBe(0, "the failed file rolled back with its keys");

        await ExecuteAsync(database, "update public.run set manifest_id = @ma where id = @run", [("ma", manifestA), ("run", run)], token);

        MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database))
            .ExitCode.ShouldBe(MigratorExitCodes.Success);
        (await ScalarAsync(
            database,
            "select count(*) from pg_constraint where conrelid = 'public.run'::regclass and convalidated "
            + "and conname in ('run_project_fk', 'run_manifest_fk', 'run_environment_revision_fk', 'run_policy_revision_fk')",
            [],
            token)).ShouldBe(4);
        (await ScalarAsync(database, "select count(*) from pg_trigger where tgname = 'run_suite_revision_project_guard'", [], token))
            .ShouldBe(1);

        // Raw re-execution of the file is a no-op: every statement is guarded or drops before it adds.
        await ExecuteAsync(database, await File.ReadAllTextAsync(Path.Combine(migrations.Path, KeyFile), token), [], token);
    }

    [Fact]
    public async Task SuiteAndParentPreCheck_Raises_With_The_Counts_Leaves_The_Rows_Untouched_Then_Applies_Once_Repaired()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await _fixture.CreateDatabaseAsync();

        using var migrations = TempMigrations.Create();
        migrations.CopyReal([.. PostgresMigratorFixture.RealMigrationFileNames.Where(name => string.CompareOrdinal(name, SecondKeyFile) < 0)]);
        MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database))
            .ExitCode.ShouldBe(MigratorExitCodes.Success);

        var ids = Enumerable.Range(0, 16).Select(_ => Guid.CreateVersion7()).ToArray();
        var (org, a, b, suiteA, suiteB, srA1, srA2, srB, envA, erA, envB, erB, manifestA, manifestB, runB, child) =
            (ids[0], ids[1], ids[2], ids[3], ids[4], ids[5], ids[6], ids[7], ids[8], ids[9], ids[10], ids[11], ids[12], ids[13], ids[14], ids[15]);
        var manifestOfB = Guid.CreateVersion7();
        var run1 = Guid.CreateVersion7();
        await ExecuteAsync(
            database,
            """
            insert into public.organization (id, slug, name) values (@org, 'precheck2', 'precheck2');
            insert into public.project (id, org_id, name, slug) values (@a, @org, 'A', 'alpha'), (@b, @org, 'B', 'beta');
            insert into public.suite (id, org_id, project_id, name, slug) values (@sa, @org, @a, 'a', 'suite-a'), (@sb, @org, @b, 'b', 'suite-b');
            insert into public.suite_revision (id, org_id, suite_id, revision_no, revision_hash, shard_strategy, retry_policy)
              values (@sra1, @org, @sa, 1, 'sha256:' || repeat('a', 64), '{"kind":"none"}', '{"max_retries":0}'),
                     (@sra2, @org, @sa, 2, 'sha256:' || repeat('b', 64), '{"kind":"none"}', '{"max_retries":0}'),
                     (@srb, @org, @sb, 1, 'sha256:' || repeat('c', 64), '{"kind":"none"}', '{"max_retries":0}');
            insert into public.environment (id, org_id, project_id, environment_key, name) values (@ea, @org, @a, 'staging', 'Staging'), (@eb, @org, @b, 'staging', 'Staging');
            insert into public.environment_revision (id, org_id, project_id, environment_id, revision_no, base_urls, dependency_versions, revision_hash, created_by)
              values (@era, @org, @a, @ea, 1, '{}', '{}', 'sha256:' || repeat('d', 64), 'probe'),
                     (@erb, @org, @b, @eb, 1, '{}', '{}', 'sha256:' || repeat('e', 64), 'probe');
            insert into public.manifest (id, org_id, project_id, manifest_hash, schema_version, suite_revision_id, environment_revision_id, shard_plan, matrix, env_needs, planned_count, document, verified_by)
              values (@ma, @org, @a, 'sha256:' || repeat('1', 64), 1, @sra1, @era, '{}', '{}', '{}', 0, '{}', 'probe'),
                     (@mb, @org, @b, 'sha256:' || repeat('2', 64), 1, @sra1, @erb, '{}', '{}', '{}', 0, '{}', 'probe'),
                     (@mob, @org, @b, 'sha256:' || repeat('3', 64), 1, @srb, @erb, '{}', '{}', '{}', 0, '{}', 'probe');
            insert into public.run (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id, collection_deadline_at, late_window_minutes)
              values (@run1, @org, @a, @ma, @sra2, @era, now() + interval '2 hours', 1440),
                     (@runb, @org, @b, @mob, @srb, @erb, now() + interval '2 hours', 1440);
            insert into public.run (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id, parent_run_id, collection_deadline_at, late_window_minutes)
              values (@child, @org, @a, @ma, @sra1, @era, @runb, now() + interval '2 hours', 1440);
            """,
            [("org", org), ("a", a), ("b", b), ("sa", suiteA), ("sb", suiteB), ("sra1", srA1), ("sra2", srA2), ("srb", srB), ("ea", envA),
             ("era", erA), ("eb", envB), ("erb", erB), ("ma", manifestA), ("mb", manifestB), ("mob", manifestOfB), ("runb", runB),
             ("child", child), ("run1", run1)],
            token);

        migrations.CopyReal(SecondKeyFile);
        var refused = MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database));
        refused.ExitCode.ShouldBe(MigratorExitCodes.MigrationFailed);
        refused.Transcript.ShouldContain("manifest with a suite revision of another project 1");
        refused.Transcript.ShouldContain("run whose suite revision differs from its manifest's 1");
        refused.Transcript.ShouldContain("run whose parent run is of another project 1");

        (await ScalarAsync(database, "select count(*) from public.run where id = @c and parent_run_id = @p", [("c", child), ("p", runB)], token))
            .ShouldBe(1, "the pre-check never deletes or rewrites the violating row");
        (await ScalarAsync(database, "select count(*) from pg_constraint where conname = 'run_manifest_suite_revision_fk'", [], token))
            .ShouldBe(0, "the failed file rolled back with its keys");

        // Manifests are append-only for every role, so the repair of the bad manifest has to lift that guard for this one statement.
        await ExecuteAsync(
            database,
            """
            alter table public.manifest disable trigger manifest_immutable;
            update public.manifest set suite_revision_id = @srb where id = @mb;
            alter table public.manifest enable trigger manifest_immutable;
            update public.run set suite_revision_id = @sra1 where id = @run1;
            update public.run set parent_run_id = null where id = @child;
            """,
            [("srb", srB), ("mb", manifestB), ("sra1", srA1), ("run1", run1), ("child", child)],
            token);

        MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database))
            .ExitCode.ShouldBe(MigratorExitCodes.Success);
        (await ScalarAsync(
            database,
            "select count(*) from pg_constraint where conrelid = 'public.run'::regclass and convalidated "
            + "and conname in ('run_parent_run_fk', 'run_manifest_suite_revision_fk')",
            [],
            token)).ShouldBe(2);
        (await ScalarAsync(database, "select count(*) from pg_trigger where tgname = 'manifest_suite_revision_project_guard'", [], token))
            .ShouldBe(1);

        await ExecuteAsync(database, await File.ReadAllTextAsync(Path.Combine(migrations.Path, SecondKeyFile), token), [], token);
    }

    private async Task ExecuteAsync(string database, string sql, (string Name, object Value)[] parameters, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(_fixture.OwnerConnectionString(database));
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(token);
    }

    private async Task<long> ScalarAsync(string database, string sql, (string Name, object Value)[] parameters, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(_fixture.OwnerConnectionString(database));
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (long)(await command.ExecuteScalarAsync(token))!;
    }
}
