using System.Globalization;
using Evidgate.Migrator;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// One digest-pinned PostgreSQL 18 container per test assembly and a fresh
/// database per test, each brought to head by the real <c>Evidgate.Migrator</c>
/// as <c>evidgate_owner</c> (02.1.1-T1.4): the superuser bootstrap creates only
/// the owner role with LOGIN + CREATEROLE (migration 0001 creates the three
/// application roles) and then hands <c>evidgate_app</c> and
/// <c>evidgate_operator</c> their LOGIN + credential — exactly the split
/// <c>scripts/dev/db-init.sh</c> performs (01 §6).
/// </summary>
public sealed class PostgresTenancyFixture : IAsyncLifetime
{
    private const string DatabaseOwnerRole = "evidgate_owner";

    // One fresh migrated database per test lives in this single container; PostgreSQL's cumulative-statistics
    // shared memory (posix DSA in /dev/shm) grows by doubling with the database count and, at the Docker default of
    // 64 MB, fails with 53100 "could not resize shared memory segment" once the suite passes ~750 databases.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(ContainerImages.Postgres)
        .WithDatabase("evidgate")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithCreateParameterModifier(parameters =>
        {
            ArgumentNullException.ThrowIfNull(parameters.HostConfig);
            parameters.HostConfig.ShmSize = 512L * 1024 * 1024;
        })
        .Build();

