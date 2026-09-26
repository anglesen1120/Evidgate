using System.Diagnostics.CodeAnalysis;
using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// Binds a stored payload to a handler's <typeparamref name="TPayload"/> and runs
/// it (01.5.1 §5). The generic handler never sees a string.
/// </summary>
/// <typeparam name="TPayload">Payload type of the wrapped handler.</typeparam>
public sealed class TaskHandlerAdapter<TPayload> : ITaskHandlerInvoker
{
    private readonly ITaskHandler<TPayload> _handler;

    /// <summary>Wraps one typed handler.</summary>
    public TaskHandlerAdapter(ITaskHandler<TPayload> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    /// <inheritdoc />
    public string TaskType => _handler.TaskType;

    /// <inheritdoc />
    public ValueTask InvokeAsync(string payloadJson, ITenantScope? scope, CancellationToken cancellationToken) =>
        _handler.HandleAsync(TaskPayloadSerializer.Deserialize<TPayload>(payloadJson), scope, cancellationToken);
}

/// <summary>
/// The process's handlers by <c>task_type</c>. Built from the container, so a
/// module only has to register its handler; a duplicate <c>task_type</c> fails at
/// first resolution instead of dispatching to an arbitrary one of the two.
/// </summary>
public sealed class TaskHandlerRegistry : ITaskHandlerRegistry
{
    private readonly Dictionary<string, ITaskHandlerInvoker> _handlers;

    /// <summary>Builds the registry from every registered handler adapter.</summary>
    public TaskHandlerRegistry(IEnumerable<ITaskHandlerInvoker> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        _handlers = new Dictionary<string, ITaskHandlerInvoker>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            if (!_handlers.TryAdd(handler.TaskType, handler))
            {
                throw new InvalidOperationException(
                    $"two task handlers claim the task_type '{handler.TaskType}': a task_type dispatches to exactly one handler");
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> TaskTypes => _handlers.Keys;

    /// <inheritdoc />
    public bool TryGet(string taskType, [NotNullWhen(true)] out ITaskHandlerInvoker? invoker)
    {
        ArgumentNullException.ThrowIfNull(taskType);

        return _handlers.TryGetValue(taskType, out invoker);
    }
}
