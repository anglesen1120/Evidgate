using Evidgate.BuildingBlocks.Contracts.Validation;
using Evidgate.BuildingBlocks.Persistence;
using Evidgate.BuildingBlocks.Pipeline;
using Evidgate.Modules.Identity;
using Evidgate.Modules.Policy;
using Mediator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// The Mediator generator reads its options at compile time from the AddMediator call below: scoped handlers like the
// hosts, and a namespace of its own so this assembly's generated copy stays distinct from another assembly's.

namespace Evidgate.Migrator;

/// <summary>
/// The in-process container <c>seed-dev --with-token</c> drives the modules' commands through: the same registrations
/// the Api and the Worker use, over the app-role data source, so the seed's token and policy come from the real
/// handlers under the real pipeline rather than from a second copy of their logic.
/// </summary>
/// <remarks>
/// The composition is the shared extensions the entry points call, in their order, so a change to any of them reaches
/// the seed. The contract schema set is registered from the repository's <c>contracts/</c> directory because a console
/// tool has no <c>IHostEnvironment</c> to seed the host-search overload.
/// </remarks>
internal static class DevSeedServices
{
    /// <summary>Builds the container; nothing connects and no schema compiles until a service is resolved.</summary>
    /// <param name="environment">Environment snapshot; null reads the process environment.</param>
    /// <param name="contractsDirectory">The repository's <c>contracts/</c> directory.</param>
    /// <param name="loggerProvider">An extra log sink, for tests that assert what reaches a logger.</param>
    internal static ServiceProvider Compose(
        IReadOnlyDictionary<string, string?>? environment,
        string contractsDirectory,
        ILoggerProvider? loggerProvider = null)
    {
        var configuration = Configuration(environment);

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            if (loggerProvider is not null)
            {
                // Trace, so a test sink sees every level the seed could leak a secret at, not just Information and up.
                builder.SetMinimumLevel(LogLevel.Trace).AddProvider(loggerProvider);
            }
        });

        services.AddEvidgatePersistence(configuration);
        // Lazy on purpose: the set compiles every contract with Roslyn, and a refusal must not pay for that compile.
        services.AddSingleton(_ => ContractSchemaSet.Load(contractsDirectory));
        services.AddMediator((MediatorOptions options) =>
        {
            options.Namespace = "Evidgate.Migrator.Generated";
            options.ServiceLifetime = ServiceLifetime.Scoped;
        });
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
