using System.Globalization;
using Evidgate.Migrator;
using Evidgate.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// One digest-pinned PostgreSQL 18 container per test assembly and a fresh
/// database per test, each brought to head by the real <c>Evidgate.Migrator</c>
/// with the same superuser bootstrap the isolation suite uses (01.4.1 §9,
/// scripts/dev/db-init.sh): the superuser creates <c>evidgate_owner</c>, migration
/// 0001 creates the application roles, and the superuser then hands
/// <c>evidgate_app</c>, <c>evidgate_operator</c> and <c>evidgate_worker_sched</c>
/// their LOGIN and credential (01.5.1: purge runs as the sweep role).
/// </summary>
public sealed class PostgresTasksFixture : IAsyncLifetime
{
    private const string DatabaseOwnerRole = "evidgate_owner";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(ContainerImages.Postgres)
        .WithDatabase("evidgate")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly string _ownerPassword = "owner-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private readonly string _appPassword = "app-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private readonly string _operatorPassword = "operator-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private readonly string _workerSchedPassword = "sched-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// Repository <c>migrations/</c>, resolved by the Migrator's own default-path
    /// rule (walk up from the test directory to <c>Evidgate.slnx</c>), so the suite
    /// applies the real files through the real entry point.
    /// </summary>
    public static string RepoMigrationsPath { get; } = MigratorOptions.FromEnvironment(
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [MigratorOptions.ConnectionStringVariable] = "Host=127.0.0.1;Database=evidgate;Username=evidgate_owner",
        }).MigrationsPath;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var connection = await OpenSuperuserAsync("postgres");

        // The container is fresh, so the owner role does not exist yet; the
        // attributes match the bootstrap exactly (LOGIN + CREATEROLE, nothing that
        // would bypass RLS — DECISIONS F5).
        await ExecuteAsync(
            connection,
            $"create role {DatabaseOwnerRole} login createrole nosuperuser nocreatedb nobypassrls password '{_ownerPassword}'");
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>Creates a database owned by the migration role, migrates it, and enables the app login.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var database = "scoped_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..10];

        await using (var connection = await OpenSuperuserAsync("postgres"))
        {
            await ExecuteAsync(connection, $"create database \"{database}\" owner {DatabaseOwnerRole}");
        }

        var outcome = MigratorRunner.Run(
            new MigratorOptions
            {
                ConnectionString = OwnerConnectionString(database),
                MigrationsPath = RepoMigrationsPath,
            },
            MigrationCommand.Apply,
            new StringWriter(CultureInfo.InvariantCulture));

        if (outcome.ExitCode != MigratorExitCodes.Success)
        {
            throw new InvalidOperationException(
                $"the suite could not reach head on '{database}' (exit {outcome.ExitCode}): {string.Join(" | ", outcome.Messages)}");
        }

        await using (var connection = await OpenSuperuserAsync(database))
        {
            await ExecuteAsync(connection, $"alter role evidgate_app login password '{_appPassword}'");
            await ExecuteAsync(connection, $"alter role evidgate_operator login password '{_operatorPassword}'");
            await ExecuteAsync(connection, $"alter role evidgate_worker_sched login password '{_workerSchedPassword}'");
        }

        return database;
    }

    public string OwnerConnectionString(string database) => Build(DatabaseOwnerRole, _ownerPassword, database);

    public string AppConnectionString(string database) => Build("evidgate_app", _appPassword, database);

    public string OperatorConnectionString(string database) => Build("evidgate_operator", _operatorPassword, database);

    public string WorkerSchedConnectionString(string database) => Build("evidgate_worker_sched", _workerSchedPassword, database);

    /// <summary>
    /// Opens an app-role data source for one test. <paramref name="maximumPoolSize"/>
    /// is 1 by default, so a test that reuses a connection is really reusing the
    /// same physical backend (01.4.2-T1.2's pool-reset proof).
    /// </summary>
    public NpgsqlDataSource OpenAppDataSource(string database, int maximumPoolSize = 1)
    {
        var builder = new NpgsqlConnectionStringBuilder(AppConnectionString(database))
        {
            MaxPoolSize = maximumPoolSize,
        };

        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    /// <summary>Opens a superuser connection (bootstrap and catalog assertions only).</summary>
    public async Task<NpgsqlConnection> OpenSuperuserAsync(string database)
    {
        var connection = new NpgsqlConnection(SuperuserConnectionString(database));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private string SuperuserConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database }.ConnectionString;

    private string Build(string username, string password, string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
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
public sealed class TasksSuite : ICollectionFixture<PostgresTasksFixture>
{
    public const string Name = "postgres-tasks";
}
