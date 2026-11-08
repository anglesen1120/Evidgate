namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// The sweeps this story registers (01.5.1 §7): `idempotency.purge` (T6.2) and
/// `task.sweep` (the 30-day retention of terminal task rows, T11). Both are
/// platform tasks — no tenant, so their rows carry a NULL <c>org_id</c> — and both
/// are ordinary cron fires, so any worker may fire them and the unique fire key
/// decides who does.
/// </summary>
public static class EvidgateSweeps
{
    /// <summary>
    /// Queue both sweeps are enqueued into. They are maintenance, not tenant work,
    /// so they never share a queue with a module's tasks and their concurrency stays
    /// one.
    /// </summary>
    public const string MaintenanceQueue = "maintenance";

    /// <summary>Handler key of the expired-idempotency-record sweep.</summary>
    public const string IdempotencyPurgeTaskType = "idempotency.purge";

    /// <summary>Handler key of the terminal-task retention sweep.</summary>
    public const string TaskSweepTaskType = "task.sweep";

    /// <summary>Schedule name of the idempotency purge (also the fire key's first half).</summary>
    public const string IdempotencyPurgeSchedule = "idempotency.purge";

    /// <summary>Schedule name of the retention sweep.</summary>
    public const string TaskSweepSchedule = "task.sweep";

    /// <summary>
    /// Records one purge call deletes at most (the owner function caps it at 10 000
    /// regardless, 01.5.1-T6.1). A daily schedule passing a modest batch and looping
    /// keeps one short statement per batch instead of one long delete.
    /// </summary>
    public const int PurgeBatchSize = 1000;

    /// <summary>
    /// Largest batch <c>evidgate.purge_expired_idempotency</c> accepts: its body
    /// deletes <c>limit least(greatest(p_limit, 0), 10000)</c> (0007). A drain loop
    /// has to work in the function's units — a payload asking for more than this
    /// would read a capped batch as "the backlog is drained" and stop, and a
    /// non-positive one would ask for zero rows and spin the whole time budget
    /// deleting nothing.
    /// </summary>
    public const int PurgeMaxBatchSize = 10_000;

    /// <summary>
    /// How long one idempotency purge fire may keep draining. It replaces a fixed
    /// batch ceiling that could stop a large backlog while the fire's lease still had
    /// headroom: elapsed time is what the lease actually bounds. The next fire
    /// continues from where this one stopped, and the audit row records that the
    /// budget ended the drain.
    /// </summary>
    public static readonly TimeSpan PurgeTimeBudget = TimeSpan.FromMinutes(4);

    /// <summary>
    /// Batches one authoring-change purge fire may drain before it stops and logs the
    /// remainder; the next fire continues. Only the authoring sweep still uses it:
    /// the idempotency purge drains against <see cref="PurgeTimeBudget"/> instead.
    /// </summary>
    public const int PurgeMaxBatchesPerFire = 50;

    /// <summary>Rows one retention delete statement may remove.</summary>
    public const int TaskSweepBatchSize = 5000;

    /// <summary>Batches one retention fire may drain. 20 × 5000 = 100 000 rows a day.</summary>
    public const int TaskSweepMaxBatchesPerFire = 20;

    /// <summary>
    /// Daily at 03:00 UTC. A fixed UTC hour, never a local one: the cron parser and
    /// the fire key are UTC (00 §5.3), and an hour nobody is deploying in keeps the
    /// sweep away from a deploy window.
    /// </summary>
    public const string IdempotencyPurgeCron = "0 3 * * *";

    /// <summary>Daily at 03:15 UTC, after the purge, so the two sweeps never overlap.</summary>
    public const string TaskSweepCron = "15 3 * * *";

    /// <summary>
    /// `idempotency.purge` (01.5.1-T6.2): deletes rows whose <c>expires_at</c> has
    /// passed through the owner's <c>evidgate.purge_expired_idempotency</c>, called
    /// as <c>evidgate_worker_sched</c> — the only role with <c>EXECUTE</c> (T80).
    /// </summary>
    public static ScheduleDefinition IdempotencyPurge { get; } = new(
        name: IdempotencyPurgeSchedule,
        cron: IdempotencyPurgeCron,
        queue: MaintenanceQueue,
        taskType: IdempotencyPurgeTaskType,
        payloadFactory: _ => TaskPayloadSerializer.Serialize(new IdempotencyPurgePayload(PurgeBatchSize)));

    /// <summary>
    /// `task.sweep`: deletes `done` / `dead_letter` rows older than the retention
    /// window (T11: 30 days). Dead-lettered rows are the operator's working set, so
    /// the window has to be the one the operator list assumes.
    /// </summary>
    public static ScheduleDefinition TaskSweep { get; } = new(
        name: TaskSweepSchedule,
        cron: TaskSweepCron,
        queue: MaintenanceQueue,
        taskType: TaskSweepTaskType,
        payloadFactory: _ => TaskPayloadSerializer.Serialize(new TaskSweepPayload(TerminalRetentionDays)));

    /// <summary>Retention of terminal task rows, in days (T11 / 01.5.1 §3).</summary>
    public const int TerminalRetentionDays = 30;
}

/// <summary>Payload of an <c>idempotency.purge</c> fire (01.5.1-T6.2).</summary>
/// <param name="Limit">Batch size passed to the owner function; it caps it at 10 000 itself.</param>
public sealed record IdempotencyPurgePayload(int Limit);

/// <summary>Payload of a <c>task.sweep</c> fire.</summary>
/// <param name="OlderThanDays">A terminal row older than this many days is deleted.</param>
public sealed record TaskSweepPayload(int OlderThanDays);
