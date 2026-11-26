using Evidgate.BuildingBlocks.Persistence.Tasks;

namespace Evidgate.Modules.Policy.Application.Expiry;

/// <summary>
/// The payload of the hourly expiry sweep. It is empty: the sweep reads the due rows from the database, and the page
/// sizes are the constants of <see cref="ExpirySweeps"/>, so a payload can never widen a fire.
/// </summary>
internal sealed record SweepExpiredExceptionsCommand;

/// <summary>
/// The names of the waiver and quarantine expiry sweep. A platform task: the scan spans tenants, so the handler lists
/// the orgs with due rows and opens one tenant scope per org.
/// </summary>
internal static class ExpirySweeps
{
    /// <summary>The schedule's name; with the fire time it forms the unique fire key.</summary>
    public const string Name = "waiver_and_quarantine_expiry";

    /// <summary>The task type the schedule enqueues and the handler consumes.</summary>
    public const string TaskType = "policy.expire_exceptions";

    /// <summary>Hourly at minute 7: an exception ends at most an hour late, off the round hour so it avoids the other sweeps.</summary>
    public const string Cron = "7 * * * *";

    /// <summary>Orgs listed per page of the cross-tenant scan.</summary>
    public const int OrgBatch = 100;

    /// <summary>Rows moved per tenant transaction.</summary>
    public const int RowBatch = 200;

    /// <summary>The queue the schedule enqueues into.</summary>
    public static string Queue => EvidgateSweeps.MaintenanceQueue;

    /// <summary>The schedule registered with the mechanism.</summary>
    public static ScheduleDefinition Expire { get; } = new(
        name: Name,
        cron: Cron,
        queue: EvidgateSweeps.MaintenanceQueue,
        taskType: TaskType,
        payloadFactory: static _ => "{}");
}
