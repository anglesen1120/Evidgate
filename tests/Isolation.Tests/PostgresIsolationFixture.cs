using System.Globalization;
using Evidgate.Migrator;
using Evidgate.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// One digest-pinned PostgreSQL 18 container per test assembly and a fresh
/// database per test, each copied from one database brought to head once by the
/// real <c>Evidgate.Migrator</c> (01.4.1 §9): the superuser bootstrap creates
/// only <c>evidgate_owner</c> with LOGIN + CREATEROLE, migration 0001 creates the
/// three application roles, and the superuser then hands the application roles
/// their LOGIN + credential — exactly the split <c>scripts/dev/db-init.sh</c>
/// performs (01 §6).
///
/// AC5: with <see cref="SuperuserConnectionStringVariable"/> set the suite does
/// not start a container at all — it runs against that already-running server
/// (the compose dev stack), reusing its <c>evidgate_owner</c> role, adding one
/// migrated template and one copied scratch database per test to it, and dropping
/// both when the assembly finishes.
/// </summary>
public sealed class PostgresIsolationFixture : IAsyncLifetime
{
    /// <summary>
    /// AC5 switch: a superuser connection string of a running server, e.g.
    /// <c>Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=…</c>.
    /// Unset (the default) keeps the Testcontainers behaviour untouched.
    /// </summary>
    public const string SuperuserConnectionStringVariable = "EVIDGATE_ISOLATION__SUPERUSERCONNECTIONSTRING";

    private const string DatabaseOwnerRole = "evidgate_owner";

    private PostgreSqlContainer? _container;

    /// <summary>The reused server's superuser coordinates (AC5); null starts the container instead.</summary>
    private readonly string? _externalSuperuserConnectionString =
        Environment.GetEnvironmentVariable(SuperuserConnectionStringVariable) is { Length: > 0 } value ? value : null;

    /// <summary>Databases created on a reused server, dropped when the assembly finishes.</summary>
    private readonly List<string> _createdDatabases = [];

    private readonly string _ownerPassword = "owner-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private readonly string _appPassword = "app-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private readonly string _operatorPassword = "operator-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private readonly string _workerPassword = "worker-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private TemplateDatabase? _template;

    /// <summary>
    /// Repository <c>migrations/</c>, resolved by the Migrator's own default-path
    /// rule (walk up from the test directory to <c>Evidgate.slnx</c>), so the
    /// suite runs the real files through the real entry point.
    /// </summary>
    public static string RepoMigrationsPath { get; } = MigratorOptions.FromEnvironment(
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [MigratorOptions.ConnectionStringVariable] = "Host=127.0.0.1;Database=evidgate;Username=evidgate_owner",
        }).MigrationsPath;

    public async ValueTask InitializeAsync()
    {
        if (_externalSuperuserConnectionString is null)
        {
            _container = await ContainerStart.StartAsync(
                () => new PostgreSqlBuilder(ContainerImages.Postgres)
                    .WithDatabase("evidgate")
                    .WithUsername("postgres")
                    .WithPassword("postgres")
                    .Build()).ConfigureAwait(false);
        }

        await using var connection = await OpenSuperuserAsync("postgres").ConfigureAwait(false);

        // The container path always creates the role; a reused server already has
        // it (scripts/dev/db-init.sh ran 01.4.1-T2.3's bootstrap), so the same
        // statement has to be an alter there — never a create that would fail.
        await ExecuteAsync(
            connection,
            $"""
            do $$
            begin
              if exists (select 1 from pg_catalog.pg_roles where rolname = '{DatabaseOwnerRole}') then
                alter role {DatabaseOwnerRole} login createrole nosuperuser nocreatedb nobypassrls password '{_ownerPassword}';
              else
                create role {DatabaseOwnerRole} login createrole password '{_ownerPassword}';
              end if;
            end
            $$;
            """).ConfigureAwait(false);

        _template = new TemplateDatabase(
            SuperuserConnectionString,
            OwnerConnectionString,
            RepoMigrationsPath,
            [
                $"alter role evidgate_app login password '{_appPassword}'",
                $"alter role evidgate_operator login password '{_operatorPassword}'",
                $"alter role evidgate_worker_sched login password '{_workerPassword}'",
            ]);
    }

    public async ValueTask DisposeAsync()
    {
        if (_externalSuperuserConnectionString is null)
        {
            if (_container is not null)
            {
                await _container.DisposeAsync().ConfigureAwait(false);
            }

            _template?.Dispose();
            return;
        }

        // A reused server outlives the run: drop the scratch databases it holds
        // for this assembly, then the template they were copied from — in that
        // order, because PostgreSQL refuses to drop a template another database
        // still depends on (the rotated role passwords stay — docs/dev-setup.md).
        foreach (var database in _createdDatabases)
        {
            try
            {
                await ExecuteAsync("postgres", $"drop database if exists \"{database}\" with (force)").ConfigureAwait(false);
            }
            catch (NpgsqlException)
            {
                // A leftover scratch database must never fail the run.
            }
        }

        if (_template is not null)
        {
            await _template.DropAsync().ConfigureAwait(false);
        }

        _template?.Dispose();
    }

    /// <summary>The migrated template every per-test database is copied from.</summary>
    private TemplateDatabase Template =>
        _template ?? throw new InvalidOperationException("the fixture has not started its server");

    /// <summary>Creates a database at head (copied from the migrated template) and enables the app logins.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var database = "isolated_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..10];

        await Template.CreateDatabaseAsync(database).ConfigureAwait(false);

        _createdDatabases.Add(database);

        return database;
    }

    public string OwnerConnectionString(string database) => Build(DatabaseOwnerRole, _ownerPassword, database);

    public string AppConnectionString(string database) => Build("evidgate_app", _appPassword, database);

    public string OperatorConnectionString(string database) => Build("evidgate_operator", _operatorPassword, database);

    public string WorkerSchedulerConnectionString(string database) =>
        Build("evidgate_worker_sched", _workerPassword, database);

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
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? default! : (T)value;
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

    internal static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Superuser connection to one database: the reused server's own credentials
    /// when AC5 is on, otherwise the fixture container's postgres/postgres.
    /// </summary>
    private string SuperuserConnectionString(string database) =>
        _externalSuperuserConnectionString is null
            ? Build("postgres", "postgres", database)
            : new NpgsqlConnectionStringBuilder(_externalSuperuserConnectionString) { Database = database }.ConnectionString;

    private string Build(string username, string password, string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(_externalSuperuserConnectionString ?? _container!.GetConnectionString())
        {
            Database = database,
            Username = username,
            Password = password,
        };

        return builder.ConnectionString;
    }
}

/// <summary>Binds the container fixture to every test class that needs it (one container per assembly).</summary>
[CollectionDefinition(Name)]
public sealed class IsolationSuite : ICollectionFixture<PostgresIsolationFixture>
{
    public const string Name = "postgres-isolation";
}
