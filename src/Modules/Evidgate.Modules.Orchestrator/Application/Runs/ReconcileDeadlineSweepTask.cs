using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

/// <summary>Payload of the collection-deadline sweep's own fire.</summary>
/// <param name="Limit">
/// Runs one page reads; the handler clamps it to <see cref="ReconcileDeadlineSweeps.BatchSize"/>, so a payload
/// can only narrow the batch and never widen it past what one tick is sized for.
/// </param>
internal sealed record ReconcileDeadlineSweepPayload(int Limit);

/// <summary>
/// Names of the collection-deadline sweep: the only trigger a run gets when nothing else closes it.
/// <para>
/// A reconcile is otherwise enqueued in the transaction that closes a producer slot, and the attempt decides
/// the run's outcome against the collection deadline on the database clock. A run whose shard never closes —
/// or that never leaves <c>queued</c>/<c>running</c> because its producers never finalize — therefore never
/// gets an attempt, and stays open for ever with no row in the outbox, so nothing in the platform retries it.
/// This sweep is that missing retry: it finds the runs past their deadline that are still decidable
/// (<c>queued</c>, <c>running</c> or <c>collecting</c>), and queues one reconcile for each — the same task
/// type, the same handler, no new reconciliation logic. Which of them can be decided is the attempt's call,
/// never the sweep's.
/// </para>
/// <para>
/// A trigger whose task dead-letters would leave the run stuck exactly the same way, so the sweep also re-arms
/// it: the next attempt is queued once the previous one gave up, up to
/// <see cref="RunReconcileContract.DeadlineMaxAttempts"/> attempts. Past the bound the run is left alone and
/// the fire warns once — a permanent failure stops on its own instead of queueing for ever.
/// </para>
/// <para>
/// Known limit, and the reason the warning matters: a run past the bound stays overdue, so the scan still names
/// it and it keeps its slot in every later fire, while a fire reads at most
/// <see cref="BatchSize"/> x <see cref="MaxPagesPerFire"/> rows from the lowest keys and persists no cursor.
/// More than one budget of such runs at low keys therefore puts the rest of the backlog past a fire's reach —
/// the warning is what makes that visible, and paging past it needs a cursor the schema does not have yet.
/// </para>
/// </summary>
internal static class ReconcileDeadlineSweeps
{
    /// <summary>The task type the handler serves, which is also the schedule name.</summary>
    public const string ScheduleName = RunReconcileContract.DeadlineSweepTaskType;

    /// <summary>
    /// Every ten minutes. The sweep only has to notice a run after its deadline, and a run that is a full
    /// tick late costs nothing but latency on an outcome that is already determined; the fire also cannot
    /// pile up, because a fire of a still-open schedule is dropped, not queued.
    /// </summary>
    public const string Cron = "*/10 * * * *";

    /// <summary>
    /// Runs one page reads. Small on purpose: each run of a page costs an outbox write inside that run's
    /// tenant scope in the same fire, and a sweep must not hold a tenant's connection for a whole backlog.
    /// </summary>
    public const int BatchSize = 100;

    /// <summary>
    /// Pages one fire may drain. A fire that reaches this bound leaves the rest to the next one: the cursor
    /// restarts at the beginning and the keys already queued are skipped, so no run is queued twice — but a run
    /// past the bound waits a whole tick, and the handler warns when the bound cut a full page short.
    /// </summary>
    public const int MaxPagesPerFire = 10;

    /// <summary>The schedule definition: platform task on the maintenance queue, one row per fire.</summary>
    public static ScheduleDefinition Sweep { get; } = new(
        name: ScheduleName,
        cron: Cron,
        queue: EvidgateSweeps.MaintenanceQueue,
        taskType: ScheduleName,
        payloadFactory: _ => """{"limit":100}""");
}

