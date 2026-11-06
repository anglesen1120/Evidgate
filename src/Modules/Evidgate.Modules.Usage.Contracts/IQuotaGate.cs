namespace Evidgate.Modules.Usage.Contracts;

/// <summary>
/// Reserves metered work atomically inside the caller's tenant transaction. A block refuses new
/// work; it never rolls back work already committed. Hosts register the real implementation.
/// <c>EVIDGATE__USAGE__QUOTAGATE__ENABLED=false</c> is an explicit operator switch that answers Ok
/// without writes, not a substitute for missing registration. Otherwise no counter row means no
/// configured limit.
/// <para>
/// One call may charge two counters — the organization-wide one and the project's
/// (<see cref="QuotaCheckRequest.IncludeTenantScope"/>) — and then the last statement decides for both: the work is
/// admitted only when both have room, and both are charged or neither is, so a refusal can never leave usage behind
/// in the scope that had room. The counters are locked in one order for every caller, tenant first, so two calls
/// cannot deadlock on each other's scope.
/// </para>
/// </summary>
public interface IQuotaGate
{
    /// <summary>Checks and reserves one batch; the caller owns commit or rollback.</summary>
    /// <param name="request">The metric, billing month, project, strictly positive quantity and which scopes to charge.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The decision and, on block, the refusing scope, retry and operator raise details.</returns>
    Task<QuotaCheckResult> CheckAsync(QuotaCheckRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The billing month, <c>YYYY-MM</c> in UTC, of the caller's open database transaction: the instant the batch's own
    /// rows and the ledger row metering them are stamped with, so a batch is gated and metered in one month even when
    /// it straddles a month end. Callers build <see cref="QuotaCheckRequest.Period"/> from this, never from an
    /// application clock, and ask inside the transaction that later calls <see cref="CheckAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The period of the transaction's start time.</returns>
    Task<string> CurrentPeriodAsync(CancellationToken cancellationToken);

    /// <summary>
    /// An advisory, read-only answer to the question <see cref="CheckAsync"/> would decide: it writes nothing, locks
    /// nothing and never carries a limit forward. <see cref="QuotaDecision.Block"/> means a counter of the period
    /// exists and has no room for the quantity right now; every other answer, including a period with no counter yet,
    /// means only "do not skip the authoritative check". It never admits work.
    /// </summary>
    /// <param name="request">The same question the authoritative call would ask.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Block with the wait to the next period, or Ok.</returns>
    Task<QuotaCheckResult> PeekAsync(QuotaCheckRequest request, CancellationToken cancellationToken);
}
