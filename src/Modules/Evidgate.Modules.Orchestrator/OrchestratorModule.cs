using Evidgate.Modules.Orchestrator.Domain;
using Evidgate.Modules.Orchestrator.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.Modules.Orchestrator;

/// <summary>
/// Composition seam of the Orchestrator module (01.1.2-T2.1); internal by design
/// and reached through <c>InternalsVisibleTo</c> (00 §5.5).
/// </summary>
internal static class OrchestratorModule
{
    /// <summary>Registers the module's slice services in the calling host.</summary>
    public static IServiceCollection AddOrchestratorModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IRunRegistry, InMemoryRunRegistry>();

        return services;
    }
}
