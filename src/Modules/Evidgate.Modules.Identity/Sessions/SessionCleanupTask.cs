using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Identity.Sessions;

/// <summary>Payload of the <c>session.cleanup</c> task (02.3.2-T2.1).</summary>
/// <param name="Limit">Rows one batch may stamp and delete; the handler clamps it to the sweep's batch size.</param>
internal sealed record SessionCleanupPayload(int Limit);

/// <summary>
/// Names of the session sweeper (02.3.2 §7): it stamps <c>expired_at</c> on rows past their idle or absolute
/// deadline (reporting only — expiry itself is derived) and hard-deletes rows whose last deadline passed more than
/// 30 days ago, which is the retention rule the story records for the IP and user-agent personal data.
/// </summary>
internal static class SessionCleanupSweeps
{
    /// <summary>The task type the handler serves.</summary>
    public const string TaskType = "session.cleanup";

    /// <summary>
    /// The schedule's own name. It differs from <see cref="TaskType"/> because 02.3.2 §7 names the schedule
    /// <c>session-cleanup</c>; the schedule row and the task row it writes are keyed separately
    /// (<c>unique (schedule_name, fire_at)</c> against <c>task.idempotency_key</c>), so the two names never need to
    /// agree.
    /// </summary>
    public const string ScheduleName = "session-cleanup";

    /// <summary>
    /// Daily at 03:40 UTC: the stamping is reporting only and the hard delete waits for a 30-day-old row, so a day
    /// of lag changes nothing a caller can observe, while a daily fire keeps the two platform-wide scans (no index
    /// carries a past deadline) off the peak.
    /// </summary>
    public const string Cron = "40 3 * * *";

    /// <summary>Rows one batch may touch (the definer caps it at the same figure).</summary>
    public const int BatchSize = 10_000;

    /// <summary>
    /// Batches one fire may drain before it stops; whatever is left waits for the next daily fire. Bounded so a
    /// backlog never holds one long lock and a fire never outlives the lease it holds. A rotation chain of depth
    /// <c>d</c> consumes <c>d</c> of these because the sweep unwinds it one row per call.
    /// </summary>
    public const int MaxBatchesPerFire = 50;

    public static ScheduleDefinition Cleanup { get; } = new(
        name: ScheduleName,
        cron: Cron,
        queue: EvidgateSweeps.MaintenanceQueue,
        taskType: TaskType,
        payloadFactory: _ => """{"limit":10000}""");
}

/// <summary>
/// The <c>session.cleanup</c> sweep (02.3.2 §7, T2.1): bounded batches through
/// <see cref="ISessionRoutesStore.CleanupAsync"/>, which runs the owner-owned definers of migration 0048.
/// </summary>
/// <remarks>
/// <para>
/// A platform task with no tenant scope: <c>public.session</c> carries no <c>org_id</c> (the D12 exception), so
/// there is nothing to scope. The batch itself runs on the <c>evidgate_worker_sched</c> connection the store
/// holds (<see cref="IWorkerSchedDataSource"/>), because <c>EXECUTE</c> on the cleanup definer is granted to that
/// role alone — the <c>CiTokenIssuePurge</c> posture (TL T80). The four route statements of the same store use the
/// app-role data source; only this sweep needs the sweep credential.
/// </para>
/// <para>
/// The loop ends on the first call that comes back EMPTY — stamped plus deleted equal zero — which is
/// what makes the sweep <c>done</c> on an empty table, and it may spend at most
/// <see cref="SessionCleanupSweeps.MaxBatchesPerFire"/> batches before it yields to the next daily fire.
/// A short batch is deliberately NOT a stop signal: the sweep deletes a rotation chain from its tip, one
/// row per call, so a chain of depth <c>d</c> is <c>d</c> calls and one fire must be allowed to make
/// them, or the chain's IP and user-agent data would outlive the 30-day retention by <c>d</c> days.
/// A rerun changes nothing: a stamped row is not stamped again (<c>expired_at is null</c> is part of the
/// predicate) and a deleted row is gone.
/// </para>
/// </remarks>
/// <param name="store">The route store, whose <see cref="ISessionRoutesStore.CleanupAsync"/> owns the SQL.</param>
/// <param name="clock">The clock the fire's one <c>p_now</c> comes from.</param>
/// <param name="logger">Where the count lands — never an id, an address or a user agent.</param>
internal sealed partial class SessionCleanupTaskHandler(
    ISessionRoutesStore store,
    TimeProvider clock,
    ILogger<SessionCleanupTaskHandler> logger) : ITaskHandler<SessionCleanupPayload>
{
    /// <inheritdoc />
    public string TaskType => SessionCleanupSweeps.TaskType;

    /// <inheritdoc />
    public bool RequiresTenant => false;

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        SessionCleanupPayload payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var limit = Math.Clamp(payload.Limit, 1, SessionCleanupSweeps.BatchSize);

        // One instant for the whole fire: every batch judges the same deadline, so a long backlog can never
        // delete a row a later batch just stamped as past 30 days.
        var now = clock.GetUtcNow();
        var stamped = 0;
        var deleted = 0;

        // An EMPTY call is the stop signal, never a short one: the sweep unwinds a rotation chain one
        // row per call (the self-FK keeps a predecessor until its successor is gone), so a chain of
        // depth d is d calls, and stopping at the first batch below `limit` would spread that chain
        // over d daily fires and keep its IP and user-agent data past the 30-day retention.
        // MaxBatchesPerFire still bounds the fire.
        var drained = false;
        for (var batch = 0; batch < SessionCleanupSweeps.MaxBatchesPerFire && !drained; batch++)
        {
            var result = await store.CleanupAsync(now, limit, cancellationToken).ConfigureAwait(false);
            stamped += result.Stamped;
            deleted += result.Deleted;
            drained = result.Stamped + result.Deleted == 0;
        }

        LogCleaned(stamped, deleted, limit);
    }

    [LoggerMessage(
        EventId = 2412,
        Level = LogLevel.Information,
        Message = "Audited sweep evidgate.session_cleanup as evidgate_worker_sched stamped {Stamped} expired sessions and deleted {Deleted} past retention (batch limit {Limit})")]
    private partial void LogCleaned(int stamped, int deleted, int limit);
}
