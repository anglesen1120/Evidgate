using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Policy.Application;
using Evidgate.Modules.Policy.Application.Queries;
using Evidgate.Modules.Policy.Contracts;
using Evidgate.Modules.Policy.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.Modules.Policy;

/// <summary>
/// Composition seam of the Policy module (01.1.2-T2.1); internal by design and
/// reached through <c>InternalsVisibleTo</c> (00 §5.5).
/// </summary>
internal static class PolicyModule
{
    /// <summary>Registers the module's services in the calling host.</summary>
    public static IServiceCollection AddPolicyModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 07.1.1-T1: the EF write model of policy revisions (00 §5.5 C4).
        services.AddEvidgateDbContext<PolicyDbContext>(typeof(PolicyDbContext).Assembly);

        // 07.1.1-T1: the append-only revision store 07.1.1-T3's command writes through.
        services.TryAddScoped<PolicyRevisionStore>();

        // 07.1.1-T2: the sole producer of policy_revision.hash (finding T1-SEC-S2).
        services.TryAddSingleton<IPolicyHasher, PolicyHasher>();

        // 07.1.1-T4: the activation store (the pointer write) and the in-process surface other modules call
        // (IPolicyContracts, 00 §5.5): pin a run and read a revision's required artifact kinds. All of these are
        // scoped: they run in the caller's tenant transaction.
        services.TryAddScoped<PolicyActivationStore>();
        services.TryAddScoped<GetActivePolicyQuery>();
        services.TryAddScoped<GetRequiredArtifactKindsQuery>();
        services.TryAddScoped<IPolicyContracts, PolicyContracts>();

        // 07.1.1-T3: the policy routes validate their bodies against the contract schema set, which is compiled on
        // first use (the routes resolve it per request, never at start-up).
        services.TryAddEvidgateContractValidation();

        // Handlers are resolved eagerly by Mediator at start-up: they receive a Lazy, so the schema set compiles on first use.
        services.TryAddSingleton(provider => new Lazy<ContractSchemaSet>(() => provider.GetRequiredService<ContractSchemaSet>()));

        // 07.3.1-T1: the waiver create validates `expires_at` against the server clock (00 §5.5 C2).
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }
}
