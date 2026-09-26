using System.Globalization;
using Npgsql;
using Xunit;

namespace Isolation.Tests;

/// <summary>
/// One explicit transaction opened as an application role with the tenant
/// setting applied (00 §5.4.2: the parameterised form of <c>SET LOCAL</c>), so a
/// test exercises exactly the path the API uses. <c>set_config(…, true)</c> is
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
        var token = TestContext.Current.CancellationToken;
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(token);
        var scope = new TenantScope(connection, transaction);

        if (orgId is { } org)
        {
            await scope.SetAsync("evidgate.org_id", org.ToString("D", CultureInfo.InvariantCulture));
        }

        if (projectId is { } project)
        {
            await scope.SetAsync("evidgate.project_id", project.ToString("D", CultureInfo.InvariantCulture));
        }

        return scope;
    }

    /// <summary>Sets a tenant GUC inside this transaction, parameterised (00 §5.7 S1).</summary>
    internal async Task SetAsync(string key, string value) =>
        await ScalarAsync<string>("select set_config(@key, @value, true)", ("key", key), ("value", value));

    /// <summary>Runs a statement and returns the affected row count.</summary>
    internal async Task<int> ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(sql, parameters);
        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Runs a scalar query.</summary>
    internal async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(sql, parameters);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is null or DBNull ? default! : (T)value;
    }

    /// <summary>Runs a statement that must be rejected and returns the server error.</summary>
    internal async Task<PostgresException> CaptureErrorAsync(string sql, params (string Name, object Value)[] parameters)
    {
        try
        {
            await ExecuteAsync(sql, parameters);
        }
        catch (PostgresException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"statement was expected to fail but succeeded: {sql}");
    }

    /// <summary>
    /// Commits this transaction. A scope that only reads (or that deliberately
    /// asserts an error) never calls it: <see cref="DisposeAsync"/> rolls back,
    /// so a test can never leave a row behind by accident.
    /// </summary>
    internal Task CommitAsync() => _transaction.CommitAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private NpgsqlCommand Command(string sql, (string Name, object Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}
