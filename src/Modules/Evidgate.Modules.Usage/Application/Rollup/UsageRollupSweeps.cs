using Evidgate.BuildingBlocks.Persistence.Tasks;

namespace Evidgate.Modules.Usage.Application.Rollup;

/// <summary>
/// Payload of a <c>usage_rollup.compute</c> fire.
/// </summary>
/// <param name="OrgBatch">
/// Org ids read per page. The handler clamps it to <see cref="UsageRollupSweeps.OrgBatch"/>, so a payload can only
/// narrow a page and never widen it past what one fire is sized for.
/// </param>
internal sealed record UsageRollupPayload(int OrgBatch);

/// <summary>
/// The roll-up's schedules: a platform task on the maintenance queue that recomputes the aggregate rows of every org
/// with usage. The unique <c>(schedule_name, fire_at)</c> key is the whole election, so every worker registers the
/// schedules and exactly one row per fire exists.
/// </summary>
internal static class UsageRollupSweeps
{
    /// <summary>The task type the handler serves, and the daily schedule's name.</summary>
    public const string TaskType = "usage_rollup.compute";

    /// <summary>
    /// Daily at 02:23 UTC: off-peak, and off the minutes the mechanism's own sweeps use, so a fire never has to wait
    /// behind a purge. A fixed UTC hour, never a local one, because the cron parser and the fire key are UTC.
    /// </summary>
    public const string Cron = "23 2 * * *";

    /// <summary>
    /// The period-close schedule's name: distinct from <see cref="TaskType"/> because the schedule name is the
    /// election key, and two schedules under one name would steal each other's fires through the unique key.
    /// </summary>
    public const string PeriodCloseScheduleName = "usage-rollup-period-close";

    /// <summary>
    /// Monthly on the first day at 00:05 UTC: just after a billing month closes, so the previous month's aggregate is
    /// recomputed while it is still the newest close. The daily fire recomputes every period anyway; this one exists so
    /// a closed period is not left to the next 02:23 pass after a month boundary.
    /// </summary>
    public const string PeriodCloseCron = "5 0 1 * *";

    /// <summary>The maintenance queue: the recompute is platform work, not one tenant's task.</summary>
    public const string Queue = EvidgateSweeps.MaintenanceQueue;

    /// <summary>Org ids listed per page through the sweep role's definer.</summary>
    public const int OrgBatch = 100;

    /// <summary>The daily schedule: one row per fire, payload naming the page size.</summary>
    public static ScheduleDefinition Compute { get; } = new(
        name: TaskType,
        cron: Cron,
        queue: Queue,
        taskType: TaskType,
        payloadFactory: _ => $$"""{"org_batch":{{OrgBatch}}}""");

    /// <summary>The period-close schedule: the same handler, one row at the month boundary.</summary>
    public static ScheduleDefinition ComputeOnPeriodClose { get; } = new(
        name: PeriodCloseScheduleName,
        cron: PeriodCloseCron,
        queue: Queue,
        taskType: TaskType,
        payloadFactory: _ => $$"""{"org_batch":{{OrgBatch}}}""");
}
