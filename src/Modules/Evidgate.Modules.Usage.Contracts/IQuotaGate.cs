namespace Evidgate.Modules.Usage.Contracts;

/// <summary>
/// Reserves metered work atomically inside the caller's tenant transaction. A block refuses new
/// work; it never rolls back work already committed. Hosts register the real implementation.
/// <c>EVIDGATE__USAGE__QUOTAGATE__ENABLED=false</c> is an explicit operator switch that answers Ok
/// without writes, not a substitute for missing registration. Otherwise no counter row means no
/// configured limit.
/// </summary>
public interface IQuotaGate
{
    /// <summary>Checks and reserves one batch; the caller owns commit or rollback.</summary>
    /// <param name="request">The metric, billing month, project and strictly positive quantity.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The decision and, on block, retry and operator raise details.</returns>
    Task<QuotaCheckResult> CheckAsync(QuotaCheckRequest request, CancellationToken cancellationToken);
}
