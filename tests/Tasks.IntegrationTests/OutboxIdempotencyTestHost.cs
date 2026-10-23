using Evidgate.BuildingBlocks.Persistence.Idempotency;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Tasks.IntegrationTests;

/// <summary>
/// The production wiring of the outbox and idempotency paths under test
/// (01.5.1-T2.2/T6.1): an app-role <c>NpgsqlDataSource</c>, <c>AddEvidgateTenancy</c>,
/// <c>AddEvidgateOutbox</c> and <c>AddEvidgateIdempotency</c>, exactly as the
/// hosts register the single data-access path. The raw-connection helpers beside
/// it run the owner/superuser/worker_sched assertions the app role deliberately
/// cannot make (0006 grants it INSERT on <c>outbox</c> and nothing else; 0007
/// grants <c>evidgate_worker_sched</c> the purge function and no table access).
/// </summary>
internal sealed class OutboxIdempotencyTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private OutboxIdempotencyTestHost(NpgsqlDataSource dataSource, ServiceProvider provider)
    {
        DataSource = dataSource;
        _provider = provider;
    }

    internal NpgsqlDataSource DataSource { get; }

    internal ITenantScopeFactory Scopes => _provider.GetRequiredService<ITenantScopeFactory>();

    internal ITenantDb Db => _provider.GetRequiredService<ITenantDb>();

    internal IOutboxWriter Outbox => _provider.GetRequiredService<IOutboxWriter>();

    internal IIdempotencyStore Idempotency => _provider.GetRequiredService<IIdempotencyStore>();

    /// <summary>Starts the host over one test database; <paramref name="maximumPoolSize"/> bounds the app-role pool.</summary>
    internal static OutboxIdempotencyTestHost Start(
        PostgresTasksFixture fixture,
        string database,
        int maximumPoolSize = 1)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var dataSource = fixture.OpenAppDataSource(database, maximumPoolSize);
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddEvidgateTenancy();
        services.AddEvidgateOutbox();
        services.AddEvidgateIdempotency();

        return new OutboxIdempotencyTestHost(dataSource, services.BuildServiceProvider());
    }

    /// <summary>Opens a connection with an explicit connection string (owner, superuser or sweep role).</summary>
    internal static async Task<NpgsqlConnection> OpenAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>Runs a statement and returns the number of affected rows.</summary>
    internal static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken = default,
        params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Runs a query and returns its first column of the first row.</summary>
    internal static async Task<T?> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken = default,
        params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? default : (T)value;
    }

    private static NpgsqlCommand Command(
        NpgsqlConnection connection,
        string sql,
        (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await DataSource.DisposeAsync();
    }
}
