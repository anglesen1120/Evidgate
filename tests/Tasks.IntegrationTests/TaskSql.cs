using System.Globalization;
using Npgsql;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// Raw SQL for the tests' own arrange/assert steps. It deliberately does not use the
/// mechanism's own components: a test that seeds a task row through
/// <c>ITaskQueueWriter</c> and then asserts through the same writer is testing the
/// writer against itself. Seeding and reading go through plain Npgsql, and only the
/// behaviour under test goes through the production path.
/// </summary>
internal static class TaskSql
{
    internal static async Task<int> ExecuteAsync(
        string connectionString,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        return await ExecuteAsync(connection, sql, parameters);
    }

    internal static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);

        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    internal static async Task<T?> ScalarAsync<T>(
        string connectionString,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        return await ScalarAsync<T>(connection, sql, parameters);
    }

    /// <summary>
    /// Reads one value on a connection the test already opened, for the bootstrap roles whose
    /// connection string the fixture keeps private (the superuser assertion path).
    /// </summary>
    internal static async Task<T?> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        return value is null or DBNull ? default : (T)value;
    }

    internal static async Task<IReadOnlyList<T>> QueryAsync<T>(
        string connectionString,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = Command(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        var rows = new List<T>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((T)reader.GetValue(0));
        }

        return rows;
    }

    /// <summary>
    /// Reads the first row's first two columns, for a one-row catalogue probe
    /// (`pg_stat_user_tables` counters and the like).
    /// </summary>
    internal static async Task<(TFirst First, TSecond Second)> QueryRowAsync<TFirst, TSecond>(
        string connectionString,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = Command(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        if (!await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            throw new InvalidOperationException($"'{sql}' returned no row");
        }

        return ((TFirst)reader.GetValue(0), (TSecond)reader.GetValue(1));
    }

    /// <summary>
    /// Seeds one task row directly, so a test can start from any state the state
    /// machine defines (queued, active with a live or expired lease, done, …).
    /// </summary>
    internal static Task<int> SeedTaskAsync(
        string connectionString,
        string queue,
        string taskType,
        string payload = "{}",
        Guid? orgId = null,
        string state = "queued",
        int attempts = 0,
        int maxAttempts = 8,
        string? idempotencyKey = null,
        string? scheduleName = null,
        DateTime? fireAt = null,
        DateTime? availableAt = null,
        DateTime? lockedUntil = null,
        DateTime? createdAt = null) =>
        ExecuteAsync(
            connectionString,
            """
            insert into public.task
              (id, org_id, queue, task_type, payload, idempotency_key, schedule_name, fire_at,
               state, attempts, max_attempts, available_at, locked_until, created_at)
            values
              (@id, @org_id, @queue, @task_type, @payload::jsonb, @idempotency_key, @schedule_name, @fire_at,
               @state, @attempts, @max_attempts, coalesce(@available_at, now()), @locked_until, coalesce(@created_at, now()))
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("queue", queue),
            ("task_type", taskType),
            ("payload", payload),
            ("idempotency_key", idempotencyKey ?? $"seed:{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}"),
            ("schedule_name", scheduleName),
            ("fire_at", fireAt),
            ("state", state),
            ("attempts", attempts),
            ("max_attempts", maxAttempts),
            ("available_at", availableAt),
            ("locked_until", lockedUntil),
            ("created_at", createdAt));

    private static NpgsqlCommand Command(
        NpgsqlConnection connection,
        string sql,
        (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(name, value ?? DBNull.Value));
        }

        return command;
    }
}
