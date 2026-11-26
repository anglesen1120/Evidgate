using System.Diagnostics;
using Evidgate.Migrator;
using Npgsql;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

[Collection(MigratorPostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class IdentityGithubRemovalMigrationTests(PostgresMigratorFixture fixture)
{
    private const string FileName = "0134_identity_github_removal.sql";
    private const string BindingResolver = "evidgate_resolve.resolve_ci_binding(text,bigint,bytea[])";
    private const string Purger = "evidgate.purge_expired_ci_token_issue(integer)";
    private const string Org = "01980000-0000-7000-8000-000000000001";
    private const string Project = "01980000-0000-7000-8000-000000000002";
    private const string Token = "01980000-0000-7000-8000-000000000003";
    private const string User = "01980000-0000-7000-8000-000000000004";
    private const string Suite = "01980000-0000-7000-8000-000000000005";
    private const string SuiteRevision = "01980000-0000-7000-8000-000000000006";
    private const string EnvironmentId = "01980000-0000-7000-8000-000000000007";
    private const string EnvironmentRevision = "01980000-0000-7000-8000-000000000008";
    private const string Manifest = "01980000-0000-7000-8000-000000000009";
    private const string Run = "01980000-0000-7000-8000-000000000010";
    private const string EmptyJson = "{}";
    private const string NoShards = """{"kind":"none"}""";
    private const string NoRetries = """{"max_retries":0}""";
    private const string SharedGrants = """
        select jsonb_agg(to_jsonb(rp) order by rp.id)::text from public.role_permission rp
         where rp.permission_code in ('ci_binding:write', 'ingest_token:write')
        """;
    private const string AuditHistory = "select to_jsonb(a)::text from public.audit_log a where a.action = 'ci_binding.create'";

    [Fact]
    public async Task Upgrade_WithPopulatedLegacyTables_RollsBackOnContentionThenPreservesIngestAndHistory()
    {
        using var migrations = TempMigrations.Create();
        var database = await SeedPreviousHeadAsync(migrations);
        var owner = fixture.OwnerConnectionString(database);
        await using var connection = await fixture.OpenSuperuserAsync(database);
        var survivors = await ReadSurvivorsAsync(connection);
        var grants = await fixture.ScalarAsync<string>(database, SharedGrants);
        var audit = await fixture.ScalarAsync<string>(database, AuditHistory);
        grants.ShouldContain("ci_binding:write", Case.Sensitive, "seeded precondition: the shared permission is held");
        grants.ShouldContain("ingest_token:write", Case.Sensitive, "seeded precondition: the built-in tenant role holds it");
        migrations.CopyReal(FileName);

        await AssertBoundedRollbackAsync(database, migrations, owner);
        var applied = MigratorHarness.Run(migrations.Path, owner);
        applied.ExitCode.ShouldBe(MigratorExitCodes.Success, applied.Transcript);
        applied.Result.AppliedNow.ShouldBe([FileName]);
        await AssertRetiredAsync(database);
        await AssertRoleProbesAsync(connection);
        (await fixture.ScalarAsync<string>(database, SharedGrants)).ShouldBe(grants);
        (await fixture.ScalarAsync<string>(database, AuditHistory)).ShouldBe(audit);
        (await ReadSurvivorsAsync(connection)).ShouldBe(survivors);
        (await fixture.ScalarAsync<long>(
            database,
            "select count(*) from public.permission where code in ('ci_binding:write', 'ingest_token:write')")).ShouldBe(2);

        var repeated = MigratorHarness.Run(migrations.Path, owner);
        repeated.ExitCode.ShouldBe(MigratorExitCodes.Success, repeated.Transcript);
        repeated.Result.AppliedNow.ShouldBeEmpty();
    }

    [Fact]
    public async Task Migration_DirectSqlReplay_PreservesIdentityRunEvidenceAndIngestResolver()
    {
        using var migrations = TempMigrations.Create();
        var database = await SeedPreviousHeadAsync(migrations);
        await using var connection = await fixture.OpenSuperuserAsync(database);
        var survivors = await ReadSurvivorsAsync(connection);
        migrations.CopyReal(FileName);
        var applied = MigratorHarness.Run(migrations.Path, fixture.OwnerConnectionString(database));
        applied.ExitCode.ShouldBe(MigratorExitCodes.Success, applied.Transcript);
        applied.Result.AppliedNow.ShouldBe([FileName]);
        (await ReadSurvivorsAsync(connection)).ShouldBe(survivors);
        await AssertRoleProbesAsync(connection);

        var cancellation = TestContext.Current.CancellationToken;
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString(database));
        await owner.OpenAsync(cancellation);
        await using var transaction = await owner.BeginTransactionAsync(cancellation);
        await using var replay = owner.CreateCommand();
        replay.Transaction = transaction;
        replay.CommandText = await File.ReadAllTextAsync(
            Path.Combine(PostgresMigratorFixture.RepoMigrationsPath, FileName), cancellation);
        await replay.ExecuteNonQueryAsync(cancellation);
        await transaction.CommitAsync(cancellation);

        await AssertRetiredAsync(database);
        await AssertRoleProbesAsync(connection);
        (await ReadSurvivorsAsync(connection)).ShouldBe(survivors);
    }

    private async Task<string> SeedPreviousHeadAsync(TempMigrations migrations)
    {
        var database = await fixture.CreateDatabaseAsync();
        migrations.CopyReal([.. PostgresMigratorFixture.RealMigrationFileNames.Where(name => string.CompareOrdinal(name, FileName) < 0)]);
        var before = MigratorHarness.Run(migrations.Path, fixture.OwnerConnectionString(database));
        before.ExitCode.ShouldBe(MigratorExitCodes.Success, before.Transcript);
        await using var connection = await fixture.OpenSuperuserAsync(database);
        await SeedAsync(connection);
        await SeedRunEvidenceAsync(connection);
        return database;
    }

    private static async Task<string[]> ReadSurvivorsAsync(NpgsqlConnection connection)
    {
        var rows = new List<string>();
        foreach (var (table, predicate) in new[]
        {
            ("organization", $"id = '{Org}'"),
            ("project", $"org_id = '{Org}'"),
            ("\"user\"", $"id = '{User}'"),
            ("user_identity", $"user_id = '{User}'"),
            ("membership", $"org_id = '{Org}'"),
            ("membership_role", $"org_id = '{Org}'"),
            ("role", $"org_id = '{Org}'"),
            ("role_permission", $"org_id = '{Org}'"),
            ("ingest_token", $"org_id = '{Org}'"),
            ("run", $"org_id = '{Org}'"),
            ("evidence_revision", $"org_id = '{Org}'"),
            ("audit_log", $"org_id = '{Org}'"),
        })
        {
            var snapshot = await PostgresMigratorFixture.ScalarAsync<string>(connection, $"""
                select coalesce(jsonb_agg(to_jsonb(r) order by r.id), '[]'::jsonb)::text
                  from public.{table} r where {predicate}
                """);
            snapshot.ShouldNotBe("[]", table);
            rows.Add(snapshot);
        }

        return rows.ToArray();
    }

    private static Task SeedAsync(NpgsqlConnection connection) => PostgresMigratorFixture.ExecuteAsync(connection, $"""
        insert into public.organization (id, name, slug) values ('{Org}', 'upgrade', 'upgrade');
        insert into public.project (id, org_id, name, slug) values ('{Project}', '{Org}', 'upgrade', 'upgrade');
        insert into public."user" (id, email, display_name) values ('{User}', 'survivor@example.com', 'Survivor');
        insert into public.user_identity (id, user_id, provider, subject, email_at_provider)
        values (gen_random_uuid(), '{User}', 'github', '42', 'survivor@example.com');
        insert into public.membership (id, org_id, user_id, invited_email, status)
        values (gen_random_uuid(), '{Org}', '{User}', 'survivor@example.com', 'active');
        insert into public.ci_binding (id, org_id, project_id, provider, repository_id, repository_full_name,
            allowed_ref_patterns, allowed_workflow_refs, status)
        values (gen_random_uuid(), '{Org}', '{Project}', 'github_actions', 42, 'org/repo', array['main'], array['job'], 'active');
        insert into public.ci_token_issue (id, org_id, project_id, binding_id, provider, jti, repository_id,
            repository_full_name, ref, sha, run_id, run_attempt, job_workflow_ref, expires_at)
        select gen_random_uuid(), org_id, project_id, id, provider, 'issued', repository_id, repository_full_name,
            'refs/heads/main', repeat('a', 40), 41, 1, 'job', now() + interval '1 hour' from public.ci_binding;
        insert into public.ingest_token (id, org_id, project_id, name, prefix, token_hmac, pepper_version, scopes)
        values ('{Token}', '{Org}', '{Project}', 'survivor', 'AAAAAAAAAAAA', decode(repeat('01', 32), 'hex'), 1, array['run:create']);
        insert into public.role (id, org_id, code, name, is_builtin, kind)
        values (gen_random_uuid(), '{Org}', 'installer', 'Installer', false, 'member');
        insert into public.role_permission (id, org_id, role_id, permission_code)
        select gen_random_uuid(), org_id, id, 'ci_binding:write' from public.role where org_id = '{Org}' and code = 'installer';
        insert into public.membership_role (id, org_id, membership_id, role_id)
        select gen_random_uuid(), m.org_id, m.id, r.id from public.membership m
        join public.role r on r.org_id = m.org_id and r.code = 'installer' where m.org_id = '{Org}';
        insert into public.audit_log (id, org_id, seq, actor_type, actor_id, action, prev_hash, entry_hash)
        values (gen_random_uuid(), '{Org}', 1, 'system', 'upgrade', 'ci_binding.create',
            decode(repeat('00', 32), 'hex'), decode(repeat('01', 32), 'hex'));
        """);

    private static Task SeedRunEvidenceAsync(NpgsqlConnection connection) => PostgresMigratorFixture.ExecuteAsync(connection, $"""
        insert into public.suite (id, org_id, project_id, name, slug)
        values ('{Suite}', '{Org}', '{Project}', 'survivor', 'survivor');
        insert into public.suite_revision (id, org_id, suite_id, revision_no, revision_hash, shard_strategy, retry_policy)
        values ('{SuiteRevision}', '{Org}', '{Suite}', 1, 'sha256:' || repeat('a', 64), '{NoShards}', '{NoRetries}');
        insert into public.environment (id, org_id, project_id, environment_key, name)
        values ('{EnvironmentId}', '{Org}', '{Project}', 'staging', 'Staging');
        insert into public.environment_revision (id, org_id, project_id, environment_id, revision_no,
            base_urls, dependency_versions, revision_hash, created_by)
        values ('{EnvironmentRevision}', '{Org}', '{Project}', '{EnvironmentId}', 1,
            '{EmptyJson}', '{EmptyJson}', 'sha256:' || repeat('b', 64), 'upgrade');
        insert into public.manifest (id, org_id, project_id, manifest_hash, schema_version, suite_revision_id,
            environment_revision_id, shard_plan, matrix, env_needs, planned_count, document, verified_by)
        values ('{Manifest}', '{Org}', '{Project}', 'sha256:' || repeat('c', 64), 1, '{SuiteRevision}',
            '{EnvironmentRevision}', '{EmptyJson}', '{EmptyJson}', '{EmptyJson}', 0, '{EmptyJson}', 'upgrade');
        insert into public.run (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id,
            collection_deadline_at, late_window_minutes, ref_kind, ref_name, unverified_ref)
        values ('{Run}', '{Org}', '{Project}', '{Manifest}', '{SuiteRevision}', '{EnvironmentRevision}',
            now() + interval '2 hours', 1440, 'branch', 'main', true);
        insert into public.evidence_revision (id, org_id, run_id, revision_no, completeness, missing,
            event_watermark, manifest_hash)
        values (gen_random_uuid(), '{Org}', '{Run}', 1, 'incomplete', array['missing_shard'],
            0, 'sha256:' || repeat('c', 64));
        """);

    private async Task AssertBoundedRollbackAsync(string database, TempMigrations migrations, string owner)
    {
        await using var blocker = await fixture.OpenSuperuserAsync(database);
        var cancellation = TestContext.Current.CancellationToken;
        await using var transaction = await blocker.BeginTransactionAsync(cancellation);
        await PostgresMigratorFixture.ExecuteAsync(blocker, "lock table public.ci_binding in access share mode");
        var started = Stopwatch.StartNew();
        var blocked = MigratorHarness.Run(migrations.Path, owner, lockTimeoutSeconds: 30);
        blocked.ExitCode.ShouldBe(MigratorExitCodes.LockTimeout, blocked.Transcript);
        started.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
        await transaction.RollbackAsync(cancellation);
        (await fixture.ReadJournalAsync(database)).Select(row => row.Name).ShouldNotContain(FileName);
        (await fixture.ScalarAsync<long>(database, "select count(*) from public.ci_token_issue")).ShouldBe(1);
        (await fixture.ScalarAsync<bool>(database, $"select has_function_privilege('evidgate_worker_sched', '{Purger}', 'execute')")).ShouldBeTrue();
        (await fixture.ScalarAsync<bool>(database, $"select has_function_privilege('evidgate_app', '{BindingResolver}', 'execute')")).ShouldBeTrue();
    }

    private async Task AssertRetiredAsync(string database)
    {
        foreach (var relation in new[] { "ci_binding", "ci_token_issue", "ci_token_issue_run_idx" })
        {
            (await fixture.ScalarAsync<bool>(database, $"select to_regclass('public.{relation}') is null")).ShouldBeTrue(relation);
        }

        foreach (var function in new[] { BindingResolver, Purger })
        {
            (await fixture.ScalarAsync<bool>(database, $"select to_regprocedure('{function}') is null")).ShouldBeTrue(function);
        }

        (await fixture.ScalarAsync<long>(database, """
            select count(*) from pg_proc p join pg_namespace n on n.oid = p.pronamespace
             where p.prosecdef and n.nspname in ('public', 'evidgate', 'evidgate_resolve')
               and pg_get_functiondef(p.oid) ~* '\m(ci_binding|ci_token_issue)\M'
            """)).ShouldBe(0);
    }

    private static async Task AssertRoleProbesAsync(NpgsqlConnection connection)
    {
        const string ingest = $"""
            select count(*) from evidgate_resolve.resolve_ingest_token('AAAAAAAAAAAA')
             where org_id = '{Org}' and project_id = '{Project}' and binding_id = '{Token}' and status = 'active'
            """;
        await PostgresMigratorFixture.ExecuteAsync(connection, "set role evidgate_app");
        (await PostgresMigratorFixture.ScalarAsync<long>(connection, ingest)).ShouldBe(1);
        await PostgresMigratorFixture.ExecuteAsync(connection, "reset role; set role evidgate_worker_sched");
        (await PostgresMigratorFixture.ScalarAsync<long>(
            connection, $"select count(*) from evidgate.audit_org_ids(null, 10) id where id = '{Org}'")).ShouldBe(1);
        var denied = await Should.ThrowAsync<PostgresException>(
            () => PostgresMigratorFixture.ScalarAsync<long>(connection, ingest));
        denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        await PostgresMigratorFixture.ExecuteAsync(connection, "reset role");
    }
}
