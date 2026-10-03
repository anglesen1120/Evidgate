using Evidgate.BuildingBlocks.Contracts.Validation;
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

        // 05.1.1/05.1.2: the manifest and environment-revision routes validate their bodies against the contract schema
        // set, which is compiled on first use (the routes resolve it per request, never at start-up).
        services.TryAddEvidgateContractValidation();

        return services;
    }
}
