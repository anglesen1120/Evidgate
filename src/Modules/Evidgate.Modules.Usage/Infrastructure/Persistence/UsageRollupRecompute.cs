using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Usage.Application.Rollup;

namespace Evidgate.Modules.Usage.Infrastructure.Persistence;

/// <summary>
/// The production recompute: the stateless wrapper over <see cref="UsageRollupStore"/>, registered once because the
/// platform handler that calls it is a singleton.
/// </summary>
internal sealed class UsageRollupRecompute : IUsageRollupRecompute
{
    /// <inheritdoc />
    public Task RecomputeAsync(ITenantScope scope, CancellationToken cancellationToken) =>
        UsageRollupStore.RecomputeAsync(scope, cancellationToken);
}
