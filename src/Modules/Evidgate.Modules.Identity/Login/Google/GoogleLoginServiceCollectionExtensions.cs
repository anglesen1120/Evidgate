using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Evidgate.Modules.Identity.Login.Google;

/// <summary>
/// The names of the two typed clients Google login uses. They are separate clients on purpose: the
/// token POST carries a single-use authorization code and must never be retried, while the JWKS GET is
/// idempotent and carries the one bounded retry.
/// </summary>
internal static class GoogleLoginHttpClients
{
    /// <summary>Client of the authorization-code exchange (no retry).</summary>
    internal const string TokenClient = "Evidgate.Login.Google.Token";

    /// <summary>Client of the JWKS fetch (one retry on transport failure or 5xx).</summary>
    internal const string JwksClient = "Evidgate.Login.Google.Jwks";

    /// <summary>Name of the resilience pipeline wrapped around the JWKS client.</summary>
    internal const string JwksPipeline = "google-jwks";

    /// <summary>Budget of one JWKS attempt: the per-attempt timeout the pipeline applies inside the retry.</summary>
    internal static TimeSpan JwksAttemptTimeout(GoogleLoginOptions options) =>
        TimeSpan.FromSeconds(options.HttpTimeoutSeconds);

    /// <summary>
    /// Budget of one whole JWKS fetch: two attempts plus the retry delay and a little slack. The client's
    /// own <c>Timeout</c> is infinite, so this — not a shorter client timeout — is what bounds the call.
    /// </summary>
    internal static TimeSpan JwksTotalTimeout(GoogleLoginOptions options) =>
        (JwksAttemptTimeout(options) * 2) + TimeSpan.FromSeconds(2);
}

/// <summary>
/// Composition of Google federated login (02.3.1-T1.2), following the CI authentication seam
/// (<c>IdentityModule.AddCiAuthentication</c>): options bound and validated at boot, the secret resolved
/// through a post-configurer, a typed client with a hard timeout, no redirect following and the fixed S4
/// allowlist in front of it, and the provider registered as one of the module's
/// <c>ILoginProvider</c> implementations.
/// </summary>
internal static class GoogleLoginServiceCollectionExtensions
{
    /// <summary>Registers the Google options, the typed clients, the JWKS cache and the provider.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="configuration">The host configuration the options bind from.</param>
    internal static IServiceCollection AddGoogleLogin(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<GoogleLoginOptions>()
            .Bind(configuration.GetSection(GoogleLoginOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<GoogleLoginOptions>, GoogleLoginOptionsValidator>());

        // 10.1.1-T2: the client secret is resolved through ISecretProvider after the binder (which skips
        // the internal setter) and before validation runs.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPostConfigureOptions<GoogleLoginOptions>, GoogleLoginOptionsSecretPostConfigure>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddTransient<GoogleOutboundAllowlistHandler>();

        // The token exchange client: hard timeout, no redirect, allowlist only. No resilience handler —
        // the authorization code is single-use, so a retry would always fail and would burn the code.
        // Registering it as <ILoginProvider, GoogleLoginProvider> makes this one call both the typed
        // client and the module's provider entry, so the federated composition's IEnumerable<ILoginProvider>
        // resolves it without a second descriptor (which TryAddEnumerable would reject for a factory).
        services
            .AddHttpClient<ILoginProvider, GoogleLoginProvider>(GoogleLoginHttpClients.TokenClient, (provider, client) =>
            {
                client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<GoogleLoginOptions>>().Value.HttpTimeoutSeconds);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // A redirect would leave the allowlisted host (S4); the client lives long, so it must still follow DNS.
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            .AddHttpMessageHandler<GoogleOutboundAllowlistHandler>();

        // The JWKS client: the same guards plus one bounded retry. Its time budget lives in the pipeline,
        // so the client timeout is infinite: a shorter client timeout would abort the whole pipeline
        // (including the retry) before the per-attempt timeout inside it could ever fire.
        services
            .AddHttpClient(GoogleLoginHttpClients.JwksClient, (_, client) =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            .AddHttpMessageHandler<GoogleOutboundAllowlistHandler>()
            .AddResilienceHandler(GoogleLoginHttpClients.JwksPipeline, (builder, context) =>
            {
                var options = context.ServiceProvider.GetRequiredService<IOptions<GoogleLoginOptions>>().Value;

                // Order is outermost first: the total budget wraps the retry, and the per-attempt timeout
                // sits inside it, so a slow attempt is retried once instead of cancelling the operation.
                builder.AddTimeout(GoogleLoginHttpClients.JwksTotalTimeout(options));

                // At most one retry, on the failures the strategy already classifies as transient
                // (HttpRequestException, TimeoutRejectedException, 5xx and 408): a JWKS GET is idempotent.
                builder.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 1,
                    Delay = TimeSpan.FromMilliseconds(200),
                    BackoffType = DelayBackoffType.Constant,
                });

                builder.AddTimeout(GoogleLoginHttpClients.JwksAttemptTimeout(options));
            });

        // A singleton: the cache owns the in-flight fetch, and one instance is one single flight.
        services.TryAddSingleton<IGoogleJwksProvider>(provider => new HttpGoogleJwksCache(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(GoogleLoginHttpClients.JwksClient),
            provider.GetRequiredService<IOptions<GoogleLoginOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<HttpGoogleJwksCache>>()));

        services.TryAddSingleton<GoogleIdTokenValidator>();

        return services;
    }
}
