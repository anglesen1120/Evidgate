using System.ComponentModel.DataAnnotations;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// The task-queue mechanism's own options, bound from <c>EVIDGATE__TASKS__*</c>
/// (01.5.1-T1.2/T1.3). Every value has a working default, so a host that binds
/// nothing still consumes a sane queue; a deployment tunes concurrency, batching,
/// the lease and the backoff.
/// </summary>
public sealed class TasksOptions
{
    /// <summary>Configuration section bound to <c>EVIDGATE__TASKS__*</c>.</summary>
    public const string SectionName = "EVIDGATE:Tasks";

    /// <summary>Milliseconds one consumer loop waits between two claim passes.</summary>
    [Range(10, 60_000, ErrorMessage = "EVIDGATE__TASKS__POLLINTERVALMILLISECONDS must be between 10 and 60000")]
    public int PollIntervalMilliseconds { get; init; } = 1000;

    /// <summary>Largest number of rows one claim pass takes for one queue (01.5.1 §7: batch claim).</summary>
    [Range(1, 500, ErrorMessage = "EVIDGATE__TASKS__BATCHSIZE must be between 1 and 500")]
    public int BatchSize { get; init; } = 50;

    /// <summary>
    /// Lease length of a claimed row, in seconds: `locked_until = now() + this`.
    /// A consumer that dies without completing leaves the row `active` until the
    /// lease runs out, after which the next claim pass makes it claimable again —
    /// the visibility timeout of 01.5.1 §6. It must comfortably exceed the
    /// longest expected handler, because a handler that outlives its lease can be
    /// run a second time concurrently (at-least-once, A10 — handlers are
    /// idempotent by their natural key).
    /// </summary>
    [Range(1, 86_400, ErrorMessage = "EVIDGATE__TASKS__VISIBILITYTIMEOUTSECONDS must be between 1 and 86400")]
    public int VisibilityTimeoutSeconds { get; init; } = 300;

    /// <summary>Base of the retry backoff: <c>base × 2^attempts</c>, capped and jittered (01.5.1-T1.3).</summary>
    [Range(1, 3600, ErrorMessage = "EVIDGATE__TASKS__BACKOFFBASESECONDS must be between 1 and 3600")]
    public int BackoffBaseSeconds { get; init; } = 5;

    /// <summary>Ceiling of the retry backoff, in seconds.</summary>
    [Range(1, 86_400, ErrorMessage = "EVIDGATE__TASKS__BACKOFFMAXSECONDS must be between 1 and 86400")]
    public int BackoffMaxSeconds { get; init; } = 3600;

    /// <summary>Milliseconds between two schedule-runner passes of the Worker (01.5.1-T1.4).</summary>
    [Range(1_000, 3_600_000, ErrorMessage = "EVIDGATE__TASKS__SCHEDULEPOLLMILLISECONDS must be between 1000 and 3600000")]
    public int SchedulePollMilliseconds { get; init; } = 60_000;

    /// <summary>
    /// How far back the schedule runner looks for due occurrences. It is
    /// deliberately wider than the poll interval: an occurrence is identified by
    /// `(schedule_name, fire_at)`, and re-attempting an already-fired occurrence is
    /// a no-op (`on conflict … do nothing`), so a generous window turns a missed
    /// tick or a restarted Worker into a late fire instead of a lost one.
    /// </summary>
    [Range(60, 86_400, ErrorMessage = "EVIDGATE__TASKS__SCHEDULELOOKBACKSECONDS must be between 60 and 86400")]
    public int ScheduleLookbackSeconds { get; init; } = 300;

    /// <summary>
    /// Concurrent claim loops for a queue the deployment did not configure
    /// explicitly — i.e. a queue only a registered schedule declares.
    /// </summary>
    [Range(1, 64, ErrorMessage = "EVIDGATE__TASKS__DEFAULTQUEUECONCURRENCY must be between 1 and 64")]
    public int DefaultQueueConcurrency { get; init; } = 1;

