using Dapper;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence.Tasks;

/// <summary>
/// `idempotency.purge`: calls the owner <c>SECURITY DEFINER</c> function
/// <c>evidgate.purge_expired_idempotency</c> as <c>evidgate_worker_sched</c>, in
/// bounded batches, until the backlog is drained or the fire's time budget is spent.
/// </summary>
/// <remarks>
/// The app role has no <c>EXECUTE</c> on that function, so this handler is the only
/// path to it: the sweep runs on the dedicated connection of
/// <see cref="IWorkerSchedDataSource"/>. A fire is bounded by elapsed time rather
/// than by a batch count, so the bound follows how fast the database actually
/// drains instead of stopping a large backlog at a fixed ceiling. Every call is
/// audited: the row goes through <c>evidgate.record_sweep_audit</c>, the sweep
/// role's only write path, because <c>public.audit_log</c> is per-org and this
/// purge is cross-tenant platform work.
/// </remarks>
public sealed partial class IdempotencyPurgeTaskHandler : ITaskHandler<IdempotencyPurgePayload>
{
    /// <inheritdoc />
    public bool RequiresTenant => false;

    /// <summary>
    /// How long the partial-removal audit of a failed fire may take. It runs while the task's
    /// own failure is pending, so it is bounded rather than tied to the caller's cancelled token.
    /// </summary>
    private static readonly TimeSpan AuditRecoveryTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The one purge call this handler makes, as the frozen 01.5.1-T6.1 signature.</summary>
    internal const string PurgeFunctionCall = "select evidgate.purge_expired_idempotency(@limit)";

    /// <summary>The audit write, as the owner definer of 0088 freezes it.</summary>
    internal const string AuditFunctionCall =
        "select evidgate.record_sweep_audit(@action, @removed, @limit, @budget_exhausted)";

    private readonly IWorkerSchedDataSource _sweep;
    private readonly TimeProvider _time;
    private readonly ILogger<IdempotencyPurgeTaskHandler> _logger;

    public IdempotencyPurgeTaskHandler(
        IWorkerSchedDataSource sweep,
        TimeProvider time,
        ILogger<IdempotencyPurgeTaskHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _sweep = sweep;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public string TaskType => EvidgateSweeps.IdempotencyPurgeTaskType;

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        IdempotencyPurgePayload payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // The owner function caps its own batch (`least(greatest(p_limit, 0), 10000)`,
        // 0007), so the loop works in the function's units: `deleted < limit` is only
        // a drained backlog when `limit` is what the function actually deleted up to.
        // A payload asking for more than the cap would stop after the first capped
        // batch, and a non-positive one would ask for zero rows and spin the whole
        // time budget deleting nothing (a task row is replayable by an operator, so
        // the payload is not trusted to stay in range).
        var limit = Math.Clamp(payload.Limit, 1, EvidgateSweeps.PurgeMaxBatchSize);
        var progress = new DrainProgress();
        bool budgetExhausted;

        try
        {
            // A platform task: `scope` is null because the row carries no org_id. The
            // purge is cross-tenant by design — the owner function's `for update skip
            // locked` batch is what keeps a concurrent purge safe.
            await using (var connection = await _sweep.OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                budgetExhausted = await DrainAsync(connection, limit, progress, cancellationToken).ConfigureAwait(false);

                await AuditAsync(connection, progress.Removed, limit, budgetExhausted, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Rows this fire already removed cannot be reconstructed by the next fire,
            // so a failing or cancelled loop still attests what it removed. A row is
            // only owed when something was removed: the task row carries the failure.
            // This runs after the `using` disposed the failed connection, so the
            // recovery open cannot wait on a pool the failed connection still holds,
            // and it is time-bounded so a sick database does not delay the failure.
            if (progress.Removed > 0)
            {
                await TryAuditAsync(progress.Removed, limit).ConfigureAwait(false);
            }

            throw;
        }

        LogPurged(progress.Removed, limit, budgetExhausted);
    }

    /// <summary>
    /// Drains the expired backlog in batches of <paramref name="limit"/> until the function
    /// comes back short (the backlog is gone) or the fire has spent
    /// <see cref="EvidgateSweeps.PurgeTimeBudget"/>. The budget is measured on the injected
    /// clock, never on the loop's count, so a slow database stops the fire instead of holding
    /// the worker; at least one batch always runs.
    /// </summary>
    private async Task<bool> DrainAsync(
        NpgsqlConnection connection,
        int limit,
        DrainProgress progress,
        CancellationToken cancellationToken)
    {
        var start = _time.GetTimestamp();

        while (true)
        {
            var deleted = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                PurgeFunctionCall,
                new { limit },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            progress.Removed += deleted;

            // A short batch means the backlog is drained; looping again would be a
            // second round trip that can only return zero.
            if (deleted < limit)
            {
                return false;
            }

            // Only a full batch can leave a remainder, so only it can stop on the
            // budget. The check runs after the batch, so at least one batch always
            // runs in a fire.
            if (_time.GetElapsedTime(start) >= EvidgateSweeps.PurgeTimeBudget)
            {
                return true;
            }
        }
    }

    private static async Task AuditAsync(
        NpgsqlConnection connection,
        int removed,
        int limit,
        bool budgetExhausted,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            AuditFunctionCall,
            new
            {
                action = EvidgateSweeps.IdempotencyPurgeTaskType,
                removed,
                limit,
                budget_exhausted = budgetExhausted,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a failed or cancelled fire's partial removal on its own connection, within
    /// <see cref="AuditRecoveryTimeout"/>. It runs while the original exception is still
    /// pending, so it can never wait indefinitely on a sick database or a cancelled host.
    /// </summary>
    private async Task TryAuditAsync(int removed, int limit)
    {
        using var timeout = new CancellationTokenSource(AuditRecoveryTimeout);

        try
        {
            await using var connection = await _sweep.OpenAsync(timeout.Token).ConfigureAwait(false);
            await AuditAsync(connection, removed, limit, budgetExhausted: false, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogAuditFailed(exception, removed);
        }
    }

    [LoggerMessage(
        EventId = 2322,
        Level = LogLevel.Information,
        Message = "Audited sweep evidgate.purge_expired_idempotency as evidgate_worker_sched removed {Removed} expired records (batch limit {Limit}, budget exhausted {BudgetExhausted})")]
    private partial void LogPurged(int removed, int limit, bool budgetExhausted);

    /// <summary>Rows this fire removed so far, shared with the failure path the drain escapes to.</summary>
    private sealed class DrainProgress
    {
        public int Removed { get; set; }
    }

    [LoggerMessage(
        EventId = 2324,
        Level = LogLevel.Warning,
        Message = "Could not record the idempotency purge audit row after removing {Removed} records; the task failure stays the original error")]
    private partial void LogAuditFailed(Exception exception, int removed);
}
