using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.Modules.Catalog;

/// <summary>
/// Composition seam of the Catalog module (01.1.2-T2.1); internal by design and
/// reached through <c>InternalsVisibleTo</c> (00 §5.5).
/// </summary>
internal static class CatalogModule
{
    /// <summary>Registers the module's slice services in the calling host.</summary>
    public static IServiceCollection AddCatalogModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ICatalog, InMemorySuiteCatalog>();

        return services;
    }
}
