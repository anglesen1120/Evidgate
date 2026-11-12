using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Secrets;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// Registration of the durable task queue: claim, lease, dispatch,
/// schedule runner and its own sweeps, plus the queue options bound from
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
    /// Registers the task queue: the claim/lease/dispatch machinery on the keyed
    /// <c>evidgate_worker</c> data source, plus the sweep role's own connection.
    /// </summary>
    public static IServiceCollection AddEvidgateTasks(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // The sweep connection string is a secret, so the provider and
        // its post-configurer are registered here too — a host that composes the
        // queue without persistence still resolves it through ISecretProvider.
        services.AddEvidgateSecrets(configuration);

        services.AddEvidgateTaskOptions(configuration);

        // The queue's own role, resolved when first used: public.task and public.outbox
        // are writable by evidgate_worker alone, so the dispatcher, the claim/lease path
        // and the retention delete open this source. Only a Worker composes this method,
        // which is what keeps the credential out of the Api.
        services.AddKeyedSingleton<NpgsqlDataSource>(
            TaskWorkerDataSource.Key,
            static (provider, _) => TaskWorkerDataSource.Create(
                provider.GetRequiredService<IOptions<TasksOptions>>().Value));

        // The consumer opens the tenant scope of a tenant task from the row's
        // org_id (a task row is tenant-scoped exactly as a request is), so the
        // tenancy path is a prerequisite of this registration, not of the host: the
        // Worker composes persistence for its data source and never opens a scope
        // of its own, so without this the consumer would fail to resolve at boot.
        // AddEvidgateTenancy is idempotent (TryAdd*) — a host that registers it
        // itself, as the Api's pipeline eventually will, changes nothing here.
        services.AddEvidgateTenancy();

        services.TryAddSingleton(TimeProvider.System);

        // The default alert sink for every `dead_letter`
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
        // The read/stamp half of the outbox. It belongs to the
        // mechanism, not the outbox writer's registration, because it consumes
        // TasksOptions and the keyed worker data source and only ever runs in the
        // Worker — a host that composes the write path without the queue gets
        // no dispatcher, which is the correct posture for the Api.
        services.TryAddSingleton<OutboxDispatcher>();

        services.AddTaskHandler<IdempotencyPurgePayload, IdempotencyPurgeTaskHandler>();
        services.AddTaskHandler<AuthoringChangePurgePayload, AuthoringChangePurgeTaskHandler>();
        services.AddTaskHandler<TaskSweepPayload, TaskSweepTaskHandler>();
        services.AddTaskHandler<Audit.VerifyAuditChainPayload, Audit.VerifyAuditChainTaskHandler>();

        // The two schedules the queue owns. Registering them here means
        // every Worker fires them without a leader — the unique fire key decides.
        services.AddTaskSchedule(EvidgateSweeps.IdempotencyPurge);
        services.AddTaskSchedule(AuthoringChangeSweeps.Purge);
        services.AddTaskSchedule(EvidgateSweeps.TaskSweep);
        services.AddTaskSchedule(Audit.AuditSweeps.VerifyChain);

        return services;
    }

    /// <summary>
    /// Binds and validates the <c>EVIDGATE__TASKS__*</c> group — the queues, the routes and the default queue — without
    /// registering the queue's machinery.
    /// </summary>
    /// <remarks>
    /// A Worker composes <see cref="AddEvidgateTasks"/>; a host that only writes a task row reads the same routing decision
    /// the Worker consumes, and binding nothing there would write into <c>default</c> while the deployment polls a route.
    /// </remarks>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host's configuration; the same environment the Worker reads.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddEvidgateTaskOptions(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<TasksOptions>()
            .Bind(configuration.GetSection(TasksOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.Queues.All(queue => !string.IsNullOrWhiteSpace(queue.Name) && queue.Name.Length <= 64),
                "every EVIDGATE__TASKS__QUEUES__*__NAME must be a non-empty queue name of at most 64 characters")
            // ValidateDataAnnotations never looks inside a list, so the per-queue
            // [Range] is dead here and a zero value stopped the Worker instead of the
            // boot. The numbers repeat it because check-env-matrix.mjs reads them there.
            .Validate(
                options => options.Queues.All(queue => queue.Concurrency >= 1 && queue.Concurrency <= 64),
                "EVIDGATE__TASKS__QUEUES__*__CONCURRENCY must be between 1 and 64")
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

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TasksOptions>, PurgeLeaseGuardValidator>());

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

    /// <summary>
    /// Registers one typed handler that opens and commits its own short tenant transactions, so the attempt can make
    /// the network call (object store, HTTP) the platform forbids inside a database transaction. The
    /// consumer supplies the row's tenant and marks the row's outcome exactly as it does for a scoped handler.
    /// </summary>
    /// <typeparam name="TPayload">Payload type the handler binds the row's JSON to.</typeparam>
    /// <typeparam name="THandler">Handler implementation.</typeparam>
    public static IServiceCollection AddTransactionlessTaskHandler<TPayload, THandler>(this IServiceCollection services)
        where THandler : class, ITransactionlessTaskHandler<TPayload>
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<THandler>();
        services.AddSingleton<ITaskHandlerInvoker>(provider =>
            new TransactionlessTaskHandlerAdapter<TPayload>(provider.GetRequiredService<THandler>()));

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
