using Cronos;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// One cron schedule (01.5.1 §5): what fires, into which queue, as which
/// <c>task_type</c>, with which payload — and, because the fire is deduped by
/// <c>(schedule_name, fire_at)</c>, the name is the schedule's identity.
/// </summary>
/// <remarks>
/// All schedules run on UTC. The cron expression is parsed once, here, so a typo
/// fails at registration (a Worker that cannot start) instead of silently never
/// firing, which is the failure mode a schedule is least likely to be noticed in.
/// </remarks>
public sealed class ScheduleDefinition
{
    /// <summary>Creates a schedule, parsing <paramref name="cron"/> eagerly.</summary>
    /// <param name="name">Unique schedule name; half of the fire key and the advisory-lock key.</param>
    /// <param name="cron">Standard 5-field cron expression (`minute hour day month weekday`), UTC.</param>
    /// <param name="queue">Queue its fires are enqueued into.</param>
    /// <param name="taskType">Handler key of the fired task.</param>
    /// <param name="payloadFactory">Builds the payload of one fire from the occurrence.</param>
    /// <exception cref="ArgumentException">A name, queue or expression is missing or malformed.</exception>
    public ScheduleDefinition(
        string name,
        string cron,
        string queue,
        string taskType,
        Func<DateTimeOffset, string> payloadFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(cron);
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskType);
        ArgumentNullException.ThrowIfNull(payloadFactory);

        // The same limits 0008 enforces; failing here names the schedule instead of
        // surfacing as a 23514 on the insert a month later.
        if (name.Length > 128)
        {
            throw new ArgumentException($"schedule name '{name}' is longer than 128 characters (0008 `task_schedule_name_length_check`)", nameof(name));
        }

        if (queue.Length > 64)
        {
            throw new ArgumentException($"schedule '{name}' declares a queue name longer than 64 characters (0008 `task_queue_length_check`)", nameof(queue));
        }

        if (taskType.Length > 128)
        {
            throw new ArgumentException($"schedule '{name}' declares a task_type longer than 128 characters (0008 `task_type_length_check`)", nameof(taskType));
        }

        try
        {
            Expression = CronExpression.Parse(cron, CronFormat.Standard);
        }
        catch (CronFormatException exception)
        {
            throw new ArgumentException($"schedule '{name}' has an invalid cron expression '{cron}': {exception.Message}", nameof(cron), exception);
        }

        Name = name;
        Cron = cron;
        Queue = queue;
        TaskType = taskType;
        PayloadFactory = payloadFactory;
    }

    /// <summary>Unique schedule name; also the dedupe key's first half.</summary>
    public string Name { get; }

    /// <summary>The expression as written, for the registry's own reporting.</summary>
    public string Cron { get; }

    /// <summary>Queue a fire is enqueued into.</summary>
    public string Queue { get; }

    /// <summary>Handler key of the fired task.</summary>
    public string TaskType { get; }

    /// <summary>Builds the payload of one occurrence.</summary>
    public Func<DateTimeOffset, string> PayloadFactory { get; }

    /// <summary>The parsed expression, kept so the runner never re-parses per pass.</summary>
    public CronExpression Expression { get; }
}

/// <summary>
/// The schedules this process knows (01.5.1 §5). Every worker registers the same
/// set from the same composition, so "who fires" is decided by the fire key and
/// never by a leader (00 §5.3).
/// </summary>
public interface IScheduleRegistry
{
    /// <summary>The registered schedules.</summary>
    IReadOnlyList<ScheduleDefinition> Schedules { get; }
}

/// <summary>
/// The registry over the container's schedule definitions. A duplicate name is
/// fatal: two different crons under one name would silently steal each other's
/// fires through the unique key.
/// </summary>
public sealed class ScheduleRegistry : IScheduleRegistry
{
    private readonly List<ScheduleDefinition> _schedules;

    /// <summary>Builds the registry from every registered definition.</summary>
    public ScheduleRegistry(IEnumerable<ScheduleDefinition> schedules)
    {
        ArgumentNullException.ThrowIfNull(schedules);

        _schedules = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var schedule in schedules)
        {
            if (!seen.Add(schedule.Name))
            {
                throw new InvalidOperationException(
                    $"two schedules are registered under the name '{schedule.Name}': the name is the schedule's identity");
            }

            _schedules.Add(schedule);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ScheduleDefinition> Schedules => _schedules;
}