    /// <summary>Milliseconds the worker's outbox dispatcher waits between two dispatch passes (01.5.1-T3.1).</summary>
    [Range(10, 60_000, ErrorMessage = "EVIDGATE__TASKS__DISPATCHPOLLMILLISECONDS must be between 10 and 60000")]
    public int DispatchPollMilliseconds { get; init; } = 1000;

    /// <summary>Largest number of undispatched outbox rows one dispatch batch moves (01.5.1 §7: batch 200).</summary>
    [Range(1, 1_000, ErrorMessage = "EVIDGATE__TASKS__DISPATCHBATCHSIZE must be between 1 and 1000")]
    public int DispatchBatchSize { get; init; } = 200;

    /// <summary>
    /// Queue an outbox-dispatched task lands in when its <c>task_type</c> has no
    /// entry in <see cref="QueueRoutes"/>. A task_type carries no queue of its own
    /// — a consumer claims any registered type on every queue it polls — so the
    /// dispatcher's choice is purely a deployment routing decision, and an
    /// unrouted type falling to this queue stays <c>queued</c> until a consumer
    /// polls it (01.5.1 §11).
    /// </summary>
    public string DispatchDefaultQueue { get; init; } = "default";

    /// <summary>
    /// Task-type to queue routing the dispatcher applies on the insert
    /// (<c>EVIDGATE__TASKS__QUEUEROUTES__0__TASKTYPE</c>, <c>…__QUEUE</c>). A
    /// task_type absent from the list gets <see cref="DispatchDefaultQueue"/>.
    /// </summary>
    public List<QueueRouteOptions> QueueRoutes { get; init; } = [];


    /// <summary>
    /// Queues this worker consumes, each with its own concurrency limit
    /// (`EVIDGATE__TASKS__QUEUES__0__NAME`, `…__CONCURRENCY`). The queues the
    /// registered schedules declare are consumed as well, at
    /// <see cref="DefaultQueueConcurrency"/>, so a schedule never fires into a
    /// queue nobody polls.
    /// </summary>
    public List<QueueOptions> Queues { get; init; } = [];

    /// <summary>
    /// Connection string of the <c>evidgate_worker_sched</c> role (TL T80), used
    /// by the platform sweeps of 01.5.1 §7 to call owner <c>SECURITY DEFINER</c>
    /// functions such as <c>evidgate.purge_expired_idempotency</c>. It is a
    /// separate credential on purpose: the app role has no <c>EXECUTE</c> on a
    /// sweep function, so the boundary is enforced by the database and not by a
    /// convention. The value is optional at boot (a Worker without it still
    /// consumes every ordinary task) and the sweep handler fails loudly, naming
    /// this key, the first time it is needed. The setter is internal on purpose:
    /// the configuration binder only writes public setters, so the value can only
    /// come from <c>TasksOptionsSecretPostConfigure</c>, which resolves it through
    /// <c>ISecretProvider</c> (10.1.1-T2).
    /// </summary>
    public string SweepConnectionString { get; internal set; } = string.Empty;
}

/// <summary>One consumed queue and its concurrency limit (§7: per-queue poller).</summary>
public sealed class QueueOptions
{
    /// <summary>Queue name; the claim query filters `queue = @queue`.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Concurrent claim loops for this queue.</summary>
    [Range(1, 64, ErrorMessage = "EVIDGATE__TASKS__QUEUES__*__CONCURRENCY must be between 1 and 64")]
    public int Concurrency { get; init; } = 1;
}

/// <summary>One task-type → queue route the dispatcher applies on the insert (01.5.1-T3).</summary>
public sealed class QueueRouteOptions
{
    /// <summary>The <c>task_type</c> the route matches.</summary>
    public string TaskType { get; init; } = string.Empty;

    /// <summary>Queue a dispatched row of that type lands in.</summary>
    public string Queue { get; init; } = string.Empty;
}
