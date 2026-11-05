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
/// The roll-up's schedule: a platform task on the maintenance queue that recomputes the aggregate rows of every org
/// with ledger rows. The unique <c>(schedule_name, fire_at)</c> key is the whole election (00 §5.3), so every worker
/// registers the schedule and exactly one row per fire exists.
/// </summary>
internal static class UsageRollupSweeps
{
    /// <summary>The task type the handler serves, which is also the schedule name.</summary>
    public const string TaskType = "usage_rollup.compute";

    /// <summary>
    /// Daily at 02:23 UTC: off-peak, and off the minutes the mechanism's own sweeps use, so a fire never has to wait
    /// behind a purge. A fixed UTC hour, never a local one, because the cron parser and the fire key are UTC.
    /// </summary>
    public const string Cron = "23 2 * * *";

    /// <summary>The maintenance queue: the recompute is platform work, not one tenant's task.</summary>
    public const string Queue = EvidgateSweeps.MaintenanceQueue;

    /// <summary>Org ids listed per page through the sweep role's definer.</summary>
    public const int OrgBatch = 100;

    /// <summary>
    /// Pages one fire may drain. A fire that reaches the bound leaves the rest to the next one; the keyset cursor
    /// restarts at the beginning, so no org is recomputed twice by one fire.
    /// </summary>
    public const int MaxPagesPerFire = 10;

    /// <summary>The schedule definition: one row per fire, payload naming the page size.</summary>
    public static ScheduleDefinition Compute { get; } = new(
        name: TaskType,
        cron: Cron,
        queue: Queue,
        taskType: TaskType,
        payloadFactory: _ => $$"""{"org_batch":{{OrgBatch}}}""");
}
