using Evidgate.Modules.Healing.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Healing;

/// <summary>
/// The module's validated option groups, materialised by each host's boot check.
/// </summary>
/// <remarks>
/// A host materialises the provider group before it starts, so an invalid selection exits through the catch that prints
/// one named line instead of the host's critical "failed to start" with a stack trace. The group is internal to this
/// assembly, so the hosts reach it through this seam rather than through the option type.
/// </remarks>
internal static class HealingOptionsValidation
{
    /// <summary>
    /// Reads the selected provider once, so an unset, unknown or unusable selection refuses the boot.
    /// </summary>
    /// <param name="services">The built host's service provider.</param>
    /// <exception cref="OptionsValidationException">The selection is invalid; the message names its key.</exception>
    public static void ValidateHealingOptions(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _ = services.GetRequiredService<IOptions<HealingProviderOptions>>().Value;
    }
}
