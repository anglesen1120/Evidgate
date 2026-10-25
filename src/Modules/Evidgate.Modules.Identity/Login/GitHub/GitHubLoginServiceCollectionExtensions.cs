using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Evidgate.Modules.Identity.Login.GitHub;

/// <summary>
/// The names of the two typed clients GitHub login uses. They are separate clients on purpose: the token
/// POST carries a single-use authorization code and must never be retried, while the two profile GETs are
/// idempotent and carry the one bounded retry.
/// </summary>
internal static class GitHubLoginHttpClients
{
    /// <summary>Client of the authorization-code exchange (no retry).</summary>
    internal const string TokenClient = "Evidgate.Login.GitHub.Token";

    /// <summary>Client of the two profile reads, <c>GET /user</c> and <c>GET /user/emails</c> (one retry on transport failure or 5xx).</summary>
    internal const string ApiClient = "Evidgate.Login.GitHub.Api";

    /// <summary>Name of the resilience pipeline wrapped around the API client.</summary>
    internal const string ApiPipeline = "github-api";

    /// <summary>Budget of one API attempt: the per-attempt timeout the pipeline applies inside the retry.</summary>
    internal static TimeSpan ApiAttemptTimeout(GitHubLoginOptions options) =>
        TimeSpan.FromSeconds(options.HttpTimeoutSeconds);

    /// <summary>
    /// Budget of one whole API call: two attempts plus the retry delay and a little slack. The client's
    /// own <c>Timeout</c> is infinite, so this — not a shorter client timeout — is what bounds the call,
    /// including the response body the provider reads under it.
    /// </summary>
    internal static TimeSpan ApiTotalTimeout(GitHubLoginOptions options) =>
        (ApiAttemptTimeout(options) * 2) + TimeSpan.FromSeconds(2);
}

/// <summary>
/// Composition of GitHub federated login (02.3.1-T1.3), following the Google half
/// (<c>GoogleLoginServiceCollectionExtensions</c>): options bound and validated at boot, the secret
/// resolved through a post-configurer, typed clients with a hard timeout, no redirect following and the
/// fixed S4 allowlist in front of them, and the provider registered as one of the module's
/// <see cref="ILoginProvider"/> implementations.
/// </summary>
internal static class GitHubLoginServiceCollectionExtensions
{
    /// <summary>Registers the GitHub options, the two typed clients and the provider.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host configuration the options bind from.</param>
    internal static IServiceCollection AddGitHubLogin(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<GitHubLoginOptions>()
            .Bind(configuration.GetSection(GitHubLoginOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<GitHubLoginOptions>, GitHubLoginOptionsValidator>());

        // 10.1.1-T2: the client secret is resolved through ISecretProvider after the binder (which skips
        // the internal setter) and before validation runs.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPostConfigureOptions<GitHubLoginOptions>, GitHubLoginOptionsSecretPostConfigure>());

        services.TryAddTransient<GitHubOutboundAllowlistHandler>();

        // The token exchange client: hard timeout, no redirect, allowlist only. No resilience handler —
        // the authorization code is single-use, so a retry would always fail and would burn the code.
        services
            .AddHttpClient(GitHubLoginHttpClients.TokenClient, (provider, client) =>
            {
                client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<GitHubLoginOptions>>().Value.HttpTimeoutSeconds);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // A redirect would leave the allowlisted host (S4); the client lives long, so it must still follow DNS.
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            .AddHttpMessageHandler<GitHubOutboundAllowlistHandler>();

        // The profile client: the same guards plus one bounded retry. Its time budget lives in the
        // pipeline, so the client timeout is infinite: a shorter client timeout would abort the whole
        // pipeline (including the retry) before the per-attempt timeout inside it could ever fire.
        services
            .AddHttpClient(GitHubLoginHttpClients.ApiClient, (_, client) =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            .AddHttpMessageHandler<GitHubOutboundAllowlistHandler>()
            .AddResilienceHandler(GitHubLoginHttpClients.ApiPipeline, (builder, context) =>
            {
                var options = context.ServiceProvider.GetRequiredService<IOptions<GitHubLoginOptions>>().Value;

                // Order is outermost first: the total budget wraps the retry, and the per-attempt timeout
                // sits inside it, so a slow attempt is retried once instead of cancelling the operation.
                builder.AddTimeout(GitHubLoginHttpClients.ApiTotalTimeout(options));

                // At most one retry, on the failures the strategy already classifies as transient
                // (HttpRequestException, TimeoutRejectedException, 5xx and 408): both GETs are idempotent.
                builder.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 1,
                    Delay = TimeSpan.FromMilliseconds(200),
                    BackoffType = DelayBackoffType.Constant,
                });

                builder.AddTimeout(GitHubLoginHttpClients.ApiAttemptTimeout(options));
            });

        // The provider itself needs BOTH clients, so it is not a typed client: it is built from the
        // factory, which keeps one handler pool per named client. The ILoginProvider entry is a plain
        // AddTransient because the module must contribute a SECOND provider next to Google's, and
        // TryAddEnumerable de-duplicates by implementation type — null for both factory descriptors —
        // so it would silently drop this one and leave the `github` route answering 404.
        services.AddTransient<GitHubLoginProvider>(provider =>
        {
            var clients = provider.GetRequiredService<IHttpClientFactory>();

            return new GitHubLoginProvider(
                clients.CreateClient(GitHubLoginHttpClients.TokenClient),
                clients.CreateClient(GitHubLoginHttpClients.ApiClient),
                provider.GetRequiredService<IOptions<GitHubLoginOptions>>(),
                provider.GetRequiredService<ILogger<GitHubLoginProvider>>());
        });
        services.AddTransient<ILoginProvider>(provider => provider.GetRequiredService<GitHubLoginProvider>());

        return services;
    }
}
