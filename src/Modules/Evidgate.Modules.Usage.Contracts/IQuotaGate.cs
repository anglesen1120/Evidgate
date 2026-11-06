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
}
