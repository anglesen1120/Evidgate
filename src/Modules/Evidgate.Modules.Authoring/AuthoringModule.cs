using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.Modules.Authoring;

/// <summary>
/// Composition seam of the Authoring module (13.1.1-T1.3/T1.4, 00 §5.5): internal by design and reached through
/// <c>InternalsVisibleTo</c>, exactly like <c>CatalogModule</c>. The host registration
/// (<c>AddAuthoringModule</c>/<c>MapAuthoringEndpoints</c> in <c>ApiHost</c>) is 13.1.1-T2.1's, so a test constructs the
/// module through this method today.
/// </summary>
internal static class AuthoringModule
{
    /// <summary>Registers the module's write model, its one writer and the code allocator.</summary>
    /// <param name="services">The calling host's collection.</param>
    public static IServiceCollection AddAuthoringModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 13.1.1-T1.3: the EF Core 10 write model of the five tables of migration 0039 (00 §5.5 C4).
        services.AddEvidgateDbContext<AuthoringDbContext>(typeof(AuthoringDbContext).Assembly);

        // 13.1.1-T1.4: the only code path that mutates an entity or appends an authoring_version row, exposed to the
        // other modules through the contracts interface (13.1.2's apply, 13.3.1's generation).
        services.TryAddScoped<AuthoringVersionWriter>();
        services.TryAddScoped<IAuthoringVersionWriter, AuthoringVersionWriterContract>();

        // 13.1.1-T1.3: the per-project manual case code allocator (§4, AC4).
        services.TryAddScoped<AuthoringCodeAllocator>();

        // 13.1.1-T1.2: contracts/schemas/authoring-steps.v1.json is validated through Corvus; the compiled schema set is
        // built on first use, so a host that never writes a case pays nothing at start-up.
        services.TryAddEvidgateContractValidation();
        services.TryAddSingleton(provider =>
            new Lazy<ContractSchemaSet>(() => provider.GetRequiredService<ContractSchemaSet>()));
        services.TryAddScoped<ManualCaseStepsValidator>();

        return services;
    }
}
