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
/// Names of the collection-deadline sweep: the only trigger a run gets when its last shard never finalizes.
/// <para>
/// A reconcile is otherwise enqueued in the transaction that closes a producer slot, and the attempt decides
/// the run's outcome against the collection deadline on the database clock. A run whose shard never closes
/// therefore never gets an attempt, and stays <c>collecting</c> forever with no row in the outbox, so nothing
/// in the platform retries it. This sweep is that missing retry: it finds the runs that are past their
/// deadline and still collectable, and queues one reconcile for each — the same task type, the same handler,
/// no new reconciliation logic.
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
    /// Pages one fire may drain. A fire that reaches this bound leaves the rest to the next one — the cursor
    /// restarts at the beginning, and the pages it already visited are deduplicated by their keys, so stopping
    /// early loses no work and repeats none.
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
/// Platform task: reads every tenant's overdue runs through the sweep role's definer function, then queues one
/// reconcile per run inside that run's own tenant scope, and only there — the outbox row takes its tenant from
/// the scope, never from the scan.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RequiresTenant"/> is <c>false</c> because the scan spans tenants: it cannot run inside any one
/// of them. Tenant scoping happens per run instead (<see cref="ITenantScopeFactory.BeginAsync"/>, one scope per
/// page of one org), so the only write of this handler — the outbox insert — runs under that run's tenant,
/// exactly like a finalize's trigger: <see cref="IOutboxWriter"/> takes <c>org_id</c> from the ambient scope and
/// never from a caller argument. The outbox is a mechanism table with no tenant policy by design (0006), so that
/// write path, and the probe's explicit <c>org_id</c> filter, are what hold the boundary.
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

        Guid? afterOrg = null;
        Guid? afterRun = null;

        for (var page = 0; page < ReconcileDeadlineSweeps.MaxPagesPerFire; page++)
        {
            var candidates = await ReadPageAsync(afterOrg, afterRun, limit, cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                break;
            }

            enqueued += await EnqueueAsync(candidates, cancellationToken).ConfigureAwait(false);

            afterOrg = candidates[^1].OrgId;
            afterRun = candidates[^1].RunId;

            // A page below the limit is the scan's own stop signal: the keyset cursor had nothing left after
            // it, so the next page would come back empty.
            if (candidates.Count < limit)
            {
                break;
            }
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
    /// Queues one reconcile per candidate, one tenant scope per org of the page: the keys of that org are
    /// read in a single statement, so a run an earlier fire already queued (or a run a previous page of this
    /// same fire queued) is not queued twice — and the outbox's own unique index stays the enforcement, this
    /// probe only saves the write.
    /// </summary>
    private async Task<int> EnqueueAsync(List<OverdueRun> candidates, CancellationToken cancellationToken)
    {
        var enqueued = 0;
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

            await using var orgScope = await scopes
                .BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
                .ConfigureAwait(false);

            var keys = page
                .Select(row => RunReconcileContract.DeadlineIdempotencyKey(row.RunId))
                .ToArray();
            var present = await db
                .QueryAsync<string>(
                    ReconcileDeadlineSql.ExistingKeys,
                    // A list cannot travel as an array parameter on the tenant Dapper path (see ExistingKeys), so
                    // it is sent as one JSON document and unpacked by the statement.
                    new { org_id = orgId, keys = JsonSerializer.Serialize(keys) },
                    cancellationToken)
                .ConfigureAwait(false);
            var queued = new HashSet<string>(present, StringComparer.Ordinal);

            foreach (var row in page)
            {
                var key = RunReconcileContract.DeadlineIdempotencyKey(row.RunId);
                if (!queued.Add(key))
                {
                    continue;
                }

                await outbox.EnqueueAsync(
                        RunReconcileContract.OutboxTaskType,
                        RunReconcileContract.DeadlinePayloadJson(row.RunId),
                        RunReconcileContract.AggregateType,
                        row.RunId,
                        key,
                        cancellationToken)
                    .ConfigureAwait(false);
                enqueued++;
            }

            await orgScope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return enqueued;
    }

    [LoggerMessage(
        EventId = 2425,
        Level = LogLevel.Information,
        Message = "Deadline sweep queued {Enqueued} reconcile(s) in pages of at most {Limit} run(s)")]
    private partial void LogSwept(int enqueued, int limit);

    /// <summary>One overdue run as the scan projects it: the tenant, the run, and the deadline that passed.</summary>
    private sealed record OverdueRun(Guid OrgId, Guid RunId, DateTimeOffset CollectionDeadlineAt);
}
