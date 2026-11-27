using System.Diagnostics;
using Evidgate.Migrator;
using Npgsql;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary>
/// The GitHub App half of <c>0137_github_app_removal.sql</c>: the installation store, the repository binding, the
/// inbound webhook ledger and the catalog sync queue are gone together with their definers and grants, while the
/// login identity rows, the catalog suites, ingest-token grants and audit history stay; the unused CI permission is retired.
/// </summary>
[Collection(MigratorPostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class GithubAppRemovalMigrationTests(PostgresMigratorFixture fixture)
{
    private const string FileName = "0137_github_app_removal.sql";
    private const string InstallationResolver = "evidgate_resolve.installation_by_id(bigint)";
    private const string StatePurger = "evidgate.purge_expired_github_install_state(integer)";
    private const string Org = "01980000-0000-7000-8000-000000000101";
    private const string Project = "01980000-0000-7000-8000-000000000102";
    private const string User = "01980000-0000-7000-8000-000000000103";
    private const string Suite = "01980000-0000-7000-8000-000000000104";
    private const string Token = "01980000-0000-7000-8000-000000000106";
    private const string Permissions = """{"contents":"read"}""";
    private const string Binding = "01980000-0000-7000-8000-000000000105";
    private const string SweepHistory = """
        select coalesce(jsonb_agg(to_jsonb(a) order by a.id), '[]'::jsonb)::text from public.platform_sweep_audit a
         where a.action in ('github_installation.resolve', 'github_install_state.purge')
        """;
    // Matches a relation reference, not the bare word: the outbound replay definer writes the audit literal
    // 'webhook_delivery.replay' and must not count.
    private const string RetiredDefinerScan = """
        select count(*) from pg_proc p join pg_namespace n on n.oid = p.pronamespace
         where p.prosecdef and n.nspname in ('public', 'evidgate', 'evidgate_resolve')
           and pg_get_functiondef(p.oid) ~* '\m(from|join|into|update|table|only)\s+(public\.)?(github_installation|github_install_state|git_repo_binding|catalog_sync|webhook_delivery)\M'
        """;

    private static readonly string[] RetiredTables =
    [
        "catalog_sync", "git_repo_binding", "webhook_delivery", "github_install_state", "github_installation",
    ];

    private static readonly string[] RetiredIndexes =
    [
        "catalog_sync_list", "catalog_sync_binding", "github_install_state_expires_at_idx",
        "github_install_state_user_idx", "github_installation_installed_by_idx", "github_installation_active_org_key",
    ];

    private static readonly string[] RetiredFunctions =
    [
        InstallationResolver, StatePurger, "public.catalog_sync_guard()", "public.git_repo_binding_guard()",
        "public.webhook_delivery_guard()", "public.github_install_state_guard()", "public.github_installation_guard()",
    ];

    private static readonly string[] SurvivingTables =
    [
        "organization", "project", "user_identity", "ingest_token", "suite", "platform_sweep_audit", "webhook_endpoint",
        "webhook_outbound_delivery",
    ];

    [Fact]
    public async Task Upgrade_WithPopulatedGithubTables_RollsBackOnContentionThenDropsThemAndKeepsLoginAndCatalog()
    {
        using var migrations = TempMigrations.Create();
        var database = await SeedPreviousHeadAsync(migrations);
        var owner = fixture.OwnerConnectionString(database);
        await using var connection = await fixture.OpenSuperuserAsync(database);
        var survivors = await ReadSurvivorsAsync(connection);
        var sweepHistory = await fixture.ScalarAsync<string>(database, SweepHistory);
        sweepHistory.ShouldContain("github_installation.resolve", Case.Sensitive, "seeded precondition: sweep history exists");
        migrations.CopyReal(FileName);

        await AssertBoundedRollbackAsync(database, migrations, owner);
        var applied = MigratorHarness.Run(migrations.Path, owner);
        applied.ExitCode.ShouldBe(MigratorExitCodes.Success, applied.Transcript);
        applied.Result.AppliedNow.ShouldBe([FileName]);
        await AssertRetiredAsync(database);
        await AssertIngestRoleProbesAsync(connection);
        (await ReadSurvivorsAsync(connection)).ShouldBe(survivors);
        (await fixture.ScalarAsync<string>(database, SweepHistory)).ShouldBe(sweepHistory);

        var repeated = MigratorHarness.Run(migrations.Path, owner);
        repeated.ExitCode.ShouldBe(MigratorExitCodes.Success, repeated.Transcript);
        repeated.Result.AppliedNow.ShouldBeEmpty();
    }

    [Fact]
    public async Task Migration_DirectSqlReplay_LeavesRetiredObjectsAbsentAndSurvivorsUntouched()
    {
        using var migrations = TempMigrations.Create();
        var database = await SeedPreviousHeadAsync(migrations);
        await using var connection = await fixture.OpenSuperuserAsync(database);
        var survivors = await ReadSurvivorsAsync(connection);
        migrations.CopyReal(FileName);
        var applied = MigratorHarness.Run(migrations.Path, fixture.OwnerConnectionString(database));
        applied.ExitCode.ShouldBe(MigratorExitCodes.Success, applied.Transcript);
        await AssertRetiredAsync(database);
        (await ReadSurvivorsAsync(connection)).ShouldBe(survivors);
        await AssertIngestRoleProbesAsync(connection);

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
        await AssertIngestRoleProbesAsync(connection);
        (await ReadSurvivorsAsync(connection)).ShouldBe(survivors);
    }

    [Theory]
    [InlineData("webhook_delivery", 1L)]
    [InlineData("webhook_outbound_delivery", 0L)]
    public async Task RetiredDefinerScan_WithWebhookReference_MatchesOnlyTheRetiredRelation(string relation, long expected)
    {
        using var migrations = TempMigrations.Create();
        var database = await SeedPreviousHeadAsync(migrations);
        migrations.CopyReal(FileName);
        var applied = MigratorHarness.Run(migrations.Path, fixture.OwnerConnectionString(database));
        applied.ExitCode.ShouldBe(MigratorExitCodes.Success, applied.Transcript);
        await using var connection = await fixture.OpenSuperuserAsync(database);
        (await PostgresMigratorFixture.ScalarAsync<long>(connection, RetiredDefinerScan)).ShouldBe(0);

        await PostgresMigratorFixture.ExecuteAsync(connection, $"""
            create function public.retired_relation_scan_probe() returns bigint
            language plpgsql security definer set search_path = ''
            as $probe$
            begin
                return (select count(*) from public.{relation});
            end
            $probe$;
            """);

        (await PostgresMigratorFixture.ScalarAsync<long>(connection, RetiredDefinerScan)).ShouldBe(expected);
    }

    private async Task<string> SeedPreviousHeadAsync(TempMigrations migrations)
    {
        var database = await fixture.CreateDatabaseAsync();
        migrations.CopyReal([.. PostgresMigratorFixture.RealMigrationFileNames.Where(name => string.CompareOrdinal(name, FileName) < 0)]);
        var before = MigratorHarness.Run(migrations.Path, fixture.OwnerConnectionString(database));
        before.ExitCode.ShouldBe(MigratorExitCodes.Success, before.Transcript);
        await using var connection = await fixture.OpenSuperuserAsync(database);
        await SeedAsync(connection);
        (await fixture.ScalarAsync<long>(
            database, "select count(*) from public.permission where code = 'ci_binding:write'")).ShouldBe(1);
        (await fixture.ScalarAsync<long>(
            database, $"select count(*) from public.role_permission where org_id = '{Org}' and permission_code = 'ci_binding:write'")).ShouldBe(1);
        (await fixture.ScalarAsync<long>(
            database, "select count(*) from public.role_permission where org_id is null and permission_code = 'ci_binding:write'")).ShouldBeGreaterThan(0);
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
            ("ingest_token", $"org_id = '{Org}'"),
            ("suite", $"org_id = '{Org}'"),
            ("role", $"org_id = '{Org}'"),
            ("role_permission", "permission_code = 'ingest_token:write'"),
            ("audit_log", $"org_id = '{Org}' and action = 'ci_binding.create'"),
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
        insert into public.ingest_token (id, org_id, project_id, name, prefix, token_hmac, pepper_version, scopes)
        values ('{Token}', '{Org}', '{Project}', 'survivor', 'AAAAAAAAAAAA', decode(repeat('01', 32), 'hex'), 1, array['run:create']);
        insert into public.role (id, org_id, code, name, is_builtin, kind)
        values (gen_random_uuid(), '{Org}', 'installer', 'Installer', false, 'member');
        insert into public.role_permission (id, org_id, role_id, permission_code)
        select gen_random_uuid(), r.org_id, r.id, p.code
          from public.role r
          cross join (values ('ci_binding:write'), ('ingest_token:write')) p(code)
         where r.org_id = '{Org}' and r.code = 'installer';
        insert into public.audit_log (id, org_id, seq, actor_type, actor_id, action, prev_hash, entry_hash)
        values (gen_random_uuid(), '{Org}', 1, 'system', 'upgrade', 'ci_binding.create',
            decode(repeat('00', 32), 'hex'), decode(repeat('01', 32), 'hex'));
        insert into public.suite (id, org_id, project_id, name, slug)
        values ('{Suite}', '{Org}', '{Project}', 'survivor', 'survivor');
        insert into public.github_installation (id, org_id, installation_id, account_login, permissions, installed_by)
        values (gen_random_uuid(), '{Org}', 4242, 'acme', '{Permissions}', '{User}');
        insert into public.github_install_state (state_hash, org_id, user_id, expires_at)
        values (decode(repeat('ab', 32), 'hex'), '{Org}', '{User}', now() + interval '1 hour');
        insert into public.git_repo_binding (id, org_id, project_id, installation_id, repository_id, webhook_secret_ref)
        values ('{Binding}', '{Org}', '{Project}', '4242', '7', 'secret:repo');
        insert into public.catalog_sync (id, org_id, project_id, commit_sha, binding_id)
        values (gen_random_uuid(), '{Org}', '{Project}', 'a1b2c3d4', '{Binding}');
        insert into public.webhook_delivery (id, org_id, provider, delivery_id, event, payload_hash)
        values (gen_random_uuid(), '{Org}', 'github', 'delivery-1', 'push', 'sha256:' || repeat('a', 64));
        insert into public.platform_sweep_audit (actor, action, removed, batch_limit, budget_exhausted)
        values ('evidgate_app', 'github_installation.resolve', 0, 1, false),
               ('evidgate_worker_sched', 'github_install_state.purge', 1, 100, false);
        """);

    private async Task AssertBoundedRollbackAsync(string database, TempMigrations migrations, string owner)
    {
        await using var blocker = await fixture.OpenSuperuserAsync(database);
        var cancellation = TestContext.Current.CancellationToken;
        await using var transaction = await blocker.BeginTransactionAsync(cancellation);
        await PostgresMigratorFixture.ExecuteAsync(blocker, "lock table public.git_repo_binding in access share mode");
        var started = Stopwatch.StartNew();
        var blocked = MigratorHarness.Run(migrations.Path, owner, lockTimeoutSeconds: 30);
        blocked.ExitCode.ShouldBe(MigratorExitCodes.LockTimeout, blocked.Transcript);
        started.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
        await transaction.RollbackAsync(cancellation);
        (await fixture.ReadJournalAsync(database)).Select(row => row.Name).ShouldNotContain(FileName);
        (await fixture.ScalarAsync<long>(database, "select count(*) from public.git_repo_binding")).ShouldBe(1);
        (await fixture.ScalarAsync<long>(database, "select count(*) from public.github_installation")).ShouldBe(1);
        (await fixture.ScalarAsync<bool>(database, $"select has_function_privilege('evidgate_app', '{InstallationResolver}', 'execute')")).ShouldBeTrue();
        (await fixture.ScalarAsync<bool>(database, $"select has_function_privilege('evidgate_worker_sched', '{StatePurger}', 'execute')")).ShouldBeTrue();
    }

    private async Task AssertRetiredAsync(string database)
    {
        foreach (var relation in RetiredTables.Concat(RetiredIndexes))
        {
            (await fixture.ScalarAsync<bool>(database, $"select to_regclass('public.{relation}') is null")).ShouldBeTrue(relation);
        }

        foreach (var function in RetiredFunctions)
        {
            (await fixture.ScalarAsync<bool>(database, $"select to_regprocedure('{function}') is null")).ShouldBeTrue(function);
        }

        foreach (var table in SurvivingTables)
        {
            (await fixture.ScalarAsync<bool>(database, $"select to_regclass('public.{table}') is not null")).ShouldBeTrue(table);
        }

        (await fixture.ScalarAsync<long>(database, RetiredDefinerScan)).ShouldBe(0);
        (await fixture.ScalarAsync<long>(
            database, "select count(*) from public.permission where code = 'ci_binding:write'")).ShouldBe(0);
        (await fixture.ScalarAsync<long>(
            database, "select count(*) from public.role_permission where permission_code = 'ci_binding:write'")).ShouldBe(0);
        (await fixture.ScalarAsync<long>(
            database, "select count(*) from public.permission where code = 'ingest_token:write'")).ShouldBe(1);
    }

    private static async Task AssertIngestRoleProbesAsync(NpgsqlConnection connection)
    {
        const string ingest = $"""
            select count(*) from evidgate_resolve.resolve_ingest_token('AAAAAAAAAAAA')
             where org_id = '{Org}' and project_id = '{Project}' and binding_id = '{Token}' and status = 'active'
            """;
        try
        {
            await PostgresMigratorFixture.ExecuteAsync(connection, "set role evidgate_app");
            (await PostgresMigratorFixture.ScalarAsync<long>(connection, ingest)).ShouldBe(1);
            foreach (var role in new[] { "evidgate_worker", "evidgate_worker_sched" })
            {
                // Resolving the function name needs schema USAGE, which these roles lack: ask as the owner, then probe as the role.
                await PostgresMigratorFixture.ExecuteAsync(connection, "reset role");
                (await PostgresMigratorFixture.ScalarAsync<bool>(connection, $"""
                    select has_function_privilege('{role}', 'evidgate_resolve.resolve_ingest_token(text)', 'execute')
                    """)).ShouldBeFalse(role);
                await PostgresMigratorFixture.ExecuteAsync(connection, $"set role {role}");
                var denied = await Should.ThrowAsync<PostgresException>(
                    () => PostgresMigratorFixture.ScalarAsync<long>(connection, ingest));
                denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            }
        }
        finally
        {
            await PostgresMigratorFixture.ExecuteAsync(connection, "reset role");
        }
    }
}
