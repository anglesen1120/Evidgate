using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.Modules.Usage.Application.Rollup;

/// <summary>
/// The recompute one org's roll-up needs, behind a seam so the platform handler can be exercised for a single failing
/// tenant without a database that fails on purpose.
/// </summary>
internal interface IUsageRollupRecompute
{
    /// <summary>Recomputes every aggregate scope of <paramref name="scope"/>'s tenant in its open transaction.</summary>
    /// <param name="scope">The open tenant scope of the org to recompute.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task RecomputeAsync(ITenantScope scope, CancellationToken cancellationToken);
}
