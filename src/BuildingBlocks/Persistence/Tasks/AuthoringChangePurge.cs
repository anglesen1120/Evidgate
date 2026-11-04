using Dapper;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.Logging;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// Names of the retained authoring-change purge: a discarded proposal is kept long enough to explain
/// why a version was never written, then removed; an applied one is review evidence and is never
/// purged.
/// </summary>
public static class AuthoringChangeSweeps
{
    /// <summary>Handler key, which is also the schedule name and the fire key's first half.</summary>
    public const string TaskType = "authoring.change.purge";

    /// <summary>
    /// Daily at 03:30 UTC, after the idempotency (03:00) and task (03:15) sweeps: a fixed UTC hour
    /// outside the deploy window, and no two sweeps overlap.
    /// </summary>
    public const string Cron = "30 3 * * *";

    /// <summary>Rows one call asks the owner function for; it caps the batch itself at 10 000.</summary>
    public const int BatchSize = 1000;

    public static ScheduleDefinition Purge { get; } = new(
        name: TaskType,
        cron: Cron,
        queue: EvidgateSweeps.MaintenanceQueue,
        taskType: TaskType,
        payloadFactory: _ => TaskPayloadSerializer.Serialize(new AuthoringChangePurgePayload(BatchSize)));
}

/// <summary>Payload of an <c>authoring.change.purge</c> fire.</summary>
/// <param name="Limit">Rows deleted per batch; the owner function caps it at 10 000.</param>
public sealed record AuthoringChangePurgePayload(int Limit);

/// <summary>
/// Deletes <c>authoring_change</c> rows whose discard is past the retention window, through the owner
/// <c>SECURITY DEFINER</c> function <c>evidgate.purge_expired_authoring_changes</c>, in bounded
/// batches until the backlog is drained or the fire's batch budget is spent.
/// </summary>
/// <remarks>
/// A platform task: the row carries no org, the function is cross-tenant by design, and
/// <c>evidgate_worker_sched</c> is the only role with EXECUTE on it. This handler is therefore the
/// sweep's sole path, and its Information log line is the call's attestation — the table is a tenant
/// table, so a per-call audit row could not name the orgs a batch spans.
/// </remarks>
public sealed partial class AuthoringChangePurgeTaskHandler(
    IWorkerSchedDataSource sweep,
    ILogger<AuthoringChangePurgeTaskHandler> logger) : ITaskHandler<AuthoringChangePurgePayload>
{
    /// <summary>The one call this handler makes.</summary>
    private const string PurgeFunctionCall = "select evidgate.purge_expired_authoring_changes(@limit)";

    /// <inheritdoc />
    public string TaskType => AuthoringChangeSweeps.TaskType;

    /// <inheritdoc />
    public bool RequiresTenant => false;

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        AuthoringChangePurgePayload payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // The function caps its own batch, so the loop works in its units: a payload above the cap
        // would read a capped batch as "drained", and a non-positive one would spend the whole budget
        // deleting nothing. A task row is replayable by an operator, so the payload is not trusted.
        var limit = Math.Clamp(payload.Limit, 1, EvidgateSweeps.PurgeMaxBatchSize);

        await using var connection = await sweep.OpenAsync(cancellationToken).ConfigureAwait(false);

        var removed = 0;
        for (var batch = 0; batch < EvidgateSweeps.PurgeMaxBatchesPerFire; batch++)
        {
            var deleted = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                PurgeFunctionCall,
                new { limit },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

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
        Message = "Audited sweep evidgate.purge_expired_authoring_changes as evidgate_worker_sched removed {Removed} discarded changes (batch limit {Limit})")]
    private partial void LogPurged(int removed, int limit);
}
