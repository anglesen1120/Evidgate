using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Evidgate.BuildingBlocks.SharedKernel.GitHub;

/// <summary>
/// Composition of the one GitHub App: options bound and validated at boot, the App JWT and the installation-token
/// cache as singletons, and the typed clients with no redirect following. Every module that talks to the App calls
/// this once; the registrations are idempotent so two modules in one host share one cache and one JWT source.
/// </summary>
public static class GitHubServiceCollectionExtensions
{
    /// <summary>Registers the options, the clients and the two token sources.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host configuration the options bind from.</param>
    public static IServiceCollection AddGitHubApp(this IServiceCollection services, IConfiguration configuration)
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
        services.AddMetrics();

        // The token and OAuth exchanges: a hard timeout and no retry — both requests are POSTs.
        services
            .AddHttpClient(GitHubHttpClients.Token, (provider, client) =>
            {
                client.Timeout = GitHubHttpClients.AttemptTimeout(
                    provider.GetRequiredService<IOptions<GitHubOptions>>().Value);
            })
            .ConfigurePrimaryHttpMessageHandler(BareHandler);

        services
            .AddHttpClient(GitHubHttpClients.OAuth, (provider, client) =>
            {
                client.Timeout = GitHubHttpClients.AttemptTimeout(
                    provider.GetRequiredService<IOptions<GitHubOptions>>().Value);
            })
            .ConfigurePrimaryHttpMessageHandler(BareHandler);

        // The read client: every call is an idempotent GET, so the pipeline owns the whole budget and the client
        // timeout is infinite — a shorter one would abort the retry before it could run.
        services
            .AddHttpClient(GitHubHttpClients.Api, (_, client) => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(BareHandler)
            .AddResilienceHandler(GitHubHttpClients.ApiPipeline, (builder, context) =>
            {
                var options = context.ServiceProvider.GetRequiredService<IOptions<GitHubOptions>>().Value;

                // Outermost first: the total budget wraps the retry, and the per-attempt timeout sits
                // inside it, so one slow attempt is retried instead of cancelling the operation.
                builder.AddTimeout(GitHubHttpClients.TotalTimeout(options));
                builder.AddRetry(new HttpRetryStrategyOptions
                {
                    // Rate-limit responses belong to the caller's scheduler, not this bounded call.
                    ShouldHandle = args => ValueTask.FromResult(
                        args.Outcome.Result?.StatusCode != HttpStatusCode.TooManyRequests
                        && HttpClientResiliencePredicates.IsTransient(args.Outcome)),
                    MaxRetryAttempts = GitHubHttpClients.MaxAttempts - 1,
                    Delay = TimeSpan.FromMilliseconds(200),
                    BackoffType = DelayBackoffType.Exponential,
                });
                builder.AddTimeout(GitHubHttpClients.AttemptTimeout(options));
            });

        services.TryAddSingleton(provider => new GitHubAppTokenSource(
            provider.GetRequiredService<IOptions<GitHubOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<GitHubAppTokenSource>>()));

        services.TryAddSingleton(provider => new GitHubInstallationTokenSource(
            provider.GetRequiredService<IMeterFactory>(),
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<GitHubAppTokenSource>(),
            provider.GetRequiredService<IOptions<GitHubOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<GitHubInstallationTokenSource>>(),
            provider.GetService<IGitHubInstallationResolver>()));

        services.TryAddSingleton<IGitHubInstallationTokens>(
            provider => provider.GetRequiredService<GitHubInstallationTokenSource>());

        return services;
    }

    private static SocketsHttpHandler BareHandler() => new()
    {
        // GitHub answers a moved endpoint with a redirect; following one would leave the pinned host.
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };
}
