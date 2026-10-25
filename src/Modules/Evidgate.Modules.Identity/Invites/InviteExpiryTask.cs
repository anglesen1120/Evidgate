using Dapper;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Identity.Invites;

/// <summary>Payload of the <c>invite-expiry</c> task.</summary>
/// <param name="Limit">Rows expired per batch (the function caps it at 10 000).</param>
internal sealed record InviteExpiryPayload(int Limit);

/// <summary>
/// Names of the invite expiry sweep (02.1.2 §7): a membership stays <c>pending</c>
/// until its invite lapses, and the sweep is what moves the past-due ones to
/// <c>expired</c> so the partial unique index frees the address for a re-invite.
/// </summary>
internal static class InviteExpirySweeps
{
    public const string TaskType = "invite-expiry";

    /// <summary>
    /// Hourly, off the hour: an invite lapses after seven days, so an hour of lag is
    /// invisible to a human, while a busy org can accumulate invites between fires.
    /// </summary>
    public const string Cron = "5 * * * *";

    /// <summary>Rows one <c>evidgate.expire_pending_invites</c> call expires (the function caps it at 10 000).</summary>
    public const int BatchSize = 10_000;

    /// <summary>
    /// Batches one fire may drain before it stops; whatever is left waits for the next
    /// hourly fire, and the log line counts only what this fire expired (nothing marks
    /// the cap as hit). Bounded so a backlog never holds one long lock, and a fire never
    /// outlives the lease it holds.
    /// </summary>
    public const int MaxBatchesPerFire = 50;

    public static ScheduleDefinition Expire { get; } = new(
        name: TaskType,
        cron: Cron,
        queue: EvidgateSweeps.MaintenanceQueue,
        taskType: TaskType,
        payloadFactory: _ => """{"limit":10000}""");
}

/// <summary>
/// Expires memberships whose invite has lapsed (<c>status = 'pending'</c> and
/// <c>invite_expires_at &lt; now()</c>) through the owner-owned definer function
/// <c>evidgate.expire_pending_invites</c>, in bounded batches.
/// <para>
/// A platform task: it runs as the sweep role, which is the only role with
/// <c>EXECUTE</c>, and it never takes a tenant scope — the sweep has to see every
/// org's past-due invites, and the function is what owns the status transition.
/// </para>
/// </summary>
internal sealed partial class InviteExpiryTaskHandler(
    IWorkerSchedDataSource sweep,
    ILogger<InviteExpiryTaskHandler> logger) : ITaskHandler<InviteExpiryPayload>
{
    private const string ExpireCall = "select evidgate.expire_pending_invites(@limit)";

    public string TaskType => InviteExpirySweeps.TaskType;

    public bool RequiresTenant => false;

    public async ValueTask HandleAsync(InviteExpiryPayload payload, ITenantScope? scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        await using var connection = await sweep.OpenAsync(cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(payload.Limit, 1, InviteExpirySweeps.BatchSize);
        var expired = 0;

        for (var batch = 0; batch < InviteExpirySweeps.MaxBatchesPerFire; batch++)
        {
            var flipped = await connection.ExecuteScalarAsync<int>(
                new CommandDefinition(ExpireCall, new { limit }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            expired += flipped;

            if (flipped < limit)
            {
                break;
            }
        }

        // A count only: the sweep sees every org's rows, so an id, an address or the
        // org would put tenant data in a platform log line.
        LogExpired(expired, limit);
    }

    [LoggerMessage(
        EventId = 2411,
        Level = LogLevel.Information,
        Message = "Audited sweep evidgate.expire_pending_invites as evidgate_worker_sched expired {Expired} past-due invites (batch limit {Limit})")]
    private partial void LogExpired(int expired, int limit);
}
