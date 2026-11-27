using Evidgate.Modules.Authoring.Trackers.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.Modules.Authoring.Trackers.Infrastructure;

/// <summary>Composition of the tracker connection persistence.</summary>
internal static class TrackerPersistenceExtensions
{
    /// <summary>
    /// Registers the token protector and the connection store. Both hold no per-request state (the store reads the
    /// ambient tenant scope on every call), so they are singletons like <c>ITenantDb</c>; the host supplies
    /// <c>IDataProtectionProvider</c>.
    /// </summary>
    /// <param name="services">The calling host's collection.</param>
    public static IServiceCollection AddTrackerPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<TrackerTokenProtector>();
        services.TryAddSingleton<ITrackerConnectionStore, TrackerConnectionStore>();
        return services;
    }
}
