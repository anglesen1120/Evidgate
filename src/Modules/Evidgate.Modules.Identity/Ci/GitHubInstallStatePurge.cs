using Dapper;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Identity.Ci;

/// <summary>Payload of the <c>github_install_state.purge</c> task.</summary>
/// <param name="Limit">Rows deleted per batch (the function caps it at the platform maximum).</param>
internal sealed record GitHubInstallStatePurgePayload(int Limit);

/// <summary>Names and schedule of the expired install-nonce purge.</summary>
internal static class GitHubInstallStatePurges
{
    public const string TaskType = "github_install_state.purge";

    /// <summary>Daily, off the hour: a nonce lives ten minutes, so a daily sweep is far ahead of the backlog.</summary>
    public const string Cron = "35 3 * * *";

    public const int BatchSize = 5_000;

    public const int MaxBatchesPerFire = 20;

    public static ScheduleDefinition Purge { get; } = new(
        name: TaskType,
        cron: Cron,
        queue: EvidgateSweeps.MaintenanceQueue,
        taskType: TaskType,
        payloadFactory: _ => "{\"limit\":5000}");
}

/// <summary>
/// Deletes expired <c>github_install_state</c> rows through the owner-owned definer function, in bounded batches, so a
/// backlog never holds one long lock. A platform task: it runs as the sweep role and takes no tenant scope.
/// </summary>
internal sealed partial class GitHubInstallStatePurgeTaskHandler(
    IWorkerSchedDataSource sweep,
    ILogger<GitHubInstallStatePurgeTaskHandler> logger) : ITaskHandler<GitHubInstallStatePurgePayload>
{
    private const string PurgeCall = "select evidgate.purge_expired_github_install_state(@limit)";

    public string TaskType => GitHubInstallStatePurges.TaskType;

    public bool RequiresTenant => false;

    public async ValueTask HandleAsync(
        GitHubInstallStatePurgePayload payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        await using var connection = await sweep.OpenAsync(cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(payload.Limit, 1, EvidgateSweeps.PurgeMaxBatchSize);
        var removed = 0;

        for (var batch = 0; batch < GitHubInstallStatePurges.MaxBatchesPerFire; batch++)
        {
            var deleted = await connection.ExecuteScalarAsync<int>(
                new CommandDefinition(PurgeCall, new { limit }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            removed += deleted;

            if (deleted < limit)
            {
                break;
            }
        }

        LogPurged(removed, limit);
    }

    [LoggerMessage(
        EventId = 2411,
        Level = LogLevel.Information,
        Message = "Audited sweep evidgate.purge_expired_github_install_state as evidgate_worker_sched removed {Removed} expired install states (batch limit {Limit})")]
    private partial void LogPurged(int removed, int limit);
}
