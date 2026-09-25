using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Readiness;
using Evidgate.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

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

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(ContainerImages.Postgres)
        .WithDatabase("evidgate")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly string _appPassword = "app-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Superuser connection string for the container's default database (DDL and grants).</summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await EnsureAppRoleAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// A fresh database with the journal created, <c>SELECT</c> granted to the
    /// application role and the head set to <paramref name="head"/>; returns the
    /// <c>evidgate_app</c> connection string the hosts must use. The Api/Worker are
    /// ready iff <paramref name="head"/> is at or beyond <see cref="ExpectedHead"/>.
    /// </summary>
    public async Task<string> CreateDatabaseAtHeadAsync(string head)
    {
        var database = "hosts_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..10];

        await using (var connection = await OpenAsync(ConnectionString))
        {
            await ExecuteAsync(connection, $"create database \"{database}\"");
        }

        var adminConnectionString = Builder(ConnectionString, database);
        await using (var connection = await OpenAsync(adminConnectionString))
        {
            await ExecuteAsync(connection, JournalDdl);
            await ExecuteAsync(
                connection,
                $"grant select on public.schema_migration to {AppRoleName}");
            await InsertJournalRowAsync(connection, head, "test-checksum");
        }

        return Builder(adminConnectionString, database, AppRoleName, _appPassword);
    }

    /// <summary>
    /// Revokes the journal <c>SELECT</c> grant in the database
    /// <paramref name="appConnectionString"/> points at, to prove a lost grant is
    /// reported as <c>check_failed</c> rather than as an unreachable database (D8).
    /// </summary>
    public static async Task RevokeJournalSelectAsync(string appConnectionString)
    {
        await using var connection = await OpenAsync(Builder(appConnectionString, admin: true));
        await ExecuteAsync(connection, $"revoke select on public.schema_migration from {AppRoleName}");
    }

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

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}

/// <summary>Binds the container fixture to every test class that needs it (one container per assembly).</summary>
[CollectionDefinition(Name)]
public sealed class PostgresSuite : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres-hosts";
}
