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
    private const string SharedGrants = """
        select jsonb_agg(to_jsonb(rp) order by rp.id)::text from public.role_permission rp
         where rp.permission_code in ('ci_binding:write', 'ingest_token:write')
        """;
    private const string AuditHistory = "select to_jsonb(a)::text from public.audit_log a where a.action = 'ci_binding.create'";

    [Fact]
    public async Task Upgrade_WithPopulatedLegacyTables_RollsBackOnContentionThenPreservesIngestAndHistory()
    {
        var database = await fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create();
        migrations.CopyReal([.. PostgresMigratorFixture.RealMigrationFileNames.Where(name => string.CompareOrdinal(name, FileName) < 0)]);
        var owner = fixture.OwnerConnectionString(database);
        var before = MigratorHarness.Run(migrations.Path, owner);
        before.ExitCode.ShouldBe(MigratorExitCodes.Success, before.Transcript);
        await using var connection = await fixture.OpenSuperuserAsync(database);
        await SeedAsync(connection);
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
        (await fixture.ScalarAsync<long>(
            database,
            "select count(*) from public.permission where code in ('ci_binding:write', 'ingest_token:write')")).ShouldBe(2);

        var repeated = MigratorHarness.Run(migrations.Path, owner);
        repeated.ExitCode.ShouldBe(MigratorExitCodes.Success, repeated.Transcript);
        repeated.Result.AppliedNow.ShouldBeEmpty();
    }

    private static Task SeedAsync(NpgsqlConnection connection) => PostgresMigratorFixture.ExecuteAsync(connection, $"""
        insert into public.organization (id, name, slug) values ('{Org}', 'upgrade', 'upgrade');
        insert into public.project (id, org_id, name, slug) values ('{Project}', '{Org}', 'upgrade', 'upgrade');
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
        insert into public.audit_log (id, org_id, seq, actor_type, actor_id, action, prev_hash, entry_hash)
        values (gen_random_uuid(), '{Org}', 1, 'system', 'upgrade', 'ci_binding.create',
            decode(repeat('00', 32), 'hex'), decode(repeat('01', 32), 'hex'));
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
