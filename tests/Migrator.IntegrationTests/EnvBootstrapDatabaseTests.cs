using System.Diagnostics;
using System.Globalization;
using Npgsql;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary><c>scripts/env/bootstrap-db.sh</c> against a real PostgreSQL 18: the
/// two superuser steps around the real migrations, the verifier and its refusals.
/// This host has no <c>psql</c>, so every run uses the script's container mode
/// against the fixture's container and passwords, which keeps a rerun from
/// changing the credential the other tests in this collection connect with.</summary>
[Collection(MigratorPostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class EnvBootstrapDatabaseTests
{
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(180);

    private static readonly string BootstrapScript =
        Path.Combine(RepoRoot, "scripts", "env", "bootstrap-db.sh");

    private readonly PostgresMigratorFixture _fixture;

    public EnvBootstrapDatabaseTests(PostgresMigratorFixture fixture) => _fixture = fixture;

    private static string RepoRoot =>
        Path.GetDirectoryName(PostgresMigratorFixture.RepoMigrationsPath)
        ?? throw new InvalidOperationException("the migrations directory has no parent");

    [Fact]
    public async Task Bootstrap_RunTwice_LeavesTheSameCatalog_AndVerifyPasses()
    {
        var database = NewDatabaseName();

        AssertSucceeded(await RunBootstrapAsync("dev", "database", DevEnvironment(database)));
        ApplyMigrations(database).ExitCode.ShouldBe(0);
        AssertSucceeded(await RunBootstrapAsync("dev", "credentials", DevEnvironment(database)));

        var before = await CatalogSnapshotAsync(database);

        AssertSucceeded(await RunBootstrapAsync("dev", "database", DevEnvironment(database)));
        ApplyMigrations(database).ExitCode.ShouldBe(0);
        AssertSucceeded(await RunBootstrapAsync("dev", "credentials", DevEnvironment(database)));

        (await CatalogSnapshotAsync(database)).ShouldBe(before);

        var verify = await RunBootstrapAsync("dev", "verify", DevEnvironment(database));

        verify.ExitCode.ShouldBe(0, verify.Output);
        verify.Output.ShouldContain("PASS roles:");
        verify.Output.ShouldContain("PASS attributes:");
        verify.Output.ShouldContain("PASS memberships:");
        verify.Output.ShouldContain("PASS ownership:");
        verify.Output.ShouldContain("PASS rls:");
    }

    [Fact]
    public async Task AppRoleWithBypassRls_IsRefusedByTheMigrator_AndNamedByVerify()
    {
        var migrated = await MigratedDatabaseAsync();
        await ExecuteSuperuserAsync(migrated, "alter role evidgate_app bypassrls");
        try
        {
            var verify = await RunBootstrapAsync("dev", "verify", DevEnvironment(migrated));

            verify.ExitCode.ShouldNotBe(0);
            verify.Output.ShouldContain("evidgate_app");
            verify.Output.ShouldContain("rolbypassrls");

            // A fresh database proves 0001 refuses the cluster-wide flag before any journal row.
            var fresh = NewDatabaseName();
            AssertSucceeded(await RunBootstrapAsync("dev", "database", DevEnvironment(fresh)));

            var migration = ApplyMigrations(fresh);

            migration.ExitCode.ShouldNotBe(0);
            migration.Transcript.ShouldContain("evidgate_app");
            migration.Transcript.ShouldContain("BYPASSRLS");
        }
        finally
        {
            await ExecuteSuperuserAsync(migrated, "alter role evidgate_app nobypassrls");
        }
    }

    [Fact]
    public async Task Credentials_WithoutTheAppPassword_FailsNamingTheKey_AndPrintsNoValue()
    {
        var environment = DevEnvironment(NewDatabaseName());
        environment.Remove("EVIDGATE_APP_PASSWORD");

        var run = await RunBootstrapAsync("dev", "credentials", environment);

        run.ExitCode.ShouldNotBe(0);
        run.Output.ShouldContain("EVIDGATE_APP_PASSWORD");
        run.Output.ShouldNotContain(_fixture.AppPassword);
        run.Output.ShouldNotContain(_fixture.OperatorPassword);
        run.Output.ShouldNotContain(_fixture.WorkerSchedPassword);
        run.Output.ShouldNotContain(_fixture.WorkerPassword);
    }

    [Fact]
    public async Task Verify_NamesAnExtraRole()
    {
        var migrated = await MigratedDatabaseAsync();
        await ExecuteSuperuserAsync(
            "postgres",
            "drop role if exists evidgate_rogue; create role evidgate_rogue nologin");
        try
        {
            var verify = await RunBootstrapAsync("dev", "verify", DevEnvironment(migrated));

            verify.ExitCode.ShouldNotBe(0);
            verify.Output.ShouldContain("evidgate_rogue");
        }
        finally
        {
            await ExecuteSuperuserAsync("postgres", "drop role if exists evidgate_rogue");
        }
    }

    [Fact]
    public async Task Verify_NamesAppOwnedObjectsOutsideTheTableCatalogs()
    {
        // The roles exist only after a migration run, so the migrated database is the precondition.
        var database = await MigratedDatabaseAsync();
        // A collation is outside the catalogs a hand-written ownership list
        // tends to cover; the check reads pg_shdepend so no class is skipped.
        await ExecuteSuperuserAsync(
            database,
            "create collation public.app_collation from \"C\"; "
            + "alter collation public.app_collation owner to evidgate_app");

        var verify = await RunBootstrapAsync("dev", "verify", DevEnvironment(database));

        verify.ExitCode.ShouldNotBe(0);
        verify.Output.ShouldContain("app_collation");
    }

    [Theory]
    [InlineData("prod", "database", "EVIDGATE_BOOTSTRAP_CONFIRM_ENV")]
    [InlineData("staging", "database", "EVIDGATE_DB_HOST")]
    public async Task StagingAndProd_TakeTheirConnectionFromTheEnvironment(
        string environment,
        string step,
        string namedKey)
    {
        var run = await RunBootstrapAsync(
            environment,
            step,
            new Dictionary<string, string?>(StringComparer.Ordinal));

        run.ExitCode.ShouldNotBe(0);
        run.Output.ShouldContain(namedKey);
    }

    [Fact]
    public async Task Staging_Verify_RequiresTheSuperuserPasswordFromTheEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["EVIDGATE_DB_HOST"] = "db.staging.example.com",
            ["EVIDGATE_DB_SUPERUSER"] = "evidgate_admin",
        };

        var run = await RunBootstrapAsync("staging", "verify", environment);

        run.ExitCode.ShouldNotBe(0);
        run.Output.ShouldContain("EVIDGATE_DB_SUPERUSER_PASSWORD");
    }

    [Theory]
    [InlineData(
        "revoke evidgate_app from evidgate_owner; grant evidgate_owner to evidgate_app",
        "revoke evidgate_owner from evidgate_app; grant evidgate_app to evidgate_owner with admin true, inherit false, set false",
        "evidgate_app is a member of evidgate_owner")]
    [InlineData("grant evidgate_operator to evidgate_app", "revoke evidgate_operator from evidgate_app", "evidgate_app is a member of evidgate_operator")]
    [InlineData("grant pg_read_all_data to evidgate_worker_sched with admin true, inherit false, set false", "revoke pg_read_all_data from evidgate_worker_sched", "evidgate_worker_sched is a member of pg_read_all_data")]
    [InlineData("grant postgres to evidgate_app", "revoke postgres from evidgate_app", "evidgate_app is a member of postgres")]
    public async Task Verify_NamesAnApplicationRoleGrantedMembershipInAnotherRole(
        string escalation, string restore, string expected)
    {
        // A member acts as that role for every grant and policy, so the superuser
        // and an admin-only pg_* grant must fail too. app -> owner needs the
        // implicit owner -> app edge lifted first; the finally restores it exactly.
        var database = await MigratedDatabaseAsync();
        await ExecuteSuperuserAsync(database, escalation);
        try
        {
            var verify = await RunBootstrapAsync("dev", "verify", DevEnvironment(database));
            verify.ExitCode.ShouldNotBe(0);
            verify.Output.ShouldContain(expected);
        }
        finally
        {
            await ExecuteSuperuserAsync(database, restore);
        }
    }

    [Fact]
    public async Task Verify_NamesAnApplicationRoleThatCannotLogIn()
    {
        var database = await MigratedDatabaseAsync();
        await ExecuteSuperuserAsync(database, "alter role evidgate_app nologin");
        try
        {
            var verify = await RunBootstrapAsync("dev", "verify", DevEnvironment(database));
            verify.ExitCode.ShouldNotBe(0);
            verify.Output.ShouldContain("evidgate_app: rolcanlogin must be true");
        }
        finally
        {
            await ExecuteSuperuserAsync(database, "alter role evidgate_app login");
        }
    }

    [Fact]
    public async Task Verify_NamesATableWhoseForcedRlsIsDisabled()
    {
        // A forced table with a policy: mechanism exceptions carry no policy, so
        // this discovery cannot pick one of them.
        var database = await MigratedDatabaseAsync();
        var table = await _fixture.ScalarAsync<string>(database, ForcedRlsTableSql);
        table.ShouldNotBeNullOrWhiteSpace();
        await ExecuteSuperuserAsync(database, $"alter table public.{table} no force row level security");
        try
        {
            var verify = await RunBootstrapAsync("dev", "verify", DevEnvironment(database));
            verify.ExitCode.ShouldNotBe(0);
            verify.Output.ShouldContain($"{table}(rowsecurity=t,forcerowsecurity=f)");
        }
        finally
        {
            await ExecuteSuperuserAsync(database, $"alter table public.{table} force row level security");
        }
    }

    [Fact]
    public async Task Credentials_WithQuotesSemicolonsAndBackslashes_RoundTrip()
    {
        // Every character a hand-built SQL or DSN string breaks on: psql reads it via \getenv.
        const string tricky = "pa'ss;word\\\"x";
        var database = await MigratedDatabaseAsync();
        var environment = DevEnvironment(database);
        environment["EVIDGATE_APP_PASSWORD"] = tricky;
        environment["EVIDGATE_OPERATOR_PASSWORD"] = tricky + "1";
        environment["EVIDGATE_WORKER_SCHED_PASSWORD"] = tricky + "2";
        environment["EVIDGATE_WORKER_PASSWORD"] = tricky + "3";
        try
        {
            AssertSucceeded(await RunBootstrapAsync("dev", "credentials", environment));
            var verify = await RunBootstrapAsync("dev", "verify", environment);
            verify.ExitCode.ShouldBe(0, verify.Output);

            await using var connection = AppConnection(database, tricky);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "select current_user";
            var user = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            user.ShouldBe("evidgate_app");
        }
        finally
        {
            AssertSucceeded(await RunBootstrapAsync("dev", "credentials", DevEnvironment(database)));
        }
    }

    [Theory]
    [InlineData("staging")]
    [InlineData("prod")]
    public async Task ManagedEnvironment_WithAContainer_IsRefusedNamingTheKey(string environment)
    {
        // The fixture's container is running, so the refusal is about the environment.
        var variables = ManagedEnvironment(environment);
        variables["EVIDGATE_DB_CONTAINER"] = _fixture.ContainerName;
        var refused = await RunBootstrapAsync(environment, "database", variables);

        refused.ExitCode.ShouldNotBe(0);
        refused.Output.ShouldContain("refusing EVIDGATE_DB_CONTAINER");
        // Positive control: the same variables minus that key clear the refusal.
        variables.Remove("EVIDGATE_DB_CONTAINER");
        var control = await RunBootstrapAsync(environment, "database", variables);
        control.Output.ShouldNotContain("refusing EVIDGATE_DB_CONTAINER");
    }

    [Theory]
    [InlineData("staging", "evidgate_prod")]
    [InlineData("prod", "evidgate_staging")]
    public async Task ManagedEnvironment_RefusesTheOtherEnvironmentsDatabaseName(string environment, string database)
    {
        var variables = ManagedEnvironment(environment);
        variables["EVIDGATE_DB_NAME"] = database;
        var refused = await RunBootstrapAsync(environment, "database", variables);

        refused.ExitCode.ShouldNotBe(0);
        refused.Output.ShouldContain("separate clusters");
    }

    [Fact]
    public async Task Dev_WithoutAContainer_RefusesANonLoopbackHost()
    {
        // The class's other dev runs are the control: same command, same loopback host.
        var environment = DevEnvironment(NewDatabaseName());
        environment.Remove("EVIDGATE_DB_CONTAINER");
        environment["EVIDGATE_DB_HOST"] = "db.example.com";
        var refused = await RunBootstrapAsync("dev", "database", environment);

        refused.ExitCode.ShouldNotBe(0);
        refused.Output.ShouldContain("refusing EVIDGATE_DB_HOST 'db.example.com'");
    }

    [Theory]
    [InlineData("Evidgate")]
    [InlineData("evidgate-prod")]
    [InlineData("evidgate;drop database postgres")]
    [InlineData("-evidgate")]
    public async Task Bootstrap_WithAnInvalidDatabaseName_IsRefusedNamingTheKey(string database)
    {
        var run = await RunBootstrapAsync("dev", "database", DevEnvironment(database));
        run.ExitCode.ShouldNotBe(0);
        run.Output.ShouldContain("EVIDGATE_DB_NAME");
    }

    private static string NewDatabaseName() =>
        "evg_boot_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];

    private async Task<string> MigratedDatabaseAsync()
    {
        var database = NewDatabaseName();
        AssertSucceeded(await RunBootstrapAsync("dev", "database", DevEnvironment(database)));
        var migration = ApplyMigrations(database);
        migration.ExitCode.ShouldBe(0, migration.Transcript);
        // The verifier asserts the post-credentials state, so the baseline is
        // green before a test changes the one thing it is about.
        AssertSucceeded(await RunBootstrapAsync("dev", "credentials", DevEnvironment(database)));
        return database;
    }

    private RunOutcome ApplyMigrations(string database) =>
        MigratorHarness.Run(PostgresMigratorFixture.RepoMigrationsPath, _fixture.OwnerConnectionString(database));

    private Dictionary<string, string?> DevEnvironment(string database) => new(StringComparer.Ordinal)
    {
        ["EVIDGATE_DB_CONTAINER"] = _fixture.ContainerName,
        ["EVIDGATE_DB_NAME"] = database,
        ["EVIDGATE_OWNER_PASSWORD"] = _fixture.OwnerPassword,
        ["EVIDGATE_APP_PASSWORD"] = _fixture.AppPassword,
        ["EVIDGATE_OPERATOR_PASSWORD"] = _fixture.OperatorPassword,
        ["EVIDGATE_WORKER_SCHED_PASSWORD"] = _fixture.WorkerSchedPassword,
        ["EVIDGATE_WORKER_PASSWORD"] = _fixture.WorkerPassword,
    };

    /// <summary>Staging or prod values at a closed port: a run past the guards fails at the connection.</summary>
    private static Dictionary<string, string?> ManagedEnvironment(string environment) => new(StringComparer.Ordinal)
    {
        ["EVIDGATE_DB_HOST"] = "127.0.0.1",
        ["EVIDGATE_DB_PORT"] = "1",
        ["EVIDGATE_DB_SUPERUSER"] = "evidgate_admin",
        ["EVIDGATE_DB_SUPERUSER_PASSWORD"] = "unused-by-the-guards",
        ["EVIDGATE_OWNER_PASSWORD"] = "unused-by-the-guards",
        ["EVIDGATE_BOOTSTRAP_CONFIRM_ENV"] = environment,
    };

    /// <summary>The application role's connection with a password the fixture does not hold.</summary>
    private NpgsqlConnection AppConnection(string database, string password) =>
        new(new NpgsqlConnectionStringBuilder(_fixture.AppConnectionString(database))
        {
            Password = password,
            Pooling = false,
        }.ConnectionString);

    private static async Task<RunResult> RunBootstrapAsync(
        string environment,
        string step,
        IReadOnlyDictionary<string, string?> variables)
    {
        var startInfo = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepoRoot,
        };
        startInfo.ArgumentList.Add(BootstrapScript);
        startInfo.ArgumentList.Add(environment);
        startInfo.ArgumentList.Add(step);

        // No EVIDGATE_* value from the test runner may reach the child: a stray
        // EVIDGATE_DB_HOST would verify a different server. The rest of the
        // environment is kept, because the docker context decides which daemon
        // `docker exec` reaches.
        foreach (var key in startInfo.Environment.Keys
                     .Where(key => key.StartsWith("EVIDGATE_", StringComparison.Ordinal))
                     .ToList())
        {
            startInfo.Environment.Remove(key);
        }

        foreach (var (key, value) in variables)
        {
            startInfo.Environment[key] = value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("could not start bootstrap-db.sh");
        var token = TestContext.Current.CancellationToken;
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ScriptTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                $"bootstrap-db.sh {environment} {step} did not finish within {ScriptTimeout.TotalSeconds}s");
        }

        return new RunResult(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false));
    }

    private async Task ExecuteSuperuserAsync(string database, string sql)
    {
        await using var connection = await _fixture.OpenSuperuserAsync(database).ConfigureAwait(false);
        await PostgresMigratorFixture.ExecuteAsync(connection, sql).ConfigureAwait(false);
    }

    private async Task<string> CatalogSnapshotAsync(string database)
    {
        await using var connection = await _fixture.OpenSuperuserAsync(database).ConfigureAwait(false);
        var snapshot = await PostgresMigratorFixture
            .ScalarAsync<string>(connection, CatalogSnapshotSql)
            .ConfigureAwait(false);
        return snapshot ?? string.Empty;
    }

    private static void AssertSucceeded(RunResult run) => run.ExitCode.ShouldBe(0, run.Output);

    /// <summary>A forced table with a policy, which the mechanism exceptions never carry.</summary>
    private const string ForcedRlsTableSql = """
        select c.relname
          from pg_catalog.pg_class c
          join pg_catalog.pg_namespace n on n.oid = c.relnamespace
         where n.nspname = 'public' and c.relkind = 'r'
           and c.relrowsecurity and c.relforcerowsecurity
           and exists (select 1 from pg_catalog.pg_policy p where p.polrelid = c.oid)
         order by c.relname limit 1
        """;

    /// <summary>
    /// The rerun contract: every role attribute, the database owner and the
    /// schema, relation and default ACLs read the same before and after. Password
    /// verifiers are deliberately absent — a rerun must not have to keep the same
    /// SCRAM salt, only the same working credential.
    /// </summary>
    private const string CatalogSnapshotSql = """
        select string_agg(line, E'\n' order by line)
          from (
            select 'role ' || rolname || ' super=' || rolsuper || ' inherit=' || rolinherit
                   || ' createrole=' || rolcreaterole || ' createdb=' || rolcreatedb
                   || ' login=' || rolcanlogin || ' bypassrls=' || rolbypassrls as line
              from pg_catalog.pg_roles
             where rolname like 'evidgate%'
            union all
            select 'database ' || datname || ' owner=' || pg_catalog.pg_get_userbyid(datdba)
                   || ' acl=' || coalesce(array_to_string(datacl::text[], ','), '')
              from pg_catalog.pg_database
             where datname = current_database()
            union all
            select 'schema ' || nspname || ' owner=' || pg_catalog.pg_get_userbyid(nspowner)
                   || ' acl=' || coalesce(array_to_string(nspacl::text[], ','), '')
              from pg_catalog.pg_namespace
             where nspname in ('public', 'evidgate')
            union all
            select 'relation ' || n.nspname || '.' || c.relname
                   || ' owner=' || pg_catalog.pg_get_userbyid(c.relowner)
                   || ' acl=' || coalesce(array_to_string(c.relacl::text[], ','), '')
              from pg_catalog.pg_class c
              join pg_catalog.pg_namespace n on n.oid = c.relnamespace
             where n.nspname = 'public'
            union all
            select 'default_acl ' || coalesce(pg_catalog.pg_get_userbyid(d.defaclrole), '-')
                   || ' ' || d.defaclobjtype::text
                   || ' acl=' || coalesce(array_to_string(d.defaclacl::text[], ','), '')
              from pg_catalog.pg_default_acl d
          ) lines
        """;

    private sealed record RunResult(int ExitCode, string Stdout, string Stderr)
    {
        internal string Output => Stdout + Stderr;
    }
}
