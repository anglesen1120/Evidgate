using System.Globalization;
using Evidgate.Migrator;
using Evidgate.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// One digest-pinned PostgreSQL 18 container per test assembly and a fresh
/// database per test, each copied from one database brought to head once by the
/// real <c>Evidgate.Migrator</c> with the same superuser bootstrap the isolation
/// suite uses (01.4.1 §9, scripts/dev/db-init.sh): the superuser creates
/// <c>evidgate_owner</c>, migration 0001 creates the application roles, and the
/// superuser then hands <c>evidgate_app</c>, <c>evidgate_operator</c> and
/// <c>evidgate_worker_sched</c> their LOGIN and credential (01.5.1: purge runs as
/// the sweep role).
/// </summary>
public sealed class PostgresTasksFixture : IAsyncLifetime
{
    private const string DatabaseOwnerRole = "evidgate_owner";

    private PostgreSqlContainer _container = null!;

    private readonly string _ownerPassword = "owner-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private readonly string _appPassword = "app-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private readonly string _operatorPassword = "operator-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private readonly string _workerSchedPassword = "sched-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private TemplateDatabase? _template;

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
        _container = await ContainerStart.StartAsync(
            () => new PostgreSqlBuilder(ContainerImages.Postgres)
                .WithDatabase("evidgate")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build());

        await using var connection = await OpenSuperuserAsync("postgres");

        // The container is fresh, so the owner role does not exist yet; the
        // attributes match the bootstrap exactly (LOGIN + CREATEROLE, nothing that
        // would bypass RLS — DECISIONS F5).
        await ExecuteAsync(
            connection,
            $"create role {DatabaseOwnerRole} login createrole nosuperuser nocreatedb nobypassrls password '{_ownerPassword}'");

        _template = new TemplateDatabase(
            SuperuserConnectionString,
            OwnerConnectionString,
            RepoMigrationsPath,
            [
                $"alter role evidgate_app login password '{_appPassword}'",
                $"alter role evidgate_operator login password '{_operatorPassword}'",
                $"alter role evidgate_worker_sched login password '{_workerSchedPassword}'",
            ]);
    }

    public ValueTask DisposeAsync()
    {
        _template?.Dispose();
        return _container is null ? ValueTask.CompletedTask : _container.DisposeAsync();
    }

    /// <summary>The migrated template every per-test database is copied from.</summary>
    private TemplateDatabase Template =>
        _template ?? throw new InvalidOperationException("the fixture has not started its container");

    /// <summary>Creates a database at head (copied from the migrated template) and enables the app login.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var database = "scoped_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..10];

        return await Template.CreateDatabaseAsync(database);
    }

    public string OwnerConnectionString(string database) => Unpooled(Build(DatabaseOwnerRole, _ownerPassword, database));

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
        Unpooled(new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database }.ConnectionString);

    /// <summary>
    /// Bootstrap and verification connections are not pooled. The container allows
    /// about a hundred backends and every test migrates its own database, so a pooled
    /// connection per (role, database) pair would leave one idle backend per test
    /// behind and starve the suite long before the tests run out of work (the failure
    /// shows up as 53300 from the migration step of a later test). Nothing here needs
    /// a pool: the migrator's and the fixture's own connections are short-lived, and
    /// the roles whose connections the mechanism under test reuses keep their pool.
    /// </summary>
    private static string Unpooled(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

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
