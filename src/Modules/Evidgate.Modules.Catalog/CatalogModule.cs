using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Catalog.Application;
using Evidgate.Modules.Catalog.Application.Commands;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;
using Evidgate.Modules.Catalog.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.Modules.Catalog;

/// <summary>
/// Composition seam of the Catalog module (01.1.2-T2.1); internal by design and
/// reached through <c>InternalsVisibleTo</c> (00 §5.5).
/// </summary>
internal static class CatalogModule
{
    /// <summary>Registers the module's services in the calling host.</summary>
    public static IServiceCollection AddCatalogModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 03.1.1: the EF write model of the catalog (00 §5.5 C4).
        services.AddEvidgateDbContext<CatalogDbContext>(typeof(CatalogDbContext).Assembly);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidator<UpdateCaseCommand>, UpdateCaseCommandValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidator<CreateSuiteCommand>, CreateSuiteCommandValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidator<CreateFixtureCommand>, CreateFixtureCommandValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidator<CreateFixtureRevisionCommand>, CreateFixtureRevisionCommandValidator>());

        // The sidecar reader validates against contracts/catalog/sidecar.v1.json; the compiled set is built on first
        // use, so a host that never syncs a tree pays nothing at start-up.
        services.TryAddEvidgateContractValidation();

        // Handlers are resolved eagerly by Mediator at start-up, so nothing they depend on may compile the schemas:
        // they receive a Lazy that builds the set the first time a sidecar or a revision is validated.
        services.TryAddSingleton(provider => new Lazy<ContractSchemaSet>(() => provider.GetRequiredService<ContractSchemaSet>()));
        services.TryAddSingleton<SidecarManifestReader>();
        services.TryAddSingleton<CaseDiscovery>();
        services.TryAddScoped<ICaseIdentityService, CaseIdentityService>();
        services.TryAddScoped<ITestRevisionStore, TestRevisionStore>();
        services.TryAddSingleton<SuiteRevisionPlanner>();
        services.TryAddScoped<ICatalogContracts, CatalogContracts>();

        // The suite write path asks this for the fixture references a revision authors; it reads inside the caller's
        // tenant transaction, like every other catalog read.
        services.TryAddScoped<IFixtureRegistry, FixtureRegistry>();

        return services;
    }
}
