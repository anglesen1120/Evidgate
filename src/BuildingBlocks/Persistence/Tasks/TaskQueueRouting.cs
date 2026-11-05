namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// The one rule that turns a <c>task_type</c> into a queue: its <see cref="TasksOptions.QueueRoutes"/> entry when the
/// deployment has one, else <see cref="TasksOptions.DispatchDefaultQueue"/>.
/// </summary>
/// <remarks>
/// A task_type carries no queue of its own, and the Worker consumes only the queues it is configured for, so this rule is
/// stated once and shared: the outbox dispatcher takes its parameter pair from <see cref="Routes"/>, and a producer that
/// writes a row directly asks <see cref="QueueFor"/>.
/// </remarks>
public static class TaskQueueRouting
{
    /// <summary>
    /// The route list as the dispatcher's batched insert takes it, in configuration order. Options validation rejects a
    /// duplicate <c>task_type</c>, so the two arrays cannot disagree about which queue a type lands in.
    /// </summary>
    /// <param name="options">The bound <c>EVIDGATE__TASKS__*</c> group.</param>
    /// <returns>The task types and, in the same positions, their queues.</returns>
    public static (string[] TaskTypes, string[] Queues) Routes(TasksOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return (
            [.. options.QueueRoutes.Select(route => route.TaskType)],
            [.. options.QueueRoutes.Select(route => route.Queue)]);
    }

    /// <summary>The queue a row of <paramref name="taskType"/> is written to.</summary>
    /// <param name="options">The bound <c>EVIDGATE__TASKS__*</c> group.</param>
    /// <param name="taskType">The <c>task_type</c> the row will carry.</param>
    /// <returns>The configured route's queue, else <see cref="TasksOptions.DispatchDefaultQueue"/>.</returns>
    public static string QueueFor(TasksOptions options, string taskType)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskType);

        foreach (var route in options.QueueRoutes)
        {
            if (string.Equals(route.TaskType, taskType, StringComparison.Ordinal))
            {
                return route.Queue;
            }
        }

        return options.DispatchDefaultQueue;
    }
}
