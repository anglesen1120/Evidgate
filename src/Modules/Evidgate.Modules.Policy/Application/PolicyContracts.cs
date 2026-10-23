using Evidgate.Modules.Policy.Application.Queries;
using Evidgate.Modules.Policy.Contracts;

namespace Evidgate.Modules.Policy.Application;

/// <summary>
/// The module's in-process surface (00 §5.5, 07.1.1-T4.2): the adapter that exposes
/// <see cref="IPolicyContracts"/> to other modules. Catalog's <c>CatalogContracts</c> is the precedent —
/// a thin, transaction-neutral wrapper that opens no transaction of its own and lets a null read stand as
/// "no active policy" (owner decision Q1) instead of surfacing an error.
/// </summary>
internal sealed class PolicyContracts(GetActivePolicyQuery query) : IPolicyContracts
{
    /// <summary>Resolves the revision a new run pins, or null when the project has no active policy.</summary>
    /// <param name="projectId">The project the run is created in.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<ActivePolicy?> GetActivePolicyAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var result = await query.GetAsync(projectId, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? result.Value : null;
    }
}
