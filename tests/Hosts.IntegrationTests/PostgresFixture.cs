using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Readiness;
using Evidgate.Migrator;
using Evidgate.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

[assembly: AssemblyFixture(typeof(Hosts.IntegrationTests.PostgresFixture))]

namespace Hosts.IntegrationTests;

/// <summary>
/// One digest-pinned PostgreSQL 18 container per test assembly (01 §7: real
/// PostgreSQL). The journal contract is the one of 01.1.2 (migrator lands with
/// 01.4.1): <c>public.schema_migration(name text primary key, checksum text not
/// null, applied_at timestamptz not null default now(), duration_ms int)</c>,
/// <c>name</c> = migration file name including <c>.sql</c>. Fixtures here create
/// it with exactly that DDL and insert rows to simulate head/behind/ahead states.
///
/// <para>
/// The connection string handed to the hosts is the <c>evidgate_app</c> role —
/// the least-privilege role of 00 §5.4.3 that holds only <c>SELECT</c> on the
/// journal — not the container superuser (D11): a lost grant is then a failing
/// test rather than a production-only surprise. The superuser
/// <see cref="ConnectionString"/> stays for the DDL and <c>GRANT</c>/<c>REVOKE</c>.
/// </para>
///
/// <para>
/// The container starts with the first test that needs a database, so a run that
/// selects only non-Docker tests never opens a Docker socket: xUnit initializes an
/// assembly fixture even when the filter excludes every Docker test. Every class
/// that needs a database creates its own <see cref="TestDatabase"/> (none of them
/// can share one: global journal state, schema-wide policy DDL, non-RLS tables), and
/// disposing that scope drops the database, so a run leaves no scratch database
/// behind.
/// </para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Contracted journal DDL of the migration journal (01.1.2 context contract).</summary>
    public const string JournalDdl = """
        create table if not exists public.schema_migration(
            name text primary key,
            checksum text not null,
            applied_at timestamptz not null default now(),
            duration_ms int)
        """;

    /// <summary>The least-privilege application role the hosts connect as (00 §5.4.3).</summary>
    public const string AppRoleName = "evidgate_app";

    /// <summary>Expected head of this build (the baked assembly value).</summary>
    public static string ExpectedHead => ExpectedMigrationHead.Value;

    private readonly object _startLock = new();
    private PostgreSqlContainer? _container;
    private Task? _start;

    private readonly string _appPassword = "app-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private readonly string _ownerPassword = "owner-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private TemplateDatabase? _template;

    /// <summary>
    /// Repository <c>migrations/</c>, resolved by the Migrator's own
    /// default-path rule (walk up from the test directory to <c>Evidgate.slnx</c>),
    /// so the suite applies the real files through the real entry point.
    /// </summary>
    public static string RepoMigrationsPath { get; } = MigratorOptions.FromEnvironment(
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [MigratorOptions.ConnectionStringVariable] = "Host=127.0.0.1;Database=evidgate;Username=evidgate_owner",
        }).MigrationsPath;

    /// <summary>Superuser connection string for the container's default database (DDL and grants).</summary>
    public string ConnectionString => Container.GetConnectionString();

    // Assembly fixtures initialize even when the filter excludes every Docker test.
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    private Task EnsureStartedAsync()
    {
        lock (_startLock)
        {
            // Single-flight: every database test awaits the one container start of the run.
            return _start ??= StartAsync();
        }
    }

    private async Task StartAsync()
    {
        _container = new PostgreSqlBuilder(ContainerImages.Postgres)
            .WithDatabase("evidgate")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _container.StartAsync();

        // One bootstrap for the whole run: these statements never take the first test's token,
        // or one cancelled test would fail every later database test.
        await EnsureAppRoleAsync();
        await EnsureOwnerRoleAsync();

        _template = new TemplateDatabase(
            AdminConnectionString,
            OwnerConnectionString,
            RepoMigrationsPath,
            [$"alter role {AppRoleName} noinherit"]);
    }

    public async ValueTask DisposeAsync()
    {
        _template?.Dispose();

        // Built before any await in StartAsync, so a failed or still-starting fixture is disposed too.
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// A fresh database with the journal created, <c>SELECT</c> granted to the
    /// application role and the head set to <paramref name="head"/>; the scope's
    /// connection string is the <c>evidgate_app</c> one the hosts must use, and the
    /// database is dropped when the scope is disposed. The Api/Worker are ready iff
    /// <paramref name="head"/> is at or beyond <see cref="ExpectedHead"/>.
    /// </summary>
    public Task<TestDatabase> CreateDatabaseAtHeadAsync(string head) =>
        CreateScopedAsync(
            "hosts_",
            async database =>
            {
                await using (var connection = await OpenAsync(ConnectionString))
                {
                    await ExecuteAsync(
                        connection, $"create database \"{database}\"", TestContext.Current.CancellationToken);
                }

                var adminConnectionString = Builder(ConnectionString, database);
                await using (var connection = await OpenAsync(adminConnectionString))
                {
                    await ExecuteAsync(connection, JournalDdl, TestContext.Current.CancellationToken);
                    await ExecuteAsync(
                        connection,
                        $"grant select on public.schema_migration to {AppRoleName}",
                        TestContext.Current.CancellationToken);
                    await InsertJournalRowAsync(connection, head, "test-checksum");
                }

                return Builder(adminConnectionString, database, AppRoleName, _appPassword);
            });

    /// <summary>
    /// Revokes the journal <c>SELECT</c> grant in the database
    /// <paramref name="appConnectionString"/> points at, to prove a lost grant is
    /// reported as <c>check_failed</c> rather than as an unreachable database (D8).
    /// </summary>
    public static async Task RevokeJournalSelectAsync(string appConnectionString)
    {
        await using var connection = await OpenAsync(Builder(appConnectionString, admin: true));
        await ExecuteAsync(
            connection,
            $"revoke select on public.schema_migration from {AppRoleName}",
            TestContext.Current.CancellationToken);
    }

    /// <summary>The migrated template every copied test database is copied from.</summary>
    private TemplateDatabase Template =>
        _template ?? throw new InvalidOperationException("the fixture has not started its container");

    /// <summary>
    /// A database copied from one template brought to head by the real
    /// <c>Evidgate.Migrator</c> — the same bootstrap tests/Tasks.IntegrationTests
    /// uses (01.4.1 §9). The Api connects as <c>evidgate_app</c>, whose credentials
    /// were set on the cluster role; the scope's connection string is that role's,
    /// and the database is dropped when the scope is disposed.
    /// </summary>
    public Task<TestDatabase> CreateMigratedDatabaseAsync() =>
        CreateScopedAsync(
            "migrated_",
            async database =>
            {
                await Template.CreateDatabaseAsync(database);
                return AppConnectionString(database);
            });

    /// <summary>
    /// Creates one per-test database through <paramref name="create"/> and drops it again when the
    /// arrange itself fails, so a failed test never leaves a scratch database for the rest of the run.
    /// </summary>
    private async Task<TestDatabase> CreateScopedAsync(string prefix, Func<string, Task<string>> create)
    {
        ArgumentNullException.ThrowIfNull(create);

        await EnsureStartedAsync();

        var database = prefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..10];
        try
        {
            return new TestDatabase(this, database, await create(database));
        }
        catch
        {
            await DropDatabaseAsync(database);
            throw;
        }
    }

    /// <summary>Drops one test database; a leftover scratch database never fails a run.</summary>
    internal async Task DropDatabaseAsync(string database)
    {
        if (_template is { } template)
        {
            await template.DropDatabaseAsync(database);
        }
    }

    /// <summary>The <c>evidgate_owner</c> connection string for one test database (migrations, seeding).</summary>
    public string OwnerConnectionString(string database) =>
        Unpooled(new NpgsqlConnectionStringBuilder(Builder(ConnectionString, database))
        {
            Username = "evidgate_owner",
            Password = _ownerPassword,
        }.ConnectionString);

    /// <summary>The <c>evidgate_app</c> connection string for one test database.</summary>
    public string AppConnectionString(string database) =>
        Builder(ConnectionString, database, AppRoleName, _appPassword);

    /// <summary>Unpooled superuser connection string for a database in this container.</summary>
    public string AdminConnectionString(string database) =>
        Unpooled(Builder(ConnectionString, database, admin: true));

    private static string Unpooled(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    private PostgreSqlContainer Container =>
        _container ?? throw new InvalidOperationException("the fixture has not started its container");

    private async Task EnsureAppRoleAsync()
    {
        await using var connection = await OpenAsync(ConnectionString);
        await ExecuteAsync(
            connection,
            $"""
            do $$
            begin
                if not exists (select 1 from pg_roles where rolname = '{AppRoleName}') then
                    create role {AppRoleName} login password '{_appPassword}';
                else
                    alter role {AppRoleName} login password '{_appPassword}';
                end if;
            end
            $$;
            """);
    }

    /// <summary>
    /// The migration role's attributes match the bootstrap exactly (LOGIN +
    /// CREATEROLE, nothing that would bypass RLS — DECISIONS F5). The guard keeps
    /// a reused container from failing on a role that already exists.
    /// </summary>
    private async Task EnsureOwnerRoleAsync()
    {
        await using var connection = await OpenAsync(ConnectionString);
        await ExecuteAsync(
            connection,
            $"""
            do $$
            begin
                if not exists (select 1 from pg_roles where rolname = 'evidgate_owner') then
                    create role evidgate_owner login createrole nosuperuser nocreatedb nobypassrls password '{_ownerPassword}';
                else
                    alter role evidgate_owner login password '{_ownerPassword}';
                end if;
            end
            $$;
            """);
    }

    private static async Task InsertJournalRowAsync(NpgsqlConnection connection, string head, string checksum)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "insert into public.schema_migration(name, checksum, duration_ms) values ($1, $2, 0)";
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = head });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = checksum });
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Edits a connection string: another database, or the superuser credentials
    /// (admin) for the DDL/GRANT side of the same database.
    /// </summary>
    private static string Builder(string connectionString, string? database = null, string? user = null, string? password = null, bool admin = false)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (database is not null)
        {
            builder.Database = database;
        }

        if (admin)
        {
            builder.Username = "postgres";
            builder.Password = "postgres";
        }
        else if (user is not null)
        {
            builder.Username = user;
            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>
/// A per-test database that is dropped when the scope is disposed. It is also the
/// connection string the hosts connect with (<c>evidgate_app</c>), so a test passes
/// the scope where a connection string is expected and cannot forget the cleanup.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly PostgresFixture _fixture;

    internal TestDatabase(PostgresFixture fixture, string name, string appConnectionString)
    {
        _fixture = fixture;
        Name = name;
        AppConnectionString = appConnectionString;
    }

    /// <summary>The database name, for the fixture's owner and admin connection-string helpers.</summary>
    public string Name { get; }

    /// <summary>The <c>evidgate_app</c> connection string for this database.</summary>
    public string AppConnectionString { get; }

    public static implicit operator string(TestDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.AppConnectionString;
    }

    public override string ToString() => AppConnectionString;

    public ValueTask DisposeAsync() => new(_fixture.DropDatabaseAsync(Name));
}
