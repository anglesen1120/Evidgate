using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.Modules.Healing;

/// <summary>
/// Composition seam of the Healing module (08.6.3-T1, 00 §5.5): internal by design and reached through
/// <c>InternalsVisibleTo</c>.
/// </summary>
internal static class HealingModule
{
    /// <summary>Registers the module's services in the calling host.</summary>
    /// <param name="services">The host's collection.</param>
    /// <param name="configuration">The host configuration the <c>Healing</c> section is bound from (the V1 clamp).</param>
    public static IServiceCollection AddHealingModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // 08.6.3-T1 AC7: the V1 clamp — `Healing:Apply` (absent or false in every shipped configuration: V1 runs
        // shadow healing) is the only thing that lets `enabled` be set. A section of its own, like the Identity
        // module's `Identity:CustomRoles`.
        services.Configure<HealingOptions>(configuration.GetSection(HealingOptions.SectionName));
        services.TryAddSingleton<IHealingApplyPolicy, HealingApplyPolicy>();

        // The switch store is the module's only Dapper path: the tenant/project upsert and the three-row resolution
        // read (AC9). No EF write model exists — the row is configuration, written through one statement.
        services.TryAddScoped<HealingSwitchStore>();

        // 00 §5.5: the Contracts seam other modules resolve to ask "may healing act for this project?".
        services.TryAddScoped<IHealingAvailabilityResolver, HealingAvailabilityResolver>();

        // 08.6.3: the operator's only writer of the platform row, resolved by the operator ingress route.
        services.TryAddSingleton<HealingSystemSwitchWriter>();

        return services;
    }
}
