using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Ingestion.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.Modules.Ingestion;

/// <summary>
/// Composition seam of the Ingestion module (01.1.2-T2.1): both entry points call
/// it with the same module set. Internal on purpose — 00 §5.5 keeps only
/// <c>.Contracts</c>/<c>.Endpoints</c> public, so the Api and Worker reach it
/// through <c>InternalsVisibleTo</c> (declared in the .csproj).
/// </summary>
internal static class IngestionModule
{
    /// <summary>Registers the module's slice services in the calling host.</summary>
    public static IServiceCollection AddIngestionModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IIngestion, InMemoryManifestStore>();
        services.AddSingleton<InMemoryEventStore>();

        return services;
    }
}
