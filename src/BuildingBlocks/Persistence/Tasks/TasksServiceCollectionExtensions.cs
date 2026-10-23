using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Secrets;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// Registration of the durable task queue (01.5.1-T1): claim, lease, dispatch,
/// schedule runner and the story's own sweeps, plus the queue options bound from
/// <c>EVIDGATE__TASKS__*</c>.
/// </summary>
/// <remarks>
/// The hosted services that drive the loops live in the host
/// (<c>Evidgate.Worker</c>), not here: this assembly is the mechanism, and the
/// process lifecycle belongs to the entry point.
/// </remarks>
public static class TasksServiceCollectionExtensions
{
    /// <summary>
    /// Registers the task queue over the app-role <c>NpgsqlDataSource</c> that
    /// <c>AddEvidgatePersistence</c> put in the container, and the
    /// <c>evidgate_worker_sched</c> connection a sweep calls its owner function
    /// with.
    /// </summary>
    public static IServiceCollection AddEvidgateTasks(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // 10.1.1-T2: the sweep connection string is a secret, so the provider and
        // its post-configurer are registered here too — a host that composes the
        // queue without persistence still resolves it through ISecretProvider.
        services.AddEvidgateSecrets(configuration);

        services
            .AddOptions<TasksOptions>()
            .Bind(configuration.GetSection(TasksOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.Queues.All(queue => !string.IsNullOrWhiteSpace(queue.Name) && queue.Name.Length <= 64),
                "every EVIDGATE__TASKS__QUEUES__*__NAME must be a non-empty queue name of at most 64 characters")
            .Validate(
                options => options.BackoffMaxSeconds >= options.BackoffBaseSeconds,
                "EVIDGATE__TASKS__BACKOFFMAXSECONDS must be at least EVIDGATE__TASKS__BACKOFFBASESECONDS")
            .Validate(
                options => options.QueueRoutes.All(route =>
                    !string.IsNullOrWhiteSpace(route.TaskType) && route.TaskType.Length <= 128
                    && !string.IsNullOrWhiteSpace(route.Queue) && route.Queue.Length <= 64),
                "every EVIDGATE__TASKS__QUEUEROUTES__* entry needs a non-empty TASKTYPE (≤128 characters) and QUEUE (≤64 characters)")
            .Validate(
                options => options.QueueRoutes.Select(route => route.TaskType).Distinct(StringComparer.Ordinal).Count() == options.QueueRoutes.Count,
                "EVIDGATE__TASKS__QUEUEROUTES__*__TASKTYPE must be unique — a task_type cannot dispatch to two queues")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.DispatchDefaultQueue) && options.DispatchDefaultQueue.Length <= 64,
                "EVIDGATE__TASKS__DISPATCHDEFAULTQUEUE must be a non-empty queue name of at most 64 characters")
            .ValidateOnStart();

        // The consumer opens the tenant scope of a tenant task from the row's
        // org_id (00 §5.4.2 covers a task exactly as it covers a request), so the
        // tenancy path is a prerequisite of this registration, not of the host: the
        // Worker composes persistence for its data source and never opens a scope
        // of its own, so without this the consumer would fail to resolve at boot.
        // AddEvidgateTenancy is idempotent (TryAdd*) — a host that registers it
        // itself, as the Api's pipeline eventually will, changes nothing here.
        services.AddEvidgateTenancy();

        services.TryAddSingleton(TimeProvider.System);

        // 01.5.1 §7/T4.1: the default alert sink for every `dead_letter`
        // transition is the logging/counter one — it exists so the hook is never
        // unregistered (the consumer and the claimer call it unconditionally).
        // TryAdd so a host or a test with a real sink wins by registering first.
        services.TryAddSingleton<ITaskAlertSink, LoggingTaskAlertSink>();

        services.TryAddSingleton<ITaskClaimer, TaskClaimer>();
        services.TryAddSingleton<ITaskQueueWriter, TaskQueueWriter>();
        services.TryAddSingleton<ITaskLeaseWriter, TaskLeaseWriter>();
        services.TryAddSingleton<ITaskHandlerRegistry, TaskHandlerRegistry>();
        services.TryAddSingleton<IScheduleRegistry>(provider =>
            new ScheduleRegistry(provider.GetServices<ScheduleDefinition>()));
        services.TryAddSingleton<IWorkerSchedDataSource, WorkerSchedDataSource>();
        services.TryAddSingleton<TaskConsumer>();
        services.TryAddSingleton<TaskScheduleRunner>();
        // 01.5.1-T3: the read/stamp half of the outbox. It belongs to the
        // mechanism, not the outbox writer's registration, because it consumes
        // TasksOptions and the app-role data source and only ever runs in the
        // Worker — a host that composes the write path without the queue gets
        // no dispatcher, which is the correct posture for the Api.
        services.TryAddSingleton<OutboxDispatcher>();

        services.AddTaskHandler<IdempotencyPurgePayload, IdempotencyPurgeTaskHandler>();
        services.AddTaskHandler<TaskSweepPayload, TaskSweepTaskHandler>();
        services.AddTaskHandler<Audit.VerifyAuditChainPayload, Audit.VerifyAuditChainTaskHandler>();

        // 01.5.1 §7: the two schedules this story owns. Registering them here means
        // every Worker fires them without a leader — the unique fire key decides.
        services.AddTaskSchedule(EvidgateSweeps.IdempotencyPurge);
        services.AddTaskSchedule(EvidgateSweeps.TaskSweep);
        services.AddTaskSchedule(Audit.AuditSweeps.VerifyChain);

        return services;
    }

    /// <summary>
    /// Registers one typed handler. Modules call this with their command-side
    /// handler type; the queue only needs its <c>task_type</c> and its payload type.
    /// </summary>
    /// <typeparam name="TPayload">Payload type the handler binds the row's JSON to.</typeparam>
    /// <typeparam name="THandler">Handler implementation.</typeparam>
    public static IServiceCollection AddTaskHandler<TPayload, THandler>(this IServiceCollection services)
        where THandler : class, ITaskHandler<TPayload>
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<THandler>();
        services.AddSingleton<ITaskHandlerInvoker>(provider =>
            new TaskHandlerAdapter<TPayload>(provider.GetRequiredService<THandler>()));

        return services;
    }

    /// <summary>Registers one cron schedule.</summary>
    public static IServiceCollection AddTaskSchedule(this IServiceCollection services, ScheduleDefinition schedule)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(schedule);

        services.AddSingleton(schedule);

        return services;
    }
}
