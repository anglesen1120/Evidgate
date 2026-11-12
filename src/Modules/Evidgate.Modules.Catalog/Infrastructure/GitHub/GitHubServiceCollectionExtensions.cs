using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Evidgate.Modules.Catalog.Infrastructure.GitHub;

/// <summary>
/// The names and time budgets of the two typed clients the App client uses. They are separate on
/// purpose: the token exchange is a POST that must never be retried, while every typed read is an
/// idempotent GET that may be retried inside one bounded budget.
/// </summary>
internal static class GitHubHttpClients
{
    /// <summary>Client of the typed reads: installation, repository, trees, blobs.</summary>
    internal const string Api = "Evidgate.Catalog.GitHub.Api";

    /// <summary>Client of the installation-token exchange (no retry).</summary>
    internal const string Token = "Evidgate.Catalog.GitHub.Token";

    /// <summary>Name of the resilience pipeline wrapped around <see cref="Api"/>.</summary>
    internal const string ApiPipeline = "catalog-github-api";

    /// <summary>Attempts the API pipeline may make: one call plus two retries.</summary>
    internal const int MaxAttempts = 3;

    /// <summary>Budget of one API attempt.</summary>
    internal static TimeSpan AttemptTimeout(GitHubOptions options) =>
        TimeSpan.FromSeconds(options.HttpTimeoutSeconds);

    /// <summary>Budget of one whole API call: all attempts plus their delays and a little slack.</summary>
    internal static TimeSpan TotalTimeout(GitHubOptions options) =>
        (AttemptTimeout(options) * MaxAttempts) + TimeSpan.FromSeconds(2);
}

/// <summary>
/// Composition of the GitHub App client: options bound and validated at boot, the App JWT and the
/// installation-token cache as singletons, and two typed clients with no redirect following.
/// </summary>
/// <remarks>
/// This extension is the module's only wiring; the host calls it from its composition root next to
/// <c>AddCatalogModule</c> once the catalog sync task exists. It registers no hosted service and
/// sends nothing at start-up, so adding it to a host without a GitHub App changes no behaviour.
/// </remarks>
internal static class GitHubServiceCollectionExtensions
{
    /// <summary>Registers the options and the App client.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host configuration the options bind from.</param>
    internal static IServiceCollection AddCatalogGitHub(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<GitHubOptions>()
            .Bind(configuration.GetSection(GitHubOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<GitHubOptions>, GitHubOptionsValidator>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPostConfigureOptions<GitHubOptions>, GitHubOptionsSecretPostConfigure>());
        services.TryAddSingleton(TimeProvider.System);

        // The token exchange client: a hard timeout and no retry — the request is a POST.
        services
            .AddHttpClient(GitHubHttpClients.Token, (provider, client) =>
            {
                client.Timeout = GitHubHttpClients.AttemptTimeout(
                    provider.GetRequiredService<IOptions<GitHubOptions>>().Value);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // GitHub answers a moved endpoint with a redirect; following one would leave the pinned host.
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });

        // The read client: every call is an idempotent GET, so the pipeline owns the whole budget and
        // the client timeout is infinite — a shorter one would abort the retry before it could run.
        services
            .AddHttpClient(GitHubHttpClients.Api, (_, client) => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            .AddResilienceHandler(GitHubHttpClients.ApiPipeline, (builder, context) =>
            {
                var options = context.ServiceProvider.GetRequiredService<IOptions<GitHubOptions>>().Value;

                // Outermost first: the total budget wraps the retry, and the per-attempt timeout sits
                // inside it, so one slow attempt is retried instead of cancelling the operation.
                builder.AddTimeout(GitHubHttpClients.TotalTimeout(options));
                builder.AddRetry(new HttpRetryStrategyOptions
                {
                    // HttpRetryStrategyOptions already treats 429/503 as transient and waits for the
                    // Retry-After delay GitHub sends, which is what keeps a rate-limited sync honest.
                    MaxRetryAttempts = GitHubHttpClients.MaxAttempts - 1,
                    Delay = TimeSpan.FromMilliseconds(200),
                    BackoffType = DelayBackoffType.Exponential,
                });
                builder.AddTimeout(GitHubHttpClients.AttemptTimeout(options));
            });

        services.TryAddSingleton(provider => new GitHubAppTokenSource(
            provider.GetRequiredService<IOptions<GitHubOptions>>(),
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(provider => new GitHubInstallationTokenSource(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<GitHubAppTokenSource>(),
            provider.GetRequiredService<IOptions<GitHubOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<GitHubInstallationTokenSource>>()));

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
