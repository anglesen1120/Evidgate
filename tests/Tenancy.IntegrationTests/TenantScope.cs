using System.Globalization;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// One explicit transaction opened as an application role with the tenant setting
/// applied (00 §5.4.2: the parameterised form of <c>SET LOCAL</c>), so a test
/// exercises exactly the path the API uses. <c>set_config(…, true)</c> is
/// transaction-scoped, which is also what makes "no tenant context" testable: a
/// release of the transaction must leave no tenant behind.
/// </summary>
internal sealed class TenantScope : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    private TenantScope(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    /// <summary>Begins a transaction for the role in <paramref name="connectionString"/> and sets the tenant context.</summary>
    internal static async Task<TenantScope> BeginAsync(
        string connectionString,
        Guid? orgId = null,
        Guid? projectId = null)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        var scope = new TenantScope(connection, transaction);

        if (orgId is { } org)
        {
            await scope.SetAsync("evidgate.org_id", org.ToString("D", CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }

        if (projectId is { } project)
        {
            await scope.SetAsync("evidgate.project_id", project.ToString("D", CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
        }

        return scope;
    }

    /// <summary>Sets a tenant GUC inside this transaction, parameterised (00 §5.7 S1).</summary>
    internal async Task SetAsync(string key, string value) =>
        await ScalarAsync<string>("select set_config(@key, @value, true)", ("key", key), ("value", value))
            .ConfigureAwait(false);

    /// <summary>Runs a statement and returns the affected row count.</summary>
    internal async Task<int> ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(sql, parameters);
        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a scalar query.</summary>
    internal async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(sql, parameters);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? default! : (T)value;
    }

    /// <summary>Runs a statement that must be rejected and returns the server error.</summary>
    internal async Task<PostgresException> CaptureErrorAsync(string sql, params (string Name, object Value)[] parameters)
    {
        var error = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await using var command = Command(sql, parameters);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        });

        return error;
    }

    /// <summary>
    /// Commits this transaction. A scope that only reads (or that deliberately
    /// asserts an error) never calls it: <see cref="DisposeAsync"/> rolls back, so
    /// a test can never leave a row behind by accident.
    /// </summary>
    internal Task CommitAsync() => _transaction.CommitAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private NpgsqlCommand Command(string sql, (string Name, object Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}
