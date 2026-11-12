using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// The keyed boundary of the task machinery: the outbox dispatcher and every queue
/// transition open connections as <c>evidgate_worker</c>, the only role with DML on
/// <c>public.task</c> and <c>public.outbox</c>. A producer keeps the app role, which
/// holds <c>INSERT</c> alone, so the seat of the queue is a grant rather than a
/// convention.
/// </summary>
/// <remarks>
/// Registered by <c>AddEvidgateTasks</c> alone. The Api composes the same
/// <see cref="TasksOptions"/> but never this source, so it never needs the worker
/// credential; the Worker resolves it at boot and refuses to start without it.
/// </remarks>
public static class TaskWorkerDataSource
{
    /// <summary>Service key of the worker-role <see cref="NpgsqlDataSource"/>.</summary>
    public const string Key = "evidgate_worker";

    /// <summary>The only role the configured connection string may name.</summary>
    public const string Role = "evidgate_worker";

    /// <summary>
    /// The <see cref="NpgsqlDataSource.Name"/> of the worker pool: Npgsql exports it as
    /// <c>db.client.connection.pool.name</c>, and the driver's default would put the
    /// host, user and database of the worker role on a metric label.
    /// </summary>
    public const string DataSourceName = "evidgate_worker";

    /// <summary>Key an operator has to set; every failure message names it.</summary>
    internal const string ConfigurationKey = SecretNames.TasksWorkerConnectionString;

    /// <summary>Builds the worker-role source from the resolved option.</summary>
    /// <param name="options">The bound task options, with the secret already resolved.</param>
    public static NpgsqlDataSource Create(TasksOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return Create(options.WorkerConnectionString);
    }

    /// <summary>Builds the worker-role source from a connection string.</summary>
    /// <param name="connectionString">The resolved secret value.</param>
    /// <exception cref="InvalidOperationException">
    /// The connection string is unset, malformed, or names another role. The message names
    /// the configuration key and never echoes the value, which carries a password.
    /// </exception>
    public static NpgsqlDataSource Create(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"{ConfigurationKey} is not set: the outbox dispatcher and the task queue "
                + $"write as {Role}, the only role with DML on public.task and public.outbox");
        }

        try
        {
            // NpgsqlConnectionStringBuilder splits the credential out before any message is
            // built, so a malformed value fails the boot naming the key — never an
            // ArgumentException whose text can quote the Password= fragment.
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (!string.Equals(builder.Username, Role, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{ConfigurationKey} must connect as {Role} but names "
                    + $"'{builder.Username ?? "<none>"}': only that role holds the queue's DML");
            }

            return new NpgsqlDataSourceBuilder(builder.ConnectionString)
            {
                Name = DataSourceName,
            }.Build();
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                $"{ConfigurationKey} is not a valid connection string (check quoting and keywords; "
                + "the value is not echoed because it carries a password).");
        }
    }
}
