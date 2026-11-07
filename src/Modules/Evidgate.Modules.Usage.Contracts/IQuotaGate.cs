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
/// <para>
/// The wait a refusal names until the next period is measured from the database's transaction clock — the clock that
/// named the period — so it never contradicts a month the database still has open because a host's clock runs ahead.
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
    /// An advisory, read-only answer to the question <see cref="CheckAsync"/> would decide: it writes nothing, locks
    /// nothing and never carries a limit forward. <see cref="QuotaPeekResult.Blocked"/> means a counter of the period
    /// exists and has no room for the quantity right now; every other answer, including a period with no counter yet,
    /// means only "do not skip the authoritative check". It never admits work, and its result type is not
    /// <see cref="QuotaCheckResult"/>, so the two cannot be swapped.
    /// </summary>
    /// <param name="request">The same question the authoritative call would ask.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Blocked with the wait to the next period, or <see cref="QuotaPeekResult.Clear"/>.</returns>
    Task<QuotaPeekResult> PeekAsync(QuotaCheckRequest request, CancellationToken cancellationToken);
}
