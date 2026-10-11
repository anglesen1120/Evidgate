using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.Modules.Orchestrator.Application.Attempts;
using Evidgate.Modules.Orchestrator.Application.Runs;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Evidgate.Modules.Orchestrator.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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

        // Handlers are resolved eagerly by Mediator at start-up: they receive a Lazy, so the schema set compiles on first use.
        services.TryAddSingleton(provider => new Lazy<ContractSchemaSet>(() => provider.GetRequiredService<ContractSchemaSet>()));

        // 05.2.1: the run-create defaults (T93) are bound and validated at boot like every other EVIDGATE__* group.
        // BindConfiguration resolves IConfiguration from the container, so the module seam keeps its signature.
        services
            .AddOptions<RunsOptions>()
            .BindConfiguration(RunsOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // 05.3.3-T2: the case-attempt writer and the blocked-dependant resolver 04's event-batch commit calls
        // inside its own tenant transaction. Scoped like every other module service; the writer and the resolver
        // take the ambient scope through ITenantDb/ITenantScopeAccessor, never a connection of their own.
        services.TryAddScoped<ICaseAttemptWriter, CaseAttemptWriter>();
        services.TryAddScoped<IBlockedAttemptResolver, BlockedAttemptResolver>();

        return services;
    }
}
