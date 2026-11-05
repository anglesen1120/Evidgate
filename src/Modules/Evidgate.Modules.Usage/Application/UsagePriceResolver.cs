using Evidgate.Modules.Usage.Contracts;
using Evidgate.Modules.Usage.Infrastructure.Persistence;

namespace Evidgate.Modules.Usage.Application;

/// <summary>
/// The in-process pricing surface: the adapter that exposes <see cref="IUsagePriceResolver"/> to the callers
/// that price a metered row. It opens no transaction of its own and runs on the caller's tenant scope, so the price it
/// answers sees the caller's uncommitted rows and the caller's org — never another tenant's override.
/// </summary>
internal sealed class UsagePriceResolver(UsagePriceStore store) : IUsagePriceResolver
{
    /// <inheritdoc />
    public Task<ResolvedUsagePrice?> ResolveAsync(
        UsageMetric metric,
        DateTimeOffset at,
        CancellationToken cancellationToken) =>
        store.ResolveAsync(metric, at, cancellationToken);
}
