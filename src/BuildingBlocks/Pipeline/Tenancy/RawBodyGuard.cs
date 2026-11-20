using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Evidgate.BuildingBlocks.Pipeline.Tenancy;

/// <summary>
/// The one check every raw request-body read sits behind: a caller the host did not resolve to a principal AND a
/// tenant is refused with the pipeline's own 401 (<see cref="TenantContextMissingException"/>, rendered by the
/// envelope middleware) before a byte of the body is buffered.
/// </summary>
/// <remarks>
/// A session with an unreadable organization header is published as a principal but resolves no tenant, so a
/// principal-only test lets it through to a body read the pipeline refuses afterwards. Permission is NOT checked
/// here: it runs inside the Mediator behaviour, so an authenticated caller without it still gets body validation first.
/// </remarks>
public static class RawBodyGuard
{
    /// <summary>
    /// Refuses a request that has no principal or no tenant. A route that must keep its own anonymous answer tests
    /// its principal first and calls <see cref="RequireResolvedTenant"/> after it.
    /// </summary>
    /// <param name="services">The request's service provider (<c>HttpContext.RequestServices</c>).</param>
    /// <exception cref="TenantContextMissingException">No principal, or no tenant, is published for the request.</exception>
    public static void RequireAuthenticatedTenant(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.GetRequiredService<IRequestPrincipalAccessor>().Current is null)
        {
            throw new TenantContextMissingException();
        }

        RequireResolvedTenant(services);
    }

    /// <summary>Refuses a request whose host resolved no tenant.</summary>
    /// <param name="services">The request's service provider (<c>HttpContext.RequestServices</c>).</param>
    /// <exception cref="TenantContextMissingException">No tenant is published for the request.</exception>
    public static void RequireResolvedTenant(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.GetRequiredService<IRequestTenantContextAccessor>().Current is null)
        {
            throw new TenantContextMissingException();
        }
    }
}