/// <summary>
/// Platform task: reads every tenant's overdue runs through the sweep role's definer function, then queues the
/// attempt each run may still spend inside that run's own tenant scope, and only there — the outbox row takes its
/// tenant from the scope, never from the scan.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RequiresTenant"/> is <c>false</c> because the scan spans tenants: it cannot run inside any one
/// of them. Tenant scoping happens per run instead (<see cref="ITenantScopeFactory.BeginAsync"/>, one scope per
/// page of one org), so the only write of this handler — the outbox insert — runs under that run's tenant,
/// exactly like a finalize's trigger: <see cref="IOutboxWriter"/> takes <c>org_id</c> from the ambient scope and
/// never from a caller argument. The outbox and the task queue are mechanism tables with no tenant policy by
/// design (0006), so that write path, and the probe's explicit <c>org_id</c> filter, are what hold the boundary.
/// </para>
/// <para>
/// Overdue is decided by the database (<c>collection_deadline_at &lt;= now()</c> inside the function), not by
/// <see cref="TimeProvider"/>: the reconcile attempt it triggers decides on the same clock, and two different
/// clocks would let the sweep queue an attempt that then finds the deadline still ahead. The fake clock of a
/// test drives the schedule ticks, never this comparison.
/// </para>
/// </remarks>
internal sealed partial class ReconcileDeadlineSweepTaskHandler(
    IWorkerSchedDataSource sweep,
    ITenantScopeFactory scopes,
    ITenantDb db,
    IOutboxWriter outbox,
    ILogger<ReconcileDeadlineSweepTaskHandler> logger) : ITaskHandler<ReconcileDeadlineSweepPayload>
{
    /// <summary>The queue's giving-up state: the one state that says an attempt will never run again.</summary>
    private const string DeadLettered = "dead_letter";

    /// <inheritdoc />
    public string TaskType => ReconcileDeadlineSweeps.ScheduleName;

    /// <inheritdoc />
    public bool RequiresTenant => false;

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        ReconcileDeadlineSweepPayload payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var limit = Math.Clamp(payload.Limit, 1, ReconcileDeadlineSweeps.BatchSize);
        var enqueued = 0;
        var exhausted = 0;
        var drained = false;

        Guid? afterOrg = null;
        Guid? afterRun = null;

        for (var page = 0; page < ReconcileDeadlineSweeps.MaxPagesPerFire; page++)
        {
            var candidates = await ReadPageAsync(afterOrg, afterRun, limit, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                drained = true;
                break;
            }

            var outcome = await EnqueueAsync(candidates, cancellationToken).ConfigureAwait(false);
            enqueued += outcome.Enqueued;
            exhausted += outcome.Exhausted;

            afterOrg = candidates[^1].OrgId;
            afterRun = candidates[^1].RunId;

            // A page below the limit is the scan's own stop signal: the keyset cursor had nothing left after
            // it, so the next page would come back empty.
            if (candidates.Count < limit)
            {
                drained = true;
                break;
            }
        }

        // A full last page at the bound means the scan has no evidence the backlog ended: whatever is left stays
        // overdue and waits for the next fire, so the cut is logged rather than passed off as a finished sweep.
        if (!drained)
        {
            LogPageBoundReached(ReconcileDeadlineSweeps.MaxPagesPerFire, limit);
        }

        // Once per fire, never once per run: a run past its attempt bound is a permanent failure the sweep must
        // keep naming for an operator, without turning every tick into a page of identical warnings.
        if (exhausted > 0)
        {
            LogAttemptsExhausted(exhausted, RunReconcileContract.DeadlineMaxAttempts);
        }

        LogSwept(enqueued, limit);
    }

    /// <summary>One page of overdue runs, read as the sweep role.</summary>
    private async Task<List<OverdueRun>> ReadPageAsync(
        Guid? afterOrg,
        Guid? afterRun,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await sweep.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(ReconcileDeadlineSql.ReadPage, connection);
        command.Parameters.AddWithValue("after_org", (object?)afterOrg ?? DBNull.Value);
        command.Parameters.AddWithValue("after_run", (object?)afterRun ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", limit);

        var rows = new List<OverdueRun>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new OverdueRun(
                reader.GetGuid(0),
                reader.GetGuid(1),
                await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken).ConfigureAwait(false)));
        }

        return rows;
    }

    /// <summary>
    /// Queues the attempt each candidate may still spend, one tenant scope per org of the page: a run an earlier
    /// fire queued (or a previous page of this same fire queued) is not queued twice — and the outbox's own
    /// unique index stays the enforcement, the probe only saves the write.
    /// </summary>
    private async Task<DeadlineEnqueueOutcome> EnqueueAsync(
        List<OverdueRun> candidates,
        CancellationToken cancellationToken)
    {
        var enqueued = 0;
        var exhausted = 0;
        var index = 0;

        while (index < candidates.Count)
        {
            var orgId = candidates[index].OrgId;
            var page = new List<OverdueRun>();
            while (index < candidates.Count && candidates[index].OrgId == orgId)
            {
                page.Add(candidates[index]);
                index++;
            }

            var outcome = await EnqueuePageAsync(orgId, page, cancellationToken).ConfigureAwait(false);
            enqueued += outcome.Enqueued;
            exhausted += outcome.Exhausted;
        }

        return new DeadlineEnqueueOutcome(enqueued, exhausted);
    }

    /// <summary>Queues one org's share of a page inside that org's own scope, in one probe round trip.</summary>
    private async Task<DeadlineEnqueueOutcome> EnqueuePageAsync(
        Guid orgId,
        List<OverdueRun> page,
        CancellationToken cancellationToken)
    {
        await using var orgScope = await scopes
            .BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
            .ConfigureAwait(false);

        // Every attempt of every run on the page in one key set: the probe answers with the ones that exist and
        // what became of them, which is all a decision needs.
        var keys = page.SelectMany(row => AttemptKeys(row.RunId)).ToArray();
        var attempts = await db
            .QueryAsync<DeadlineAttemptRow>(
                ReconcileDeadlineSql.AttemptStates,
                // A list cannot travel as an array parameter on the tenant Dapper path (see AttemptStates), so it
                // is sent as one JSON document and unpacked by the statement.
                new { org_id = orgId, keys = JsonSerializer.Serialize(keys) },
                cancellationToken)
            .ConfigureAwait(false);
        var states = attempts.ToDictionary(
            row => row.IdempotencyKey,
            row => row.TaskState,
            StringComparer.Ordinal);

        var enqueued = 0;
        var exhausted = 0;
        foreach (var row in page)
        {
            var (attempt, spent) = PlanAttempt(row.RunId, states);
            if (spent)
            {
                exhausted++;
                continue;
            }

            if (attempt == 0)
            {
                continue;
            }

            await outbox.EnqueueAsync(
                    RunReconcileContract.OutboxTaskType,
                    RunReconcileContract.DeadlinePayloadJson(row.RunId),
                    RunReconcileContract.AggregateType,
                    row.RunId,
                    RunReconcileContract.DeadlineIdempotencyKey(row.RunId, attempt),
                    cancellationToken)
                .ConfigureAwait(false);
            enqueued++;
        }

        await orgScope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DeadlineEnqueueOutcome(enqueued, exhausted);
    }

    /// <summary>The keys of every attempt a run may spend, in attempt order.</summary>
    private static IEnumerable<string> AttemptKeys(Guid runId) =>
        Enumerable
            .Range(1, RunReconcileContract.DeadlineMaxAttempts)
            .Select(attempt => RunReconcileContract.DeadlineIdempotencyKey(runId, attempt));

    /// <summary>
    /// Which attempt of one overdue run the sweep may queue now, and whether the run has spent its bound.
    /// <para>
    /// The chain advances only through a dead letter: the first attempt is queued while it has no outbox row, and
    /// a later one only once the attempt before it reached <c>dead_letter</c>. A row that is still undispatched
    /// (no task yet), pending, in flight or done is not a failure, so the sweep leaves it alone — queueing again
    /// would spend a second attempt on work the first one is still doing, and a done attempt that left the run
    /// open is answered by the deadline itself, not by a loop.
    /// </para>
    /// </summary>
    /// <returns>The attempt to queue (0 for none) and whether every attempt dead-lettered.</returns>
    private static (int Attempt, bool Exhausted) PlanAttempt(
        Guid runId,
        Dictionary<string, string?> taskStates)
    {
        for (var attempt = 1; attempt <= RunReconcileContract.DeadlineMaxAttempts; attempt++)
        {
            if (!taskStates.TryGetValue(RunReconcileContract.DeadlineIdempotencyKey(runId, attempt), out var state))
            {
                return (attempt, false);
            }

            if (!string.Equals(state, DeadLettered, StringComparison.Ordinal))
            {
                return (0, false);
            }
        }

        return (0, true);
    }

    [LoggerMessage(
        EventId = 2425,
        Level = LogLevel.Information,
        Message = "Deadline sweep queued {Enqueued} reconcile(s) in pages of at most {Limit} run(s)")]
    private partial void LogSwept(int enqueued, int limit);

    [LoggerMessage(
        EventId = 2426,
        Level = LogLevel.Warning,
        Message = "Deadline sweep stopped at its {Pages}-page bound of {Limit} run(s) per page with a full page: any overdue run past it waits for the next fire")]
    private partial void LogPageBoundReached(int pages, int limit);

    [LoggerMessage(
        EventId = 2427,
        Level = LogLevel.Warning,
        Message = "Deadline sweep left {Runs} overdue run(s) alone this fire: each spent all {Attempts} collection-deadline attempt(s) and the last one dead-lettered")]
    private partial void LogAttemptsExhausted(int runs, int attempts);

    /// <summary>One overdue run as the scan projects it: the tenant, the run, and the deadline that passed.</summary>
    private sealed record OverdueRun(Guid OrgId, Guid RunId, DateTimeOffset CollectionDeadlineAt);

    /// <summary>What one page of the sweep wrote, and how many of its runs had spent their attempts.</summary>
    private readonly record struct DeadlineEnqueueOutcome(int Enqueued, int Exhausted);
}
