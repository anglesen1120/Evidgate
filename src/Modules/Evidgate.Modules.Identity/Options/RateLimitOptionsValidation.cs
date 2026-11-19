using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Options;

/// <summary>
/// The module's validated option groups, materialised by the Api's boot check.
/// </summary>
/// <remarks>
/// A host materialises the limiter group before it starts, so an invalid budget exits through the catch that prints one
/// named line instead of the host's critical "failed to start" with a stack trace. The group is internal to this
/// assembly, so the host reaches it through this seam rather than through the option type.
/// </remarks>
internal static class RateLimitOptionsValidation
{
    /// <summary>
    /// Reads the surface budgets once, so a limit, window or key capacity the store cannot honour refuses the boot.
    /// </summary>
    /// <param name="services">The built host's service provider.</param>
    /// <exception cref="OptionsValidationException">A budget is invalid; the message names its key.</exception>
    public static void ValidateRateLimitOptions(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _ = services.GetRequiredService<IOptions<RateLimitOptions>>().Value;
    }
}
