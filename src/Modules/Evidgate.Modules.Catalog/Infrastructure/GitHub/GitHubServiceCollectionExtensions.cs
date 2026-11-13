using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Catalog.Infrastructure.GitHub;

/// <summary>
/// Composition of the catalog's GitHub client on top of the shared App seam: the options, typed clients, App JWT and
/// installation-token cache come from <see cref="GitHubServiceCollectionExtensions.AddGitHubApp"/>, so Catalog and every
/// other feature share one implementation and one cache.
/// </summary>
/// <remarks>
/// This extension registers no hosted service and sends nothing at start-up, so adding it to a host without a GitHub
/// App changes no behaviour.
/// </remarks>
internal static class GitHubServiceCollectionExtensions
{
    /// <summary>Registers the shared App client and the catalog's typed reads.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host configuration the options bind from.</param>
    internal static IServiceCollection AddCatalogGitHub(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddGitHubApp(configuration);

        services.TryAddTransient(provider => new GitHubClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubHttpClients.Api),
            provider.GetRequiredService<GitHubInstallationTokenSource>(),
            provider.GetRequiredService<GitHubAppTokenSource>(),
            provider.GetRequiredService<IOptions<GitHubOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<GitHubClient>>()));

        return services;
    }
}
