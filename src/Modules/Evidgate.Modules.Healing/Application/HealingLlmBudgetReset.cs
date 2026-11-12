using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Healing.Application;

/// <summary>Payload of the <c>healing.llm_budget_reset</c> task: the fire carries no input, the day is the clock's.</summary>
internal sealed record HealingLlmBudgetResetPayload;

/// <summary>Names of the daily healing LLM budget roll (08.5.1-T1.7).</summary>
internal static class HealingLlmBudgetResetSweeps
{
    /// <summary>The task type the handler serves.</summary>
    public const string TaskType = "healing.llm_budget_reset";

    /// <summary>The schedule's own name; the fire key is <c>(schedule_name, fire_at)</c>, so one fire runs once.</summary>
    public const string ScheduleName = "llm-budget-reset";

    /// <summary>
    /// Daily just after 00:00 UTC. The budget day is a UTC date (the reserve and settle statements key on it), so the
    /// roll lands as the day turns; a late or missed fire costs nothing because a call that finds no row for today is
    /// refused as unprovisioned rather than spent unmeasured.
    /// </summary>
    public const string Cron = "5 0 * * *";

    public static ScheduleDefinition Reset { get; } = new(
        name: ScheduleName,
        cron: Cron,
        queue: EvidgateSweeps.MaintenanceQueue,
        taskType: TaskType,
        payloadFactory: _ => "{}");
}

/// <summary>
/// Opens each project's budget row for the new UTC day through the owner function
/// <c>evidgate.roll_healing_llm_budget</c>, which copies the caps from the latest earlier row and starts the counters at
/// zero.
/// </summary>
/// <remarks>
/// <para>
/// A platform task: the table is a tenant table under FORCE RLS and only <c>evidgate_worker_sched</c> may execute the
/// function, so this handler is the roll's sole path and its Information line is the call's attestation (the function
/// spans orgs, so a per-org audit row could not name them).
/// </para>
/// <para>
/// A new row per day, not an in-place zeroing: a call already in flight holds its estimate on the row of the day it
/// reserved on and settles against that same row (<c>period_start</c> is part of the unique key), so a roll under a
/// running call can neither erase nor double-release the hold. A rerun is a no-op because the function inserts with
/// <c>ON CONFLICT DO NOTHING</c>.
/// </para>
/// </remarks>
/// <param name="sweep">The sweep-role connection source the function is executed on.</param>
/// <param name="clock">The clock the budget day comes from.</param>
/// <param name="logger">Where the count lands; never a project or org id.</param>
internal sealed partial class HealingLlmBudgetResetTaskHandler(
    IWorkerSchedDataSource sweep,
    TimeProvider clock,
    ILogger<HealingLlmBudgetResetTaskHandler> logger) : ITaskHandler<HealingLlmBudgetResetPayload>
{
    /// <inheritdoc />
    public string TaskType => HealingLlmBudgetResetSweeps.TaskType;

    /// <inheritdoc />
    public bool RequiresTenant => false;

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        HealingLlmBudgetResetPayload payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var day = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        await using var connection = await sweep.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "select evidgate.roll_healing_llm_budget(@day)";
        command.Parameters.AddWithValue("day", day);

        var opened = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);

        LogRolled(opened, day);
    }

    [LoggerMessage(
        EventId = 8612,
        Level = LogLevel.Information,
        Message = "Audited sweep evidgate.roll_healing_llm_budget as evidgate_worker_sched opened {Opened} project budget rows for {Day}")]
    private partial void LogRolled(int opened, DateOnly day);
}
