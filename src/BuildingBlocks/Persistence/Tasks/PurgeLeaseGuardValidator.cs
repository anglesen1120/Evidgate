using Microsoft.Extensions.Options;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// Refuses a task lease that is not longer than the idempotency purge's drain budget plus its margin.
/// The purge keeps deleting for <see cref="EvidgateSweeps.PurgeTimeBudget"/> under one lease; a shorter
/// lease lets a second worker reclaim the row mid-delete, so the boot fails instead of a comment guarding it.
/// </summary>
public sealed class PurgeLeaseGuardValidator : IValidateOptions<TasksOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, TasksOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var lease = TimeSpan.FromSeconds(options.VisibilityTimeoutSeconds);
        var floor = EvidgateSweeps.PurgeTimeBudget + EvidgateSweeps.PurgeLeaseMargin;

        return lease > floor
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"EVIDGATE__TASKS__VISIBILITYTIMEOUTSECONDS ({options.VisibilityTimeoutSeconds} s) must exceed the "
                + $"idempotency purge time budget of {EvidgateSweeps.PurgeTimeBudget.TotalSeconds:0} s plus a "
                + $"{EvidgateSweeps.PurgeLeaseMargin.TotalSeconds:0} s margin, or a second worker could reclaim the purge mid-drain");
    }
}
