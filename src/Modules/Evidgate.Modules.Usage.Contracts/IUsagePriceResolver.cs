namespace Evidgate.Modules.Usage.Contracts;

/// <summary>
/// The rate-card lookup the pricing path calls in process. It runs inside the caller's tenant transaction —
/// the caller opened it through the pipeline, so this interface never opens a connection or a transaction, never
/// commits and never rolls back — and the organization is always the scope's tenant, never a value the caller passes:
/// a tenant can never resolve another tenant's override.
/// </summary>
public interface IUsagePriceResolver
{
    /// <summary>
    /// The price of <paramref name="metric"/> that was in force at <paramref name="at"/> for the caller's org: its own
    /// override when one covers the instant, otherwise the global rate card. The range is half-open
    /// (<c>effective_from &lt;= at &lt; effective_to</c>), so the instant a range ends belongs to the next one.
    /// </summary>
    /// <param name="metric">The metric being priced.</param>
    /// <param name="at">The instant the metered work happened, compared as an instant whatever offset it carries.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The covering price, or null when no override and no global row covers <paramref name="at"/>.</returns>
    /// <exception cref="InvalidOperationException">No tenant transaction is open: the scope guard throws before any statement runs.</exception>
    Task<ResolvedUsagePrice?> ResolveAsync(UsageMetric metric, DateTimeOffset at, CancellationToken cancellationToken);
}