    private readonly string _ownerPassword = "owner-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private readonly string _appPassword = "app-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private readonly string _operatorPassword = "operator-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// Repository <c>migrations/</c>, resolved by the Migrator's own default-path
    /// rule (walk up to <c>Evidgate.slnx</c>), so the suite runs the real files
    /// through the real entry point.
    /// </summary>
    public static string RepoMigrationsPath { get; } = MigratorOptions.FromEnvironment(
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [MigratorOptions.ConnectionStringVariable] = "Host=127.0.0.1;Database=evidgate;Username=evidgate_owner",
        }).MigrationsPath;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);

        await using var connection = await OpenSuperuserAsync("postgres").ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            $"create role {DatabaseOwnerRole} login createrole password '{_ownerPassword}'").ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);

    /// <summary>Creates a database owned by the migration role, without applying anything.</summary>
    public async Task<string> CreateEmptyDatabaseAsync()
    {
        var database = "tenancy_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..10];

        await using var connection = await OpenSuperuserAsync("postgres").ConfigureAwait(false);
        await ExecuteAsync(connection, $"create database \"{database}\" owner {DatabaseOwnerRole}").ConfigureAwait(false);

        // The host's stand-in session seeds the `user` row a signed-in caller implies (0044's
        // membership_user_fk); it can only do that through this owner seam, so the connection is
        // registered as the database is minted.
        SessionUserSeed.Register(database, OwnerConnectionString(database));

        return database;
    }

    /// <summary>Creates a database at head (real migration files) and enables the application logins.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var database = await CreateEmptyDatabaseAsync().ConfigureAwait(false);
        var run = ApplyMigrations(database);

        if (run.ExitCode != MigratorExitCodes.Success)
        {
            throw new InvalidOperationException(
                $"the suite could not reach head on '{database}' (exit {run.ExitCode}): {string.Join(" | ", run.Messages)}");
        }

        await using var connection = await OpenSuperuserAsync(database).ConfigureAwait(false);
        await ExecuteAsync(connection, $"alter role evidgate_app login password '{_appPassword}'").ConfigureAwait(false);
        await ExecuteAsync(connection, $"alter role evidgate_operator login password '{_operatorPassword}'").ConfigureAwait(false);

        return database;
    }

    /// <summary>Runs <c>Evidgate.Migrator --apply</c> in process and returns its result and transcript.</summary>
    /// <param name="database">Target database (owner role).</param>
    /// <param name="migrationsPath">Directory of files to apply; the repository's <c>migrations/</c> by default.</param>
    public MigrationRun ApplyMigrations(string database, string? migrationsPath = null)
    {
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var result = MigratorRunner.Run(
            new MigratorOptions
            {
                ConnectionString = OwnerConnectionString(database),
                MigrationsPath = migrationsPath ?? RepoMigrationsPath,
            },
            MigrationCommand.Apply,
            writer);

        return new MigrationRun(result, writer.ToString());
    }

    public string OwnerConnectionString(string database) => Build(DatabaseOwnerRole, _ownerPassword, database);

    public string AppConnectionString(string database) => Build("evidgate_app", _appPassword, database);

    public string OperatorConnectionString(string database) => Build("evidgate_operator", _operatorPassword, database);

    /// <summary>Opens a superuser connection (bootstrap and catalog assertions only).</summary>
    public async Task<NpgsqlConnection> OpenSuperuserAsync(string database)
    {
        var connection = new NpgsqlConnection(SuperuserConnectionString(database));
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    /// <summary>Runs a scalar query as the superuser.</summary>
    public async Task<T> ScalarAsync<T>(string database, string sql)
    {
        await using var connection = await OpenSuperuserAsync(database).ConfigureAwait(false);
        return await ScalarAsync<T>(connection, sql).ConfigureAwait(false);
    }

    /// <summary>Runs a one-column query as the superuser.</summary>
    public async Task<IReadOnlyList<T>> ColumnAsync<T>(string database, string sql)
    {
        await using var connection = await OpenSuperuserAsync(database).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var token = TestContext.Current.CancellationToken;
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

        var values = new List<T>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            values.Add(await reader.GetFieldValueAsync<T>(0, token).ConfigureAwait(false));
        }

        return values;
    }

    /// <summary>Runs a statement as the superuser.</summary>
    public async Task ExecuteAsync(string database, string sql)
    {
        await using var connection = await OpenSuperuserAsync(database).ConfigureAwait(false);
        await ExecuteAsync(connection, sql).ConfigureAwait(false);
    }

    /// <summary>The journal, ordered by name, read as the migration role.</summary>
    public async Task<IReadOnlyList<JournalRow>> ReadJournalAsync(string database)
    {
        await using var connection = new NpgsqlConnection(OwnerConnectionString(database));
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "select name, checksum, applied_at, duration_ms from public.schema_migration order by name";
        var token = TestContext.Current.CancellationToken;
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

        var rows = new List<JournalRow>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            rows.Add(new JournalRow(
                await reader.GetFieldValueAsync<string>(0, token).ConfigureAwait(false),
                await reader.GetFieldValueAsync<string>(1, token).ConfigureAwait(false),
                await reader.GetFieldValueAsync<DateTimeOffset>(2, token).ConfigureAwait(false),
                await reader.IsDBNullAsync(3, token).ConfigureAwait(false)
                    ? null
                    : await reader.GetFieldValueAsync<int>(3, token).ConfigureAwait(false)));
        }

        return rows;
    }

    /// <summary>True when <paramref name="table"/> exists in schema <c>public</c>.</summary>
    public async Task<bool> TableExistsAsync(string database, string table) =>
        await ScalarAsync<long>(
            database,
            $"select count(*) from information_schema.tables where table_schema = 'public' and table_name = '{table}'")
            .ConfigureAwait(false) > 0;

    /// <summary>
    /// The table's columns as <c>name:data_type:udt_name:is_nullable:default</c>,
    /// in physical order — the one projection the shape tests compare against the
    /// feature plan §4 and the 00 §7 column contract.
    /// </summary>
    public async Task<IReadOnlyList<string>> ColumnsAsync(string database, string table) =>
        await ColumnAsync<string>(
            database,
            $"""
            select column_name || ':' || data_type || ':' || udt_name || ':' || is_nullable || ':' ||
                   coalesce(column_default, '-')
              from information_schema.columns
             where table_schema = 'public' and table_name = '{table}'
             order by ordinal_position
            """).ConfigureAwait(false);

    /// <summary>The table's constraints as <c>contype:conname:definition</c> (AC1 reads <c>pg_constraint</c>).</summary>
    public async Task<IReadOnlyList<string>> ConstraintsAsync(string database, string table) =>
        await ColumnAsync<string>(
            database,
            $"""
            select contype::text || ':' || conname || ':' || pg_get_constraintdef(oid)
              from pg_catalog.pg_constraint
             where conrelid = 'public.{table}'::regclass
               -- PostgreSQL 17+ records NOT NULL as a `n` constraint; nullability is
               -- already pinned by the column projection of ColumnsAsync.
               and contype <> 'n'
             order by contype, conname
            """).ConfigureAwait(false);

    /// <summary>The table's constraint names as <c>contype:conname</c>, the stable projection of the rule set.</summary>
    public async Task<IReadOnlyList<string>> ConstraintNamesAsync(string database, string table) =>
        await ColumnAsync<string>(
            database,
            $"""
            select contype::text || ':' || conname
              from pg_catalog.pg_constraint
             where conrelid = 'public.{table}'::regclass
               -- PostgreSQL 17+ records NOT NULL as a `n` constraint; nullability is
               -- already pinned by the column projection of ColumnsAsync.
               and contype <> 'n'
             order by contype, conname
            """).ConfigureAwait(false);

    /// <summary>The table's indexes as <c>name:definition</c> (00 §5.7 P4 reads the covering index).</summary>
    public async Task<IReadOnlyList<string>> IndexesAsync(string database, string table) =>
        await ColumnAsync<string>(
            database,
            $"""
            select indexname || ':' || indexdef
              from pg_catalog.pg_indexes
             where schemaname = 'public' and tablename = '{table}'
             order by indexname
            """).ConfigureAwait(false);

    /// <summary>Row security is on and applies to the owner too (<c>relforcerowsecurity</c>, DECISIONS F5).</summary>
    public async Task<(bool Enabled, bool Forced)> RowSecurityAsync(string database, string table)
    {
        var values = await ColumnAsync<string>(
            database,
            $"""
            select relrowsecurity::text || ':' || relforcerowsecurity::text
              from pg_catalog.pg_class
             where oid = 'public.{table}'::regclass
            """).ConfigureAwait(false);

        var parts = values.ShouldHaveSingleItem().Split(':');

        return (parts[0] == "true", parts[1] == "true");
    }

    /// <summary>Every policy of the table as <c>name:role:cmd:using:withcheck</c>, one row per target role.</summary>
    public async Task<IReadOnlyList<string>> PoliciesAsync(string database, string table) =>
        await ColumnAsync<string>(
            database,
            $"""
            select p.polname || ':' || r.rolname || ':' || p.polcmd::text || ':' ||
                   coalesce(pg_get_expr(p.polqual, p.polrelid), '-') || ':' ||
                   coalesce(pg_get_expr(p.polwithcheck, p.polrelid), '-')
              from pg_policy p
              cross join lateral unnest(p.polroles) as role_oid
              join pg_roles r on r.oid = role_oid
             where p.polrelid = 'public.{table}'::regclass
             order by p.polname, r.rolname
            """).ConfigureAwait(false);

    /// <summary>Table privilege check for one role in the given database.</summary>
    public async Task<bool> HasTablePrivilegeAsync(string database, string role, string table, string privilege) =>
        await ScalarAsync<bool>(
            database,
            $"select has_table_privilege('{role}', 'public.{table}', '{privilege}')").ConfigureAwait(false);

    internal static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    internal static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? default! : (T)value;
    }

    private string SuperuserConnectionString(string database) => Build("postgres", "postgres", database);

    private string Build(string username, string password, string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = database,
            Username = username,
            Password = password,
            // One database per test makes every connection string a distinct pool
            // key, and a pooled connector stays open (visible in pg_stat_activity)
            // until the test process exits — enough databases exhaust the
            // container's connection slots. Pooling off closes the physical
            // connection on dispose, so the suite retains ~0 connections per test.
            Pooling = false,
        };

        return builder.ConnectionString;
    }
}

/// <summary>One Migrator run: the structured result plus the console transcript.</summary>
/// <param name="Result">Exit code, plan and messages.</param>
/// <param name="Transcript">Everything the run wrote to its output.</param>
public sealed record MigrationRun(MigrationRunResult Result, string Transcript)
{
    internal int ExitCode => Result.ExitCode;

    internal IReadOnlyList<string> Messages => Result.Messages;
}

/// <summary>One <c>public.schema_migration</c> row, as the tests read it.</summary>
/// <param name="Name">Migration file name including <c>.sql</c>.</param>
/// <param name="Checksum">Lowercase hex SHA-256 recorded at apply time.</param>
/// <param name="AppliedAt">Commit timestamp.</param>
/// <param name="DurationMs">Script duration recorded by the Migrator.</param>
public sealed record JournalRow(string Name, string Checksum, DateTimeOffset AppliedAt, int? DurationMs);

/// <summary>Binds the container fixture to every test class that needs it (one container per assembly).</summary>
[CollectionDefinition(Name)]
public sealed class TenancySuite : ICollectionFixture<PostgresTenancyFixture>
{
    public const string Name = "postgres-tenancy";
}
