using Evidgate.Modules.Orchestrator.Application.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Orchestrator;

/// <summary>
/// The module's validated option groups, materialised by each host's boot check.
/// </summary>
/// <remarks>
/// A host materialises every validated group before it starts, so an invalid value exits through the catch that prints one
/// named line instead of the host's critical "failed to start" with a stack trace. The cancel windows are internal to this
/// assembly, so the hosts reach them through this seam rather than through the option type.
/// </remarks>
internal static class OrchestratorOptionsValidation
{
    /// <summary>
    /// Reads the cancel windows once, so a missing, zero, negative or inverted window refuses the boot.
    /// </summary>
    /// <param name="services">The built host's service provider.</param>
    /// <exception cref="OptionsValidationException">A window is invalid; the message names its key.</exception>
    public static void ValidateOrchestratorOptions(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _ = services.GetRequiredService<IOptions<RunCancelOptions>>().Value;
    }
}
