using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.Modules.Ingestion.Application;
using Evidgate.Modules.Ingestion.Application.Commands;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Ingestion.Domain;
using Evidgate.Modules.Ingestion.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        // 04.7.1-T2.1: the pure negotiation rule, one instance for the registration path (04.7.1-T2.2/T2.3) and
        // the 05.7.x claim path.
        services.AddSingleton<IProtocolNegotiator, ProtocolNegotiator>();

        // 04.7.1-T2.2/T2.3: the capability store and the registration path shared by both entries.
        services.TryAddScoped<AdapterCapabilityStore>();
        services.TryAddScoped<AdapterCapabilityRegistrar>();

        // The capability body validates against contracts/adapter/capability.v1.json; the compiled set is built on
        // first use, so a host that never registers a capability pays nothing at start-up.
        services.TryAddEvidgateContractValidation();
        services.TryAddSingleton(provider => new Lazy<ContractSchemaSet>(() => provider.GetRequiredService<ContractSchemaSet>()));

        return services;
    }
}
