using Dapper;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Identity.Ci;

/// <summary>Payload of the <c>ci_token_issue.purge</c> task.</summary>
/// <param name="Limit">Rows deleted per batch (the function caps it at 10 000).</param>
internal sealed record CiTokenIssuePurgePayload(int Limit);

/// <summary>Names of the replay-cache purge (02.4.1-T4.1, TL T7).</summary>
internal static class CiTokenIssueSweeps
{
    public const string TaskType = "ci_token_issue.purge";

    /// <summary>Hourly, off the hour: a busy org mints more than one daily batch (50k) of replay records.</summary>
    public const string Cron = "20 * * * *";

    public const int BatchSize = 10_000;

    public const int MaxBatchesPerFire = 50;

    public static ScheduleDefinition Purge { get; } = new(
        name: TaskType,
        cron: Cron,
        queue: EvidgateSweeps.MaintenanceQueue,
        taskType: TaskType,
        payloadFactory: _ => "{\"limit\":10000}");
}

/// <summary>
/// Deletes <c>ci_token_issue</c> rows whose replay window has closed (the OIDC lifetime plus 10 minutes, T7).
/// A platform task: it runs as the sweep role through an owner-owned definer function, in bounded batches, so a
/// large backlog never holds one long lock.
/// </summary>
internal sealed partial class CiTokenIssuePurgeTaskHandler(
    IWorkerSchedDataSource sweep,
    ILogger<CiTokenIssuePurgeTaskHandler> logger) : ITaskHandler<CiTokenIssuePurgePayload>
{
    private const string PurgeCall = "select evidgate.purge_expired_ci_token_issue(@limit)";

    public string TaskType => CiTokenIssueSweeps.TaskType;

    public bool RequiresTenant => false;

    public async ValueTask HandleAsync(CiTokenIssuePurgePayload payload, ITenantScope? scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        await using var connection = await sweep.OpenAsync(cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(payload.Limit, 1, EvidgateSweeps.PurgeMaxBatchSize);
        var removed = 0;

        for (var batch = 0; batch < CiTokenIssueSweeps.MaxBatchesPerFire; batch++)
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
        EventId = 2410,
        Level = LogLevel.Information,
        Message = "Audited sweep evidgate.purge_expired_ci_token_issue as evidgate_worker_sched removed {Removed} expired replay records (batch limit {Limit})")]
    private partial void LogPurged(int removed, int limit);
}
