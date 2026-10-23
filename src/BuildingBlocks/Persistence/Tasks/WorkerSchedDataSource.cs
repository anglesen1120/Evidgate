using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// Opens connections as <c>evidgate_worker_sched</c> (00 §5.4.3, TL T80), the
/// dedicated role for owner <c>SECURITY DEFINER</c> sweeps. A worker has no
/// <c>EXECUTE</c> on such a function through its ordinary app-role connection, so
/// the sweep handlers take this second connection for the function call alone —
/// the boundary is enforced by the database's grants, not by a convention.
/// </summary>
/// <remarks>
/// The credential comes from <see cref="TasksOptions.SweepConnectionString"/>
/// (<see cref="SecretNames.TasksSweepConnectionString"/>). It is optional at boot so a
/// worker without it still consumes ordinary tasks; the first sweep that needs it
/// fails with the key name, which lands in the task's <c>last_error</c> and in the
/// operator's dead-letter list rather than being invisible.
/// </remarks>
public interface IWorkerSchedDataSource
{
    /// <summary>Opens one connection as the sweep role.</summary>
    ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class WorkerSchedDataSource : IWorkerSchedDataSource, IAsyncDisposable
{
    /// <summary>Key an operator has to set; the failure message names it.</summary>
    internal const string ConfigurationKey = SecretNames.TasksSweepConnectionString;

    private readonly NpgsqlDataSource? _dataSource;

    /// <summary>Creates the source, or a fail-closed placeholder when no credential is configured.</summary>
    public WorkerSchedDataSource(IOptions<TasksOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var connectionString = options.Value.SweepConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _dataSource = null;
            return;
        }

        try
        {
            // NpgsqlConnectionStringBuilder does the driver parse with the
            // credential already split out, so a malformed value fails the boot
            // with the key named — never an ArgumentException whose message can
            // echo the Password= fragment, and never a silent mis-connect.
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (builder.Username != "evidgate_worker_sched")
            {
                throw new InvalidOperationException(
                    $"{ConfigurationKey} must connect as evidgate_worker_sched "
                    + $"but names '{builder.Username ?? "<none>"}': the sweep role is the only role with "
                    + "EXECUTE on the owner SECURITY DEFINER functions (TL T80).");
            }

            _dataSource = NpgsqlDataSource.Create(builder);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                $"{ConfigurationKey} is not a valid connection string (check quoting and keywords; the value is not echoed because it carries a password).");
        }
    }

    /// <inheritdoc />
    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (_dataSource is null)
        {
            throw new InvalidOperationException(
                $"{ConfigurationKey} is not set: a sweep cannot call an owner SECURITY DEFINER function as evidgate_worker_sched without it (TL T80)");
        }

        return _dataSource.OpenConnectionAsync(cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() =>
        _dataSource is null ? ValueTask.CompletedTask : _dataSource.DisposeAsync();
}
