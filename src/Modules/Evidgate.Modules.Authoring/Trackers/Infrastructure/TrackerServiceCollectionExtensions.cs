using Evidgate.Modules.Authoring.Trackers.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Authoring.Trackers.Infrastructure;

/// <summary>Composition of the tracker connections: options, the guarded provider client, the callback and the store.</summary>
internal static class TrackerServiceCollectionExtensions
{
    /// <summary>
    /// A provider call that has not answered by then is refused; the callback holds the browser's request open for it, so
    /// it stays short. Redirects are off and every host is checked by <see cref="TrackerOutboundAllowlistHandler"/>.
    /// </summary>
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Registers the tracker connections. A provider with no client id or secret stays unavailable (its connect route
    /// answers 409); a malformed redirect URI stops the host at start.
    /// </summary>
    /// <param name="services">The calling host's collection.</param>
    public static IServiceCollection AddTrackerConnections(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<JiraCloudTrackerOptions>().BindConfiguration(JiraCloudTrackerOptions.SectionName).ValidateOnStart();
        services.AddOptions<LinearTrackerOptions>().BindConfiguration(LinearTrackerOptions.SectionName).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<JiraCloudTrackerOptions>, TrackerOAuthOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<LinearTrackerOptions>, TrackerOAuthOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<JiraCloudTrackerOptions>, TrackerOAuthOptionsSecretPostConfigure>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<LinearTrackerOptions>, TrackerOAuthOptionsSecretPostConfigure>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddTransient<TrackerOutboundAllowlistHandler>();
        services.AddHttpClient<ITrackerProviderClient, TrackerProviderClient>(client => client.Timeout = ProviderTimeout)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            .AddHttpMessageHandler<TrackerOutboundAllowlistHandler>();

        services.TryAddScoped<TrackerCallbackService>();

        return services.AddTrackerPersistence();
    }
}
