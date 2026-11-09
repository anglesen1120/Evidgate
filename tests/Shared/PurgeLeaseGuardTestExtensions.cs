using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.Testing;

/// <summary>Test hosts that shorten the task lease to expire it on command drop the purge-budget guard.</summary>
public static class PurgeLeaseGuardTestExtensions
{
    /// <summary>Removes <see cref="PurgeLeaseGuardValidator"/>; every other options check stays registered.</summary>
    public static IServiceCollection WithoutPurgeLeaseGuard(this IServiceCollection services)
    {
        foreach (var guard in services.Where(d => d.ImplementationType == typeof(PurgeLeaseGuardValidator)).ToList())
        {
            services.Remove(guard);
        }

        return services;
    }
}
