using Evidgate.Migrator.Generated;
using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Persistence;
using Evidgate.BuildingBlocks.Pipeline;
using Evidgate.Modules.Identity;
using Evidgate.Modules.Policy;
using Mediator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// The generated mediator lives in its own namespace: test projects that see this assembly's internals generate their
// own mediator, and two generated copies in one namespace collide.
[assembly: MediatorOptions(Namespace = "Evidgate.Migrator.Generated")]

namespace Evidgate.Migrator;

/// <summary>
/// The in-process container <c>seed-dev --with-token</c> drives the modules' commands through: the same registrations
/// the Api and the Worker use, over the app-role data source, so the seed's token and policy are produced by the real
/// handlers under the real pipeline (tenant scope, authorization, audit, outbox) rather than by a second copy of their
/// logic.
/// </summary>
/// <remarks>
/// One small method on purpose: the composition is the shared extensions the entry points call
/// (<c>AddEvidgatePersistence</c>, <c>AddEvidgatePipeline</c>, the module registrations, <c>AddMediator</c>), in their
/// order, so a change to any of them reaches the seed through the same seam. The contract schema set is registered
/// from the repository's <c>contracts/</c> directory instead of the host-search overload, because a console tool has no
/// <c>IHostEnvironment</c> to seed that search.
/// </remarks>
internal static class DevSeedServices
{
    /// <summary>Builds the container; nothing connects until a service is resolved.</summary>
    /// <param name="environment">Environment snapshot; null reads the process environment.</param>
    /// <param name="contractsDirectory">The repository's <c>contracts/</c> directory.</param>
    internal static ServiceProvider Compose(
        IReadOnlyDictionary<string, string?>? environment,
        string contractsDirectory)
    {
        var configuration = Configuration(environment);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEvidgatePersistence(configuration);
        services.AddEvidgateContractValidation(contractsDirectory);
        services.AddMediator();
        services.AddEvidgatePipeline();
        services.AddPolicyModule();
        services.AddIdentityModule(configuration);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The environment snapshot as configuration: the environment-variables source turns <c>__</c> into <c>:</c>, so the
    /// explicit secret names (<c>EVIDGATE__CI__PEPPER</c>) are read by the same <c>EVIDGATE:CI:PEPPER</c> keys the
    /// hosts read.
    /// </summary>
    private static IConfiguration Configuration(IReadOnlyDictionary<string, string?>? environment)
    {
        var values = environment ?? MigratorOptions.ProcessEnvironment();
        var configuration = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            configuration[key.Replace("__", ":", StringComparison.Ordinal)] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(configuration).Build();
    }
}
