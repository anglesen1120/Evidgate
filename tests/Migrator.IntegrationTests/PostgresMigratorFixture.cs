using System.Globalization;
using Evidgate.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary>
/// One digest-pinned PostgreSQL 18 container per test assembly (01 §7: real
/// PostgreSQL, never a fake) and a fresh database per test, so a journal row,
/// schema or grant created by one test can never bleed into another.
/// </summary>
/// <remarks>
/// Cluster-level objects (the four roles) are created once per container; the
/// bootstrap deliberately creates <em>only</em> <c>evidgate_owner</c> — with
/// LOGIN + CREATEROLE and no other attribute — because migration 0001 creates the
/// three application roles with their least-privilege attributes. Passwords are
/// random per run and never written to a file (00 §5.7 S3).
/// </remarks>
public sealed class PostgresMigratorFixture : IAsyncLifetime
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
    private readonly string _workerPassword = "worker-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Repository <c>migrations/</c> directory, resolved from the test output directory.</summary>
    public static string RepoMigrationsPath { get; } = ResolveRepoMigrationsPath();

    /// <summary>The real migration file names, in apply order.</summary>
    public static IReadOnlyList<string> RealMigrationFileNames { get; } =
        Directory.EnumerateFiles(RepoMigrationsPath, "*.sql")
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Highest sequence number in <c>migrations/</c>; test fixtures continue from here.</summary>
    public static int MaxRealNumber { get; } = RealMigrationFileNames
        .Select(name => int.Parse(name[..4], CultureInfo.InvariantCulture))
        .Max();

    /// <summary>Connection string for the container's superuser (bootstrap and verification only).</summary>
    public string SuperuserConnectionString(string database) => Build("postgres", "postgres", database);

    /// <summary>Connection string for the migration role.</summary>
    public string OwnerConnectionString(string database) => Build(DatabaseOwnerRole, _ownerPassword, database);

    /// <summary>Connection string for the application role (used once logins are enabled).</summary>
    public string AppConnectionString(string database) => Build("evidgate_app", _appPassword, database);

    /// <summary>Container name, for a test that drives a shell script through <c>docker exec</c>.</summary>
    public string ContainerName => _container.Name;

    /// <summary>The migration role's password, so a bootstrap that re-sets it keeps this fixture's credential.</summary>
    public string OwnerPassword => _ownerPassword;

    /// <summary>The application role's password.</summary>
    public string AppPassword => _appPassword;

    /// <summary>The audited read-only role's password.</summary>
    public string OperatorPassword => _operatorPassword;

    /// <summary>The sweep role's password.</summary>
    public string WorkerSchedPassword => _workerPassword;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);

        await using var connection = await OpenSuperuserAsync("postgres").ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            $"create role {DatabaseOwnerRole} login createrole password '{_ownerPassword}'").ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);

    /// <summary>Creates a database owned by the migration role and returns its name.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = "evg_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];

        await using var connection = await OpenSuperuserAsync("postgres").ConfigureAwait(false);
        await ExecuteAsync(connection, $"create database \"{name}\" owner {DatabaseOwnerRole}").ConfigureAwait(false);
        return name;
    }

    /// <summary>Opens a superuser connection to <paramref name="database"/>.</summary>
    public async Task<NpgsqlConnection> OpenSuperuserAsync(string database)
    {
        var connection = new NpgsqlConnection(SuperuserConnectionString(database));
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    /// <summary>Grants LOGIN + a credential to the application roles, as the superuser bootstrap does (01.4.1-T2.3).</summary>
    public async Task EnableApplicationLoginsAsync(string database)
    {
        await using var connection = await OpenSuperuserAsync(database).ConfigureAwait(false);
        await ExecuteAsync(connection, $"alter role evidgate_app login password '{_appPassword}'").ConfigureAwait(false);
        await ExecuteAsync(connection, $"alter role evidgate_operator login password '{_operatorPassword}'").ConfigureAwait(false);
        await ExecuteAsync(connection, $"alter role evidgate_worker_sched login password '{_workerPassword}'").ConfigureAwait(false);
    }

    /// <summary>Runs a scalar query as the superuser.</summary>
    public async Task<T> ScalarAsync<T>(string database, string sql)
    {
        await using var connection = await OpenSuperuserAsync(database).ConfigureAwait(false);
        return await ScalarAsync<T>(connection, sql).ConfigureAwait(false);
    }

    /// <summary>The journal, ordered by name, read as the migration role.</summary>
    public async Task<IReadOnlyList<JournalRow>> ReadJournalAsync(string database)
    {
        await using var connection = new NpgsqlConnection(OwnerConnectionString(database));
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "select name, checksum, applied_at, duration_ms from public.schema_migration order by name";
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        var token = TestContext.Current.CancellationToken;
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

    internal static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    internal static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null or DBNull ? default! : (T)value;
    }

    private string Build(string username, string password, string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = database,
            Username = username,
            Password = password,
            // Every test uses its own database, so idle pooled connections would pile up
            // until the server refuses new ones (53300).
            Pooling = false,
        };

        return builder.ConnectionString;
    }

    private static string ResolveRepoMigrationsPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Evidgate.slnx")))
            {
                return Path.Combine(directory.FullName, "migrations");
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"repository root (Evidgate.slnx) not found above '{AppContext.BaseDirectory}'");
    }
}

/// <summary>One <c>public.schema_migration</c> row, as the tests read it.</summary>
/// <param name="Name">Migration file name including <c>.sql</c>.</param>
/// <param name="Checksum">Lowercase hex SHA-256 recorded at apply time.</param>
/// <param name="AppliedAt">Commit timestamp.</param>
/// <param name="DurationMs">Script duration recorded by the Migrator.</param>
public sealed record JournalRow(string Name, string Checksum, DateTimeOffset AppliedAt, int? DurationMs);

/// <summary>Binds the container fixture to every test class that needs it (one container per assembly).</summary>
[CollectionDefinition(Name)]
public sealed class MigratorPostgresSuite : ICollectionFixture<PostgresMigratorFixture>
{
    public const string Name = "postgres-migrator";
}
