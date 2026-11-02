using Microsoft.Extensions.Hosting;

namespace Evidgate.Modules.Identity.DevLogin;

/// <summary>
/// The three conditions that let the development sign-in exist: a Development host, the explicit
/// <c>EVIDGATE__DEVLOGIN__ENABLED</c> switch, and a provisioned <c>EVIDGATE__DEVLOGIN__SECRET</c>.
/// </summary>
/// <remarks>
/// The predicate is separated from the route mapping so each condition can be proven on its own: dropping any
/// one of them leaves the route unmapped and the answer 404, never 401 — a deployment cannot be talked into a
/// development sign-in by configuration alone.
/// </remarks>
internal static class DevSessionGate
{
    /// <summary>Whether <c>POST /v1/dev/session</c> may be mapped for this host.</summary>
    /// <param name="environment">The host environment the process reports.</param>
    /// <param name="options">The bound development sign-in options.</param>
    internal static bool IsOpen(IHostEnvironment environment, DevLoginOptions options)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);

        return environment.IsDevelopment()
            && options.Enabled
            && !string.IsNullOrWhiteSpace(options.Secret);
    }
}
