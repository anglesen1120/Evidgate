using System.Globalization;
using Evidgate.Migrator;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

[assembly: AssemblyFixture(typeof(Tenancy.IntegrationTests.PostgresTenancyFixture))]

namespace Tenancy.IntegrationTests;

/// <summary>
/// One digest-pinned PostgreSQL 18 container per test assembly and one shared database
/// (tests isolate by tenant), plus dropped-after-use dedicated databases for tests that cannot
/// share, all copied from one database brought to head once by the
/// real <c>Evidgate.Migrator</c> as <c>evidgate_owner</c> (02.1.1-T1.4): the
/// superuser bootstrap creates only the owner role with LOGIN + CREATEROLE
/// (migration 0001 creates the three application roles) and then hands
/// <c>evidgate_app</c> and <c>evidgate_operator</c> their LOGIN + credential —
/// exactly the split <c>scripts/dev/db-init.sh</c> performs (01 §6).
/// The container starts with the first test that needs a database, so a run that selects only
/// non-Docker tests never opens a Docker socket: xUnit initializes an assembly fixture even when
/// the filter excludes every Docker test.
/// </summary>
public sealed class PostgresTenancyFixture : IAsyncLifetime
{
    private const string DatabaseOwnerRole = "evidgate_owner";

    private readonly object _startLock = new();
    private PostgreSqlContainer? _container;
    private Task? _start;

    private readonly string _ownerPassword = "owner-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private readonly string _appPassword = "app-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private readonly string _operatorPassword = "operator-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private TemplateDatabase? _template;
    private Task<string>? _shared;

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

    // Assembly fixtures initialize even when the filter excludes every Docker test.
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    private Task EnsureStartedAsync()
    {
        lock (_startLock)
        {
            return _start ??= StartAsync();
        }
    }

    private async Task StartAsync()
    {
        // Cumulative statistics outgrow Docker's default 64 MB shared memory around 750 databases.
        _container = new PostgreSqlBuilder(ContainerImages.Postgres)
            .WithDatabase("evidgate")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .WithCreateParameterModifier(parameters =>
            {
                ArgumentNullException.ThrowIfNull(parameters.HostConfig);
                parameters.HostConfig.ShmSize = 512L * 1024 * 1024;
            })
            .Build();

        await _container.StartAsync().ConfigureAwait(false);

        // Shared by every test: bootstrap must neither await the public connection helpers' own startup
        // task nor take the first test's cancellation token, or one cancelled test would fail the run.
        await using var connection = new NpgsqlConnection(SuperuserConnectionString("postgres"));
        await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
        await using var create = connection.CreateCommand();
        create.CommandText = $"create role {DatabaseOwnerRole} login createrole password '{_ownerPassword}'";
        await create.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);

        _template = new TemplateDatabase(
            SuperuserConnectionString,
            OwnerConnectionString,
            RepoMigrationsPath,
            [
                $"alter role evidgate_app login password '{_appPassword}'",
                $"alter role evidgate_operator login password '{_operatorPassword}'",
            ]);
    }

    public async ValueTask DisposeAsync()
    {
        _template?.Dispose();

        // Built before any await in StartAsync, so a failed or still-starting fixture is disposed too.
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The one database of the run, at head with the application logins enabled. A test using it creates
    /// its own organisation with unique identifiers (<see cref="Unique"/>) and reads only its own tenant;
    /// it never counts, drops or alters anything cluster- or database-wide.
    /// </summary>
    public async Task<string> SharedDatabaseAsync()
    {
        await EnsureStartedAsync().ConfigureAwait(false);

        Task<string> shared;
        lock (_startLock)
        {
            shared = _shared ??= CreateSharedAsync();
        }

        return await shared.ConfigureAwait(false);
    }

    /// <summary>
    /// A private database at head for a test that cannot share (global rows, schema or role changes,
    /// unfiltered counts); it is dropped when the returned scope is disposed.
    /// </summary>
    public async Task<DedicatedDatabase> CreateDedicatedDatabaseAsync()
    {
        await EnsureStartedAsync().ConfigureAwait(false);

        var database = NewDatabaseName();
        await Template.CreateDatabaseAsync(database).ConfigureAwait(false);
        SessionUserSeed.Register(database, OwnerConnectionString(database));

        return new DedicatedDatabase(this, database);
    }

    /// <summary>A private database owned by the migration role with nothing applied, dropped on dispose.</summary>
    public async Task<DedicatedDatabase> CreateEmptyDedicatedDatabaseAsync()
    {
        var database = NewDatabaseName();

        await using (var connection = await OpenSuperuserAsync("postgres").ConfigureAwait(false))
        {
            await ExecuteAsync(connection, $"create database \"{database}\" owner {DatabaseOwnerRole}").ConfigureAwait(false);
        }

        // The host's stand-in session seeds the `user` row a signed-in caller implies (0044's
        // membership_user_fk); it can only do that through this owner seam.
        SessionUserSeed.Register(database, OwnerConnectionString(database));

        return new DedicatedDatabase(this, database);
    }

    private async Task<string> CreateSharedAsync()
    {
        var database = NewDatabaseName();
        await Template.CreateDatabaseAsync(database).ConfigureAwait(false);
        SessionUserSeed.Register(database, OwnerConnectionString(database));

        return database;
    }

    private static string NewDatabaseName() =>
        "tenancy_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..10];

    internal async Task DropDatabaseAsync(string database)
    {
        try
        {
            await using var connection = new NpgsqlConnection(SuperuserConnectionString("postgres"));
            await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"drop database if exists \"{database}\" with (force)";
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // A leftover scratch database never fails a run; the container is discarded whole.
        }
    }

    /// <summary>The migrated template every dedicated database is copied from.</summary>
    private TemplateDatabase Template =>
        _template ?? throw new InvalidOperationException("the fixture has not started its container");

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
        await EnsureStartedAsync().ConfigureAwait(false);

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
        await EnsureStartedAsync().ConfigureAwait(false);

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
        var container = _container ?? throw new InvalidOperationException("the fixture has not started its container");
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
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

/// <summary>A private test database that is dropped on dispose.</summary>
public sealed class DedicatedDatabase : IAsyncDisposable
{
    private readonly PostgresTenancyFixture _fixture;

    internal DedicatedDatabase(PostgresTenancyFixture fixture, string name)
    {
        _fixture = fixture;
        Name = name;
    }

    public string Name { get; }

    public static implicit operator string(DedicatedDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.Name;
    }

    public override string ToString() => Name;

    public ValueTask DisposeAsync() => new(_fixture.DropDatabaseAsync(Name));
}

/// <summary>Random identifiers so tests sharing one database never collide on a slug or name.</summary>
public static class Unique
{
    /// <summary>A lowercase slug <c>{stem}-{8 hex}</c>.</summary>
    public static string Slug(string stem) =>
        stem + "-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
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

/// <summary>Runs cluster-wide and timing-sensitive tests without overlapping other collections.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExclusiveTenancySuite
{
    public const string Name = "postgres-tenancy-exclusive";
}
