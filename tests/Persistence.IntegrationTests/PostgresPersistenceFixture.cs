extern alias migrator;

using System.Globalization;
using migrator::Evidgate.Migrator;
using Evidgate.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Persistence.IntegrationTests;

/// <summary>
/// One digest-pinned PostgreSQL 18 container per test assembly and a fresh
/// database per test, each copied from one database brought to head once by the
/// real <c>Evidgate.Migrator</c> with the same superuser bootstrap the isolation
/// suite uses (01.4.1 §9, scripts/dev/db-init.sh): the superuser creates
/// <c>evidgate_owner</c>, migration 0001 creates the three application roles, and
/// the superuser then hands <c>evidgate_app</c> its LOGIN and credential.
/// </summary>
public sealed class PostgresPersistenceFixture : IAsyncLifetime
{
    private const string DatabaseOwnerRole = "evidgate_owner";

    private PostgreSqlContainer _container = null!;

    private readonly string _ownerPassword = "owner-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private readonly string _appPassword = "app-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private readonly string _taskWorkerPassword = "task-worker-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

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
            [$"alter role evidgate_app login password '{_appPassword}'",
                $"alter role evidgate_worker login password '{_taskWorkerPassword}'"]);
    }

    public ValueTask DisposeAsync()
    {
        _template?.Dispose();
        return new ValueTask(ContainerStart.DisposeAsync(_container));
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

    /// <summary>
    /// The owner-role string used for migrations and owner-role assertions. Pooling
    /// is off for the owner and superuser roles on purpose: the suite creates one
    /// database per test, and an idle session left in Npgsql's process-wide pool for
    /// each database and role consumes a container connection slot, which exhausts
    /// the server (`53300`) as the assembly grows. The app-role string below keeps
    /// pooling — 01.4.2-T1.2's pool-reset proof is about that pool.
    /// </summary>
    public string OwnerConnectionString(string database) =>
        Build(DatabaseOwnerRole, _ownerPassword, database, pooling: false);

    public string AppConnectionString(string database) => Build("evidgate_app", _appPassword, database);

    public string WorkerConnectionString(string database) => Build("evidgate_worker", _taskWorkerPassword, database);

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

    /// <summary>Seeds <c>public.rls_probe</c> rows as the owner (the fixture's business-table template).</summary>
    public async Task SeedProbeAsync(string database, params (Guid OrgId, Guid Id, string Label)[] rows)
    {
        await using var connection = new NpgsqlConnection(OwnerConnectionString(database));
        await connection.OpenAsync();

        foreach (var (orgId, id, label) in rows)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "insert into public.rls_probe (org_id, id, label) values (@org, @id, @label)";
            command.Parameters.AddWithValue("org", orgId);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("label", label);
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Counts the rows the owner sees — every tenant's rows, since it holds the permissive policy.</summary>
    public async Task<int> CountProbeRowsAsOwnerAsync(string database, Guid id)
    {
        await using var connection = new NpgsqlConnection(OwnerConnectionString(database));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*)::int from public.rls_probe where id = @id";
        command.Parameters.AddWithValue("id", id);
        return (int)(await command.ExecuteScalarAsync())!;
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

    private string SuperuserConnectionString(string database) => Build("postgres", "postgres", database, pooling: false);

    private string Build(string username, string password, string database, bool pooling = true)
    {
        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = database,
            Username = username,
            Password = password,
            Pooling = pooling,
        };

        return builder.ConnectionString;
    }
}

/// <summary>Binds the container fixture to every test class that needs it (one container per assembly).</summary>
[CollectionDefinition(Name)]
public sealed class PersistenceSuite : ICollectionFixture<PostgresPersistenceFixture>
{
    public const string Name = "postgres-persistence";
}
